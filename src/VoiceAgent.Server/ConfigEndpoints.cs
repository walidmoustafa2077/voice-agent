using System.Text.Json;
using VoiceAgent.Server.Options;

namespace VoiceAgent.Server;

public static class ConfigEndpoints
{
    public static void MapConfig(this WebApplication app)
    {
        app.MapGet("/api/config", (VoiceConfigStore store) =>
        {
            var c = store.Current;

            return Results.Ok(new
            {
                c.VadThreshold,
                c.SpeakingVadThreshold,
                c.MinSpeechMs,
                c.MinSilenceMs,
                c.PreRollMs,
                c.MaxChunkChars,
                c.MinChunkChars,
                c.MaxHistoryMessages,
                models = new
                {
                    stt = ModelPaths.WhisperTinyEn,
                    vad = ModelPaths.SileroVad,
                    tts = ModelPaths.Kokoro,
                    llm = "gemma4:31b-cloud @ http://localhost:11434",
                },
                browser = "Chrome or Edge (AEC quality is the interaction)",
            });
        });

        app.MapPost("/api/config", (Dictionary<string, JsonElement> patch, VoiceConfigStore store) =>
        {
            var c = store.Current;

            foreach (var (key, value) in patch)
            {
                c = key switch
                {
                    "vadThreshold" => c with { VadThreshold = value.GetSingle() },
                    "speakingVadThreshold" => c with { SpeakingVadThreshold = value.GetSingle() },
                    "minSpeechMs" => c with { MinSpeechMs = value.GetInt32() },
                    "minSilenceMs" => c with { MinSilenceMs = value.GetInt32() },
                    "preRollMs" => c with { PreRollMs = value.GetInt32() },
                    "maxChunkChars" => c with { MaxChunkChars = value.GetInt32() },
                    "minChunkChars" => c with { MinChunkChars = value.GetInt32() },
                    "maxHistoryMessages" => c with { MaxHistoryMessages = value.GetInt32() },
                    _ => throw new BadHttpRequestException($"unknown config key '{key}'"),
                };
            }

            return Results.Ok(store.Replace(c));
        });
    }
}
