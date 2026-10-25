using VoiceAgent.Server.Sessions;

namespace VoiceAgent.Server.Contracts;

public abstract record VoiceEvent;

/// <summary>The live VAD threshold travels with the state so the HUD shows what is in effect.</summary>
public sealed record StateChanged(VoiceState State, float VadThreshold) : VoiceEvent;

/// <summary>Sent at speech end. The client times latency from this event's arrival.</summary>
public sealed record TurnStarted(int TurnId, DateTimeOffset At) : VoiceEvent;

/// <summary>Server-side stage latency, surfaced in the HUD. One clock: the server's.</summary>
public sealed record StageTiming(string Stage, double Ms) : VoiceEvent;

/// <summary>Carries the turn id so a transcript that raced a cancel cannot create a phantom bubble.</summary>
public sealed record Transcript(int TurnId, string Text) : VoiceEvent;

/// <summary>Carries the turn id so a delta that raced a cancel cannot land in the next bubble.</summary>
public sealed record AssistantDelta(int TurnId, string Text) : VoiceEvent;

/// <summary>Carries the turn id so a chunk that raced a cancel cannot be played by the next turn.</summary>
public sealed record AudioChunk(int TurnId, int Index, byte[] Wav, double DurationMs) : VoiceEvent;

/// <summary>
/// <paramref name="LastPlayedIndex"/> is -1 when nothing was heard. <paramref name="HeardText"/>
/// is the sentences that were actually played, or null when none were.
/// <paramref name="RemoveLastUser"/> tells the client to drop the pending user bubble of a merged
/// turn (the merged turn will send the combined transcript).
/// </summary>
public sealed record TurnCancelled(
    int TurnId,
    string Reason,
    int LastPlayedIndex,
    string? HeardText,
    bool RemoveLastUser) : VoiceEvent;

public sealed record PipelineError(string Message) : VoiceEvent;
