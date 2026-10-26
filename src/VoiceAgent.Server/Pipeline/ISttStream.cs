namespace VoiceAgent.Server.Pipeline;

/// <summary>What a recognizer knows about the utterance so far.</summary>
/// <param name="Text">Cleaned hypothesis. Empty until the recognizer emits one.</param>
/// <param name="ChangeSeq">Increments whenever <paramref name="Text"/> changes.</param>
/// <param name="Endpoint">The recognizer itself believes the utterance ended.</param>
public readonly record struct SttSnapshot(string Text, int ChangeSeq, bool Endpoint);

/// <summary>
/// One utterance's recognizer state. Audio is pushed frame by frame while the user speaks;
/// <see cref="FinalizeAsync"/> is the only call that must wait for the end of the utterance.
/// </summary>
public interface ISttStream : IDisposable
{
    void Accept(ReadOnlySpan<short> pcm16k);

    SttSnapshot Current { get; }

    Task<SttResult> FinalizeAsync(CancellationToken ct);
}

public interface IStreamingStt : IDisposable
{
    ISttStream Begin();
}
