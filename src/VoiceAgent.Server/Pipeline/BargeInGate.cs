using VoiceAgent.Server.Audio;

namespace VoiceAgent.Server.Pipeline;

/// <summary>
/// Confirms an interruption only after sustained user speech, so a cough, click, or a single
/// syllable does not kill the reply.
/// </summary>
public sealed class BargeInGate
{
    private readonly int _confirmMs;
    private int _sustainedMs;
    private bool _latched;

    public BargeInGate(int confirmMs = 250)
    {
        _confirmMs = confirmMs;
    }

    /// <summary>Returns true once per confirmed interruption; call <see cref="Reset"/> afterwards.</summary>
    public bool Push(bool isUserSpeech, int frameMs)
    {
        if (_latched) return false;

        if (!isUserSpeech)
        {
            _sustainedMs = 0;
            return false;
        }

        _sustainedMs += frameMs;
        if (_sustainedMs < _confirmMs) return false;

        _latched = true;
        return true;
    }

    public void Reset()
    {
        _sustainedMs = 0;
        _latched = false;
    }
}

/// <summary>
/// Separates the user's voice from the agent's own playback leaking back through the microphone.
/// The floor is calibrated from the first 200 ms of each turn, because that window is normally
/// pure AEC residual: the agent is talking and the user has not started yet. If VAD says the user
/// is already speaking during that window, calibration is abandoned rather than poisoned.
/// </summary>
public sealed class EchoFloorGate
{
    public const float AbsoluteFloor = 0.015f;
    public const float VadThreshold = 0.6f;
    public const int CalibrationMs = 200;

    private readonly int _frameMs;
    private readonly object _sync = new();
    private double _sum;
    private int _frames;
    private bool _calibrating;
    private bool _calibrated;
    private float _floor = AbsoluteFloor;

    public EchoFloorGate(int frameMs = 20)
    {
        _frameMs = frameMs;
    }

    public float Floor
    {
        get
        {
            lock (_sync) return _floor;
        }
    }

    public void BeginPlayback()
    {
        lock (_sync)
        {
            if (_calibrated) return;

            _calibrating = true;
            _sum = 0;
            _frames = 0;
        }
    }

    public void EndPlayback()
    {
        lock (_sync)
        {
            // The next turn gets a fresh floor; carrying a loud previous turn's floor into
            // Thinking would make a normal merge utterance look like echo.
            _calibrating = false;
            _calibrated = false;
            _sum = 0;
            _frames = 0;
            _floor = AbsoluteFloor;
        }
    }

    public bool IsUserSpeech(ReadOnlySpan<short> pcm, float vadProbability)
    {
        var rms = Rms(pcm);

        lock (_sync)
        {
            if (_calibrating)
            {
                if (vadProbability >= VadThreshold && rms > AbsoluteFloor * 2)
                {
                    // The user started talking inside the calibration window. Their voice must
                    // not become the floor, or barge-in would be disabled for the whole turn.
                    _calibrating = false;
                    _calibrated = false;
                    _floor = AbsoluteFloor;
                    return true;
                }

                _sum += rms;
                _frames++;

                if (_frames * _frameMs >= CalibrationMs)
                {
                    _floor = Math.Max(AbsoluteFloor, (float)(_sum / _frames));
                    _calibrating = false;
                    _calibrated = true;
                }

                return false;
            }

            return vadProbability >= VadThreshold && rms > Math.Max(AbsoluteFloor, _floor * 2.5f);
        }
    }

    private static double Rms(ReadOnlySpan<short> pcm)
    {
        if (pcm.IsEmpty) return 0;

        double sum = 0;
        foreach (var sample in pcm)
        {
            var value = sample / 32768.0;
            sum += value * value;
        }

        return Math.Sqrt(sum / pcm.Length);
    }
}
