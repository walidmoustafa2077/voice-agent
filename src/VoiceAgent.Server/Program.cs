var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 1_048_576;
}).AddMessagePackProtocol();

builder.Services.AddSingleton(new Lazy<VoiceAgent.Server.Pipeline.ISttService>(
    VoiceAgent.Server.Pipeline.SttFactory.Create));

builder.Services.AddSingleton(new VoiceAgent.Server.Pipeline.LlmService(
    new OllamaSharp.OllamaApiClient(
        new Uri("http://localhost:11434"),
        "gemma4:31b-cloud")));

builder.Services.AddSingleton(new Lazy<VoiceAgent.Server.Pipeline.ITtsService>(
    VoiceAgent.Server.Pipeline.TtsFactory.Create));

builder.Services.AddSingleton<VoiceAgent.Server.Options.VoiceConfigStore>();

var app = builder.Build();

var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ModelDownloader");
using var models = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

foreach (var model in VoiceAgent.Server.ModelDownloader.Required)
{
    await VoiceAgent.Server.ModelDownloader.EnsureAsync(
        model, models, logger, CancellationToken.None);
}

// Pay the ONNX graph construction and first-inference cost once, in the background, so the
// first real turn is not the one that warms Whisper and the speech engine.
var warmupLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Warmup");

// One line on the record: every speech stage shares this CPU. Streaming ASR would run while the
// user speaks, when nothing else is busy, but a Thinking-time merge or the Whisper fallback can
// overlap synthesis and slow both.
warmupLog.LogInformation(
    "cpu: {Cores} logical cores - Whisper, TTS and VAD share them; " +
    "expect STT and synthesis to slow when a merge overlaps them",
    Environment.ProcessorCount);

_ = Task.Run(async () =>
{
    try
    {
        var ttsWarm = app.Services.GetRequiredService<Lazy<VoiceAgent.Server.Pipeline.ITtsService>>().Value;
        var sttWarm = app.Services.GetRequiredService<Lazy<VoiceAgent.Server.Pipeline.ISttService>>().Value;
        await ttsWarm.SynthesizeAsync("Ready.", CancellationToken.None);

        if (VoiceAgent.Server.Pipeline.SttFactory.Mode == VoiceAgent.Server.Pipeline.SttMode.Streaming)
        {
            // Pinned-pairing guard: sherpa-onnx ships its own onnxruntime.dll and the build
            // resolves that collision in Microsoft's favour. Both native stacks loading in one
            // process is the property we rely on, so fail loudly here rather than mid-turn.
            using var vadPair = new VoiceAgent.Server.Pipeline.SileroVadModel(
                VoiceAgent.Server.ModelPaths.SileroVad);
            warmupLog.LogInformation("stt self-check: Silero VAD and sherpa-onnx coexist");
        }

        // Constructing SttService only loads the model; Whisper's first inference is the real
        // cost. Silence is short-circuited before Whisper runs, so feed a short non-silent tone.
        var tone = new short[16000];
        for (var i = 0; i < tone.Length; i++)
            tone[i] = (short)(6000 * Math.Sin(2 * Math.PI * 220 * i / 16000.0));
        await sttWarm.TranscribeAsync(tone, CancellationToken.None);

        warmupLog.LogInformation("speech models warm");
    }
    catch (Exception ex)
    {
        warmupLog.LogWarning(ex, "warm-up failed; the first turn will be slower");
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapHub<VoiceAgent.Server.Hubs.VoiceHub>("/hub/voice");
VoiceAgent.Server.ConfigEndpoints.MapConfig(app);
app.MapFallbackToFile("index.html");

app.Run();
