using VoiceAgent.Server.Sessions;

namespace VoiceAgent.Server.Contracts;

public abstract record VoiceEvent;

public sealed record StateChanged(VoiceState State) : VoiceEvent;

/// <summary>Sent at speech end. The client times latency from this event's arrival.</summary>
public sealed record TurnStarted(int TurnId, DateTimeOffset At) : VoiceEvent;

/// <summary>Server-side stage latency, surfaced in the HUD. One clock: the server's.</summary>
public sealed record StageTiming(string Stage, double Ms) : VoiceEvent;

public sealed record Transcript(string Text) : VoiceEvent;

public sealed record AssistantDelta(string Text) : VoiceEvent;

public sealed record AudioChunk(int Index, byte[] Wav, double DurationMs) : VoiceEvent;

public sealed record TurnCancelled(string Reason) : VoiceEvent;

public sealed record PipelineError(string Message) : VoiceEvent;
