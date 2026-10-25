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

var app = builder.Build();

var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ModelDownloader");
using var models = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

foreach (var model in VoiceAgent.Server.ModelDownloader.Required)
{
    await VoiceAgent.Server.ModelDownloader.EnsureAsync(
        model, models, logger, CancellationToken.None);
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapHub<VoiceAgent.Server.Hubs.VoiceHub>("/hub/voice");
app.MapFallbackToFile("index.html");

app.Run();
