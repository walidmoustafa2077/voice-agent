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

// A fresh clone has no models at all. Fetch whatever this configuration needs before anything
// tries to open a file; EnsureAsync skips every file that is already complete, so a warm start
// is just a handful of stat calls and nothing else.
var required = VoiceAgent.Server.ModelDownloader.Required;
var missing = required.Where(model => !model.IsReady).ToList();

if (missing.Count == 0)
{
    logger.LogInformation("all {Count} speech model files present", required.Count);
}
else
{
    logger.LogInformation(
        "first run: {Missing} of {Count} speech model files missing, downloading into {Root}",
        missing.Count, required.Count, VoiceAgent.Server.ModelPaths.Root);

    for (var i = 0; i < missing.Count; i++)
    {
        var model = missing[i];
        logger.LogInformation("({Index}/{Count}) {Name} -> {Path}",
            i + 1, missing.Count, model.Name, model.Path);
        try
        {
            await VoiceAgent.Server.ModelDownloader.EnsureAsync(
                model, models, logger, CancellationToken.None);
        }
        catch (Exception error)
        {
            logger.LogError(error,
                "{Name} could not be downloaded. Check the connection and start again; the partial " +
                "file was removed, so the next attempt restarts this model from the beginning.",
                model.Name);
            throw;
        }
    }
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

        // Connection warm-up, not filler: the first request to the cloud model pays DNS/TLS and a
        // cold queue slot (measured at ~2.8 s) that would otherwise be charged to the first turn.
        var llmWarm = app.Services.GetRequiredService<VoiceAgent.Server.Pipeline.LlmService>();
        var llmClock = System.Diagnostics.Stopwatch.StartNew();
        await foreach (var ignored in llmWarm.StreamAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hi")],
            CancellationToken.None))
        {
            break; // the reply is discarded; the round trip is what warms the connection
        }
        llmClock.Stop();
        warmupLog.LogInformation(
            "llm connection warm in {Elapsed} ms", (long)llmClock.Elapsed.TotalMilliseconds);
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
