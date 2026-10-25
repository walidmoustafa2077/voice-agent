using VoiceAgent.Server.Contracts;

namespace VoiceAgent.Server.Sessions;

/// <summary>
/// Owns the turn lifecycle for one connection: one cancellation token per turn, and a watchdog
/// that is armed only once every sentence has been dispatched.
/// </summary>
public sealed class TurnCoordinator : IDisposable
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _fallbackMargin;

    private CancellationTokenSource _cts = new();
    private ITimer? _watchdog;
    private double _lastDurationMs;
    private readonly HashSet<int> _played = [];

    public TurnCoordinator(TimeProvider time, TimeSpan fallbackMargin)
    {
        _time = time;
        _fallbackMargin = fallbackMargin;
    }

    public VoiceState State { get; private set; } = VoiceState.Idle;

    public CancellationToken TurnToken => _cts.Token;

    /// <summary>Index the next dispatched sentence will carry.</summary>
    public int NextSentenceIndex { get; private set; }

    public bool AllSentencesDispatched { get; private set; }

    /// <summary>How many sentences this turn's TTS produced; set when dispatch completes.</summary>
    public int TotalSentences { get; private set; }

    public event Action<VoiceState>? StateChanged;

    public void BeginListening()
    {
        StopWatchdog();
        SetState(VoiceState.Listening);
    }

    public void BeginThinking()
    {
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();

        StopWatchdog();
        AllSentencesDispatched = false;
        TotalSentences = 0;
        NextSentenceIndex = 0;
        _played.Clear();

        SetState(VoiceState.Thinking);
    }

    public void BeginSpeaking(int index, double durationMs)
    {
        NextSentenceIndex = Math.Max(NextSentenceIndex, index + 1);
        _lastDurationMs = durationMs;

        SetState(VoiceState.Speaking);

        if (AllSentencesDispatched) ArmWatchdog();
    }

    public void MarkAllSentencesDispatched(int totalSentences)
    {
        TotalSentences = totalSentences;
        AllSentencesDispatched = true;
        if (State == VoiceState.Speaking) ArmWatchdog();
    }

    public void SentenceStarted(int index) => ArmWatchdogIfDispatched();

    public void SentencePlayed(int index)
    {
        _played.Add(index);

        if (AllSentencesDispatched && _played.Count >= TotalSentences)
        {
            StopWatchdog();
            SetState(VoiceState.Listening);
            return;
        }

        ArmWatchdogIfDispatched();
    }

    public void Cancel(string reason)
    {
        _cts.Cancel();
        StopWatchdog();
        SetState(VoiceState.Listening);
    }

    public void Dispose()
    {
        StopWatchdog();
        _cts.Dispose();
    }

    private void ArmWatchdogIfDispatched()
    {
        if (AllSentencesDispatched) ArmWatchdog();
    }

    private void ArmWatchdog()
    {
        StopWatchdog();

        var due = TimeSpan.FromMilliseconds(_lastDurationMs + _fallbackMargin.TotalMilliseconds);
        _watchdog = _time.CreateTimer(_ => ForceListening(), null, due, Timeout.InfiniteTimeSpan);
    }

    private void ForceListening()
    {
        StopWatchdog();
        SetState(VoiceState.Listening);
    }

    private void StopWatchdog()
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
