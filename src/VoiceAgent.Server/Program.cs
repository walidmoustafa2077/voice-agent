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

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapHub<VoiceAgent.Server.Hubs.VoiceHub>("/hub/voice");

app.Run();
