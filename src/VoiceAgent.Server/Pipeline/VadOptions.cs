namespace VoiceAgent.Server.Pipeline;

public sealed class VadOptions
{
    public float Threshold { get; set; } = 0.5f;
    public int MinSpeechMs { get; set; } = 250;
    public int MinSilenceMs { get; set; } = 600;
    public int PreRollMs { get; set; } = 150;
}

/// <summary>A start has no PCM; the end carries the whole utterance including pre-roll.</summary>
public sealed record SegmentEvent(bool IsStart, short[]? Pcm, long SampleIndex);

/// <summary>Per-push result: events plus the decision for the latest window, which barge-in reads.</summary>
public sealed record SegmenterFrame(IReadOnlyList<SegmentEvent> Events, float Probability, bool IsSpeech);
