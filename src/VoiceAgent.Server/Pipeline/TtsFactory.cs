using System.Runtime.Versioning;

namespace VoiceAgent.Server.Pipeline;

public enum TtsEngine
{
    Sapi,
    Kokoro,
}

/// <summary>
/// Chooses the speech engine once at startup. SAPI is the default on Windows because it costs
/// almost no CPU (measured ~0.01x realtime vs Kokoro's ~1.1x); Kokoro stays available for voice
/// quality with <c>VOICEAGENT_TTS=kokoro</c>.
/// </summary>
public static class TtsFactory
{
    public static TtsEngine Engine { get; } = Resolve();

    public static bool IsKokoro => Engine == TtsEngine.Kokoro;

    public static string Description => Engine == TtsEngine.Kokoro
        ? "Kokoro af_heart (local ONNX)"
        : "Windows SAPI (built-in offline voice)";

    public static ITtsService Create()
    {
        if (IsKokoro || !OperatingSystem.IsWindows())
            return new TtsService(ModelPaths.Kokoro, threads: 3);

        return CreateSapi();
    }

    [SupportedOSPlatform("windows")]
    private static ITtsService CreateSapi() =>
        new SapiTtsService(Environment.GetEnvironmentVariable("VOICEAGENT_SAPI_VOICE"));

    private static TtsEngine Resolve()
    {
        var requested = Environment.GetEnvironmentVariable("VOICEAGENT_TTS");

        if (string.Equals(requested, "kokoro", StringComparison.OrdinalIgnoreCase))
            return TtsEngine.Kokoro;

        if (string.Equals(requested, "sapi", StringComparison.OrdinalIgnoreCase))
            return OperatingSystem.IsWindows() ? TtsEngine.Sapi : TtsEngine.Kokoro;

        return OperatingSystem.IsWindows() ? TtsEngine.Sapi : TtsEngine.Kokoro;
    }
}
