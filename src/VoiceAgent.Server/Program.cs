var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 1_048_576;
}).AddMessagePackProtocol();

builder.Services.AddSingleton(new Lazy<VoiceAgent.Server.Pipeline.SttService>(
    () => new VoiceAgent.Server.Pipeline.SttService(VoiceAgent.Server.ModelPaths.WhisperTinyEn)));

builder.Services.AddSingleton(new VoiceAgent.Server.Pipeline.LlmService(
    new OllamaSharp.OllamaApiClient(
        new Uri("http://localhost:11434"),
        "gemma4:31b-cloud")));

builder.Services.AddSingleton(new Lazy<VoiceAgent.Server.Pipeline.ITtsService>(
    () => new VoiceAgent.Server.Pipeline.TtsService(VoiceAgent.Server.ModelPaths.Kokoro)));

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
// first real turn is not the one that warms Whisper and Kokoro up.
var warmupLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Warmup");
_ = Task.Run(async () =>
{
    try
    {
        var ttsWarm = app.Services.GetRequiredService<Lazy<VoiceAgent.Server.Pipeline.ITtsService>>().Value;
        var sttWarm = app.Services.GetRequiredService<Lazy<VoiceAgent.Server.Pipeline.SttService>>().Value;
        await ttsWarm.SynthesizeAsync("Ready.", CancellationToken.None);

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
