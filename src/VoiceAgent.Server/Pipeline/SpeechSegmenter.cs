namespace VoiceAgent.Server.Pipeline;

/// <summary>
/// Turns 16 kHz PCM into utterances. Owns the single utterance buffer: pre-roll plus speech,
/// which barge-in reuses instead of keeping a separate ring buffer.
/// </summary>
public sealed class SpeechSegmenter
{
    private const int WindowSamples = 512;
    private const int WindowMs = 32;
    private const int MaxUtteranceSamples = 15 * 16000;

    private readonly IVadModel _vad;
    private readonly VadOptions _options;
    private readonly List<short> _buffer = [];
    private readonly Queue<short> _preRoll = new();
    private readonly List<short> _candidate = [];
    private readonly List<short> _utterance = [];
    private readonly int _preRollCapacity;

    private bool _inSpeech;
    private bool _candidateActive;
    private float _lastProbability;
    private int _speechRunMs;
    private int _silenceRunMs;
    private int _trailingSilenceSamples;
    private long _processedSamples;
    private long _onsetSample;

    public SpeechSegmenter(IVadModel vad, VadOptions options)
    {
        _vad = vad;
        _options = options;
        _preRollCapacity = Math.Max(0, options.PreRollMs) * 16 + WindowSamples;
    }

    public float Threshold
    {
        get => _options.Threshold;
        set => _options.Threshold = value;
    }

    public SegmenterFrame Push(ReadOnlySpan<short> pcm)
    {
        var events = new List<SegmentEvent>();
        float maxProbability = 0;

        foreach (var sample in pcm)
        {
            _buffer.Add(sample);
            if (_preRollCapacity > 0)
            {
                _preRoll.Enqueue(sample);
                if (_preRoll.Count > _preRollCapacity) _preRoll.Dequeue();
            }
        }

        var offset = 0;
        while (_buffer.Count - offset >= WindowSamples)
        {
            ProcessWindow(
                _buffer.GetRange(offset, WindowSamples).ToArray(),
                events,
                ref maxProbability);
            offset += WindowSamples;
        }

        if (offset > 0) _buffer.RemoveRange(0, offset);

        return new SegmenterFrame(
            events,
            offset > 0 ? maxProbability : _lastProbability,
            _inSpeech || _candidateActive);
    }

    /// <summary>Audio captured so far for the current or just-finished utterance.</summary>
    public short[] CurrentUtterance() => [.. _utterance];

    public void Reset()
    {
        _buffer.Clear();
        _preRoll.Clear();
        _candidate.Clear();
        _utterance.Clear();
        _inSpeech = false;
        _candidateActive = false;
        _speechRunMs = 0;
        _silenceRunMs = 0;
        _trailingSilenceSamples = 0;
        _processedSamples = 0;
        _onsetSample = 0;
        _vad.Reset();
    }

    private void ProcessWindow(
        short[] window,
        List<SegmentEvent> events,
        ref float maxProbability)
    {
        var probability = _vad.Predict(window);
        _processedSamples += WindowSamples;
        _lastProbability = probability;

        maxProbability = Math.Max(maxProbability, probability);
        var isSpeech = probability >= _options.Threshold;

        if (!_inSpeech)
        {
            if (!isSpeech)
            {
                _candidateActive = false;
                _candidate.Clear();
                _speechRunMs = 0;
                return;
            }

            if (!_candidateActive)
            {
                _candidateActive = true;
                _candidate.Clear();

                // The pre-roll already contains the current window; the last WindowSamples
                // of it are the window itself, so seed only with what came before it.
                _candidate.AddRange(_preRoll.Take(Math.Max(0, _preRoll.Count - WindowSamples)));
                _speechRunMs = 0;
                _onsetSample = _processedSamples - WindowSamples;
            }

            _candidate.AddRange(window);
            _speechRunMs += WindowMs;

            if (_speechRunMs >= _options.MinSpeechMs)
            {
                _inSpeech = true;
                _candidateActive = false;
                _utterance.Clear();
                _utterance.AddRange(_candidate);
                _silenceRunMs = 0;
                _trailingSilenceSamples = 0;
                events.Add(new SegmentEvent(true, null, _onsetSample));
            }

            return;
        }

        _utterance.AddRange(window);

        if (isSpeech)
        {
            _silenceRunMs = 0;
            _trailingSilenceSamples = 0;
        }
        else
        {
            _silenceRunMs += WindowMs;
            _trailingSilenceSamples += WindowSamples;
        }

        if (_silenceRunMs < _options.MinSilenceMs && _utterance.Count < MaxUtteranceSamples) return;

        var keep = Math.Max(0, _utterance.Count - _trailingSilenceSamples);
        events.Add(new SegmentEvent(false, [.. _utterance.Take(keep)], _onsetSample));

        _inSpeech = false;
        _utterance.Clear();
        _silenceRunMs = 0;
        _trailingSilenceSamples = 0;
    }
}
