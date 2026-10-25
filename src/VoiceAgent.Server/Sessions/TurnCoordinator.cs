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
    private int _turnId;
    private bool _disposed;

    public TurnCoordinator(TimeProvider time, TimeSpan fallbackMargin)
    {
        _time = time;
        _fallbackMargin = fallbackMargin;
    }

    public VoiceState State { get; private set; } = VoiceState.Idle;

    public CancellationToken TurnToken
    {
        get
        {
            lock (_gate)
                return _disposed ? CancellationToken.None : _cts.Token;
        }
    }

    /// <summary>Increments on every turn and on every cancel; stale reports are ignored.</summary>
    public int TurnId
    {
        get
        {
            lock (_gate) return _turnId;
        }
    }

    /// <summary>Index the next dispatched sentence will carry.</summary>
    public int NextSentenceIndex { get; private set; }

    public bool AllSentencesDispatched { get; private set; }

    /// <summary>How many sentences this turn's TTS produced; set when dispatch completes.</summary>
    public int TotalSentences { get; private set; }

    /// <summary>
    /// Raised on every state change, including watchdog-forced ones. Raised while the coordinator
    /// lock is held, so handlers must be short and must not block.
    /// </summary>
    public event Action<VoiceState>? StateChanged;

    public void BeginListening()
    {
        lock (_gate)
        {
            StopWatchdogLocked();
            SetStateLocked(VoiceState.Listening);
        }
    }

    public void BeginThinking()
    {
        lock (_gate)
        {
            if (_disposed) return;

            StopWatchdogLocked();

            var previous = _cts;
            _cts = new CancellationTokenSource();

            _turnId++;
            ResetTurnLocked();
            SetStateLocked(VoiceState.Thinking);

            // Cancelled while the lock is held so a concurrent Cancel cannot hit the new turn.
            previous.Cancel();
            previous.Dispose();
        }
    }

    public void BeginSpeaking(int index, double durationMs)
    {
        lock (_gate)
        {
            BeginSpeakingLocked(index, durationMs);
        }
    }

    /// <summary>
    /// A playback report for <paramref name="turnId"/>. Returns false when the report belongs to a
    /// cancelled or superseded turn, so callers do not start playback-side effects for it.
    /// </summary>
    public bool SentenceStarted(
        int turnId, int index, double durationMs, Action? onStarted = null)
    {
        lock (_gate)
        {
            if (turnId != _turnId) return false;

            BeginSpeakingLocked(index, durationMs);
            onStarted?.Invoke();
            return true;
        }
    }

    public bool SentenceStarted(int turnId, int index) => SentenceStarted(turnId, index, 0);

    public bool MarkAllSentencesDispatched(int turnId, int totalSentences, double totalDurationMs)
    {
        lock (_gate)
        {
            if (_disposed || turnId != _turnId) return false;

            TotalSentences = totalSentences;
            _totalDurationMs = totalDurationMs;
            AllSentencesDispatched = true;

            // Armed even while Thinking: a client that never reports a start (decode failure,
            // dropped chunk) must not leave the session stuck there forever.
            ArmWatchdogLocked();
            return true;
        }
    }

    public void MarkAllSentencesDispatched(int totalSentences, double totalDurationMs) =>
        MarkAllSentencesDispatched(TurnId, totalSentences, totalDurationMs);

    public void SentencePlayed(int turnId, int index)
    {
        lock (_gate)
        {
            if (turnId != _turnId) return;

            _played.Add(index);

            if (AllSentencesDispatched && _played.Count >= TotalSentences)
            {
                StopWatchdogLocked();
                SetStateLocked(VoiceState.Listening);
            }
            else
            {
                ArmWatchdogLocked();
            }
        }
    }

    /// <summary>
    /// Ends the turn and returns the identity of the turn that was cancelled together with the
    /// highest sentence index the client reported as played. The id is captured before the bump so
    /// the cancellation payload names the turn the client is flushing.
    /// </summary>
    public (int TurnId, int LastPlayedIndex) Cancel(string reason)
    {
        lock (_gate)
        {
            var cancelledTurn = _turnId;
            var lastPlayed = _played.Count == 0 ? -1 : _played.Max();

            if (_disposed) return (cancelledTurn, lastPlayed);

            StopWatchdogLocked();

            if (!_cts.IsCancellationRequested)
                _cts.Cancel();

            // Cancellation is a turn boundary: late SentenceStarted/SentencePlayed reports must
            // not resurrect Speaking after the user has been told the agent stopped.
            _turnId++;
            ResetTurnLocked();
            SetStateLocked(VoiceState.Listening);

            return (cancelledTurn, lastPlayed);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            StopWatchdogLocked();
            _cts.Cancel();
            _cts.Dispose();
        }
    }

    private void BeginSpeakingLocked(int index, double durationMs)
    {
        NextSentenceIndex = Math.Max(NextSentenceIndex, index + 1);
        _durations[index] = durationMs;

        if (AllSentencesDispatched) ArmWatchdogLocked();

        SetStateLocked(VoiceState.Speaking);
    }

    private void ResetTurnLocked()
    {
        AllSentencesDispatched = false;
        TotalSentences = 0;
        NextSentenceIndex = 0;
        _played.Clear();
        _durations.Clear();
        _totalDurationMs = 0;
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
        var turn = _turnId;
        _watchdog = _time.CreateTimer(_ => ForceListening(turn), null, due, Timeout.InfiniteTimeSpan);
    }

    private void ForceListening(int turn)
    {
        lock (_gate)
        {
            if (turn != _turnId) return;

            StopWatchdogLocked();
            _turnId++;
            ResetTurnLocked();
            SetStateLocked(VoiceState.Listening);
        }
    }

    private void StopWatchdogLocked()
    {
        _watchdog?.Dispose();
        _watchdog = null;
    }

    private void SetStateLocked(VoiceState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }
}
