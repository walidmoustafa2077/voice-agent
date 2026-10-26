namespace VoiceAgent.Server.Options;

/// <summary>Everything the page may inspect and tune. In-memory only; no persistence.</summary>
public sealed record VoiceConfig
{
    public float VadThreshold { get; init; } = 0.5f;
    public float SpeakingVadThreshold { get; init; } = 0.6f;
    public int MinSpeechMs { get; init; } = 250;
    public int MinSilenceMs { get; init; } = 800;
    public int PreRollMs { get; init; } = 150;
    public int MaxChunkChars { get; init; } = 90;
    public int MinChunkChars { get; init; } = 20;
    public int MaxHistoryMessages { get; init; } = 40;
}

public sealed class VoiceConfigStore
{
    private VoiceConfig _current = new();

    public VoiceConfig Current => Volatile.Read(ref _current);

    public VoiceConfig Replace(VoiceConfig next)
    {
        Volatile.Write(ref _current, next);
        return next;
    }
}
