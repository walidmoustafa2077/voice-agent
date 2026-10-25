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
                try
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
                catch (Exception ex) when (ex is InvalidOperationException or FormatException)
                {
                    throw new BadHttpRequestException($"invalid value for '{key}': {ex.Message}");
                }
            }

            if (Validate(c) is { } error)
                throw new BadHttpRequestException(error);

            return Results.Ok(store.Replace(c));
        });
    }

    private static string? Validate(VoiceConfig c)
    {
        if (c.VadThreshold is < 0.05f or > 0.95f)
            return "vadThreshold must be between 0.05 and 0.95";
        if (c.SpeakingVadThreshold is < 0.05f or > 0.99f)
            return "speakingVadThreshold must be between 0.05 and 0.99";
        if (c.MinSpeechMs is < 50 or > 2000)
            return "minSpeechMs must be between 50 and 2000";
        if (c.MinSilenceMs is < 100 or > 3000)
            return "minSilenceMs must be between 100 and 3000";
        if (c.PreRollMs is < 0 or > 500)
            return "preRollMs must be between 0 and 500";
        if (c.MaxChunkChars is < 20 or > 400)
            return "maxChunkChars must be between 20 and 400";
        if (c.MinChunkChars < 1 || c.MinChunkChars > c.MaxChunkChars)
            return "minChunkChars must be between 1 and maxChunkChars";
        if (c.MaxHistoryMessages is < 2 or > 200)
            return "maxHistoryMessages must be between 2 and 200";

        return null;
    }
}
