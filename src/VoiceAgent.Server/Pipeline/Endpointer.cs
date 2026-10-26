namespace VoiceAgent.Server.Pipeline;

/// <summary>
/// Decides when an utterance is over while the recognizer is still receiving audio.
///
/// The two conditions run in parallel: Silero has to be quiet for <see cref="SilenceMs"/> and the
/// recognizer's text has to be unchanged for <see cref="StabilityMs"/>. They are not chained, so
/// the earliest possible endpoint is ~<see cref="SilenceMs"/>, not their sum: text that went
/// stable while the user was still talking only has to wait out the silence.
/// </summary>
public sealed class Endpointer
{
    /// <summary>
    /// Silence before an endpoint. Deliberately close to the VAD segmenter's own silence window:
    /// a shorter one cuts the user off at an ordinary mid-sentence pause. The win comes from
    /// decoding during speech and only paying the final flush here, not from undercutting VAD.
    /// </summary>
    public const int SilenceMs = 500;
    public const int StabilityMs = 350;
    /// <summary>
    /// Force a finalize after this much continuous speech. It sits at the segmenter's own buffer
    /// bound, because streaming decode costs the same on a long utterance and a lower cap only
    /// risks splitting a long question into two turns.
    /// </summary>
    public const int MaxUtteranceMs = 30_000;

    private readonly int _silenceThreshold;
    private readonly int _stabilityThreshold;
    private readonly int _maxMs;
    private int _silenceMs;
    private int _stableMs;
    private int _totalMs;
    private string _lastText = "";

    public Endpointer(
        int silenceMs = SilenceMs,
        int stabilityMs = StabilityMs,
        int maxUtteranceMs = MaxUtteranceMs)
    {
        _silenceThreshold = silenceMs;
        _stabilityThreshold = stabilityMs;
        _maxMs = maxUtteranceMs;
    }

    /// <summary>True once any speech frame has been seen; silence before that never endpoints.</summary>
    public bool HasSpeech { get; private set; }

    /// <summary>
    /// Returns true exactly once the utterance should be finalized. <paramref name="isSpeech"/>
    /// is the VAD verdict for this frame and <paramref name="text"/> is the recognizer's current
    /// hypothesis (empty while it has nothing yet).
    /// </summary>
    public bool Push(bool isSpeech, string text, int frameMs)
    {
        _totalMs += frameMs;

        if (isSpeech)
        {
            HasSpeech = true;
            _silenceMs = 0;
        }
        else if (HasSpeech)
        {
            _silenceMs += frameMs;
        }

        if (text != _lastText)
        {
            _lastText = text;
            _stableMs = 0;
        }
        else
        {
            _stableMs += frameMs;
        }

        if (!HasSpeech) return false;

        // A user who never stops, or a recognizer that never settles, must not hold the turn open
        // forever: force a finalize and let the pipeline start a fresh stream for the rest.
        if (_totalMs >= _maxMs) return true;

        return _silenceMs >= _silenceThreshold && _stableMs >= _stabilityThreshold;
    }

    public void Reset()
    {
        _silenceMs = 0;
        _stableMs = 0;
        _totalMs = 0;
        _lastText = "";
        HasSpeech = false;
    }
}
