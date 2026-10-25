using VoiceAgent.Server.Contracts;

namespace VoiceAgent.Server.Sessions;

/// <summary>
/// Owns the turn lifecycle for one connection: one cancellation token per turn, a turn id that
/// stale playback reports cannot cross, and a watchdog armed only once every sentence has been
/// dispatched.
/// </summary>
public sealed class TurnCoordinator : IDisposable
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _fallbackMargin;
    private readonly object _gate = new();
    private readonly HashSet<int> _played = [];
    private readonly Dictionary<int, double> _durations = [];

    private CancellationTokenSource _cts = new();
    private ITimer? _watchdog;
    private double _totalDurationMs;

    public TurnCoordinator(TimeProvider time, TimeSpan fallbackMargin)
    {
        _time = time;
        _fallbackMargin = fallbackMargin;
    }

    public VoiceState State { get; private set; } = VoiceState.Idle;

    public CancellationToken TurnToken => _cts.Token;

    /// <summary>Increments on every turn; playback reports from older turns are ignored.</summary>
    public int TurnId { get; private set; }

    /// <summary>Index the next dispatched sentence will carry.</summary>
    public int NextSentenceIndex { get; private set; }

    public bool AllSentencesDispatched { get; private set; }

    /// <summary>How many sentences this turn's TTS produced; set when dispatch completes.</summary>
    public int TotalSentences { get; private set; }

    /// <summary>Raised on every state change, including watchdog-forced ones.</summary>
    public event Action<VoiceState>? StateChanged;

    public void BeginListening()
    {
        lock (_gate)
        {
            StopWatchdogLocked();
        }

        SetState(VoiceState.Listening);
    }

    public void BeginThinking()
    {
        CancellationTokenSource previous;

        lock (_gate)
        {
            previous = _cts;
            _cts = new CancellationTokenSource();
            StopWatchdogLocked();

            TurnId++;
            AllSentencesDispatched = false;
            TotalSentences = 0;
            NextSentenceIndex = 0;
            _played.Clear();
            _durations.Clear();
            _totalDurationMs = 0;
        }

        previous.Cancel();
        previous.Dispose();

        SetState(VoiceState.Thinking);
    }

    public void BeginSpeaking(int index, double durationMs)
    {
        lock (_gate)
        {
            NextSentenceIndex = Math.Max(NextSentenceIndex, index + 1);
            _durations[index] = durationMs;

            if (AllSentencesDispatched) ArmWatchdogLocked();
        }

        SetState(VoiceState.Speaking);
    }

    public void MarkAllSentencesDispatched(int totalSentences, double totalDurationMs)
    {
        lock (_gate)
        {
            TotalSentences = totalSentences;
            _totalDurationMs = totalDurationMs;
            AllSentencesDispatched = true;

            // Armed even while Thinking: a client that never reports a start (decode failure,
            // dropped chunk) must not leave the session stuck there forever.
            ArmWatchdogLocked();
        }
    }

    public void SentenceStarted(int turnId, int index)
    {
        if (turnId != TurnId) return;

        lock (_gate)
        {
            if (AllSentencesDispatched) ArmWatchdogLocked();
        }
    }

    public void SentencePlayed(int turnId, int index)
    {
        if (turnId != TurnId) return;

        var done = false;

        lock (_gate)
        {
            _played.Add(index);

            if (AllSentencesDispatched && _played.Count >= TotalSentences)
            {
                StopWatchdogLocked();
                done = true;
            }
            else
            {
                ArmWatchdogLocked();
            }
        }

        if (done) SetState(VoiceState.Listening);
    }

    public void Cancel(string reason)
    {
        lock (_gate)
        {
            StopWatchdogLocked();
        }

        _cts.Cancel();
        SetState(VoiceState.Listening);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            StopWatchdogLocked();
        }

        _cts.Dispose();
    }

    private double RemainingMsLocked()
    {
        var played = 0.0;
        foreach (var index in _played)
            played += _durations.GetValueOrDefault(index, 0);

        return Math.Max(0, _totalDurationMs - played);
    }

    private void ArmWatchdogLocked()
    {
        StopWatchdogLocked();

        if (!AllSentencesDispatched) return;

        var due = TimeSpan.FromMilliseconds(RemainingMsLocked() + _fallbackMargin.TotalMilliseconds);
        _watchdog = _time.CreateTimer(_ => ForceListening(), null, due, Timeout.InfiniteTimeSpan);
    }

    private void ForceListening()
    {
        lock (_gate)
        {
            StopWatchdogLocked();
            AllSentencesDispatched = false;
        }

        SetState(VoiceState.Listening);
    }

    private void StopWatchdogLocked()
    {
        _watchdog?.Dispose();
        _watchdog = null;
    }

    private void SetState(VoiceState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }
}
