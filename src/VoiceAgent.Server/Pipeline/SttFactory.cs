namespace VoiceAgent.Server.Pipeline;

public enum SttMode
{
    /// <summary>Feed audio while the user speaks and finalize at the endpoint.</summary>
    Streaming,

    /// <summary>One whole-utterance Whisper call, exactly as before streaming existed.</summary>
    WholeUtterance,
}

/// <summary>
/// Selects the recognizer once per process. <c>VOICEAGENT_STT=whisper</c> (or
/// <c>wholeutterance</c>) is the escape hatch; streaming is the default.
/// </summary>
public static class SttFactory
{
    public static SttMode Mode { get; } = Resolve();

    public static string Description => Mode == SttMode.WholeUtterance
        ? "Whisper tiny.en (whole utterance)"
        : "Whisper tiny.en (buffered streaming interface)";

    public static ISttService Create() => Mode == SttMode.WholeUtterance
        ? new SttService(ModelPaths.WhisperTinyEn)
        : new BufferedWhisperStt(new SttService(ModelPaths.WhisperTinyEn));

    private static SttMode Resolve()
    {
        var value = Environment.GetEnvironmentVariable("VOICEAGENT_STT")?.Trim().ToLowerInvariant();

        return value is "whisper" or "wholeutterance" or "whole-utterance"
            ? SttMode.WholeUtterance
            : SttMode.Streaming;
    }
}
