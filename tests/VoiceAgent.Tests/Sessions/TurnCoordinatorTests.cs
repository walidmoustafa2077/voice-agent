using Microsoft.Extensions.Time.Testing;
using VoiceAgent.Server.Sessions;
using Xunit;

namespace VoiceAgent.Tests.Sessions;

public class TurnCoordinatorTests
{
    private static readonly TimeSpan Margin = TimeSpan.FromMilliseconds(500);

    [Fact]
    public void Speaking_ends_when_last_sentence_played_and_all_dispatched()
    {
        var (c, _) = Create();
        c.BeginThinking();
        c.BeginSpeaking(0, 1200);
        c.MarkAllSentencesDispatched();

        c.SentencePlayed(0);

        Assert.Equal(VoiceState.Listening, c.State);
    }

    [Fact]
    public void Speaking_does_not_end_while_kokoro_is_behind_playback()
    {
        var (c, time) = Create();
        c.BeginSpeaking(0, 300);
        c.SentencePlayed(0);

        time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(VoiceState.Speaking, c.State);
    }

    [Fact]
    public void Watchdog_forces_listening_when_sentence_played_never_arrives()
    {
        var (c, time) = Create();
        c.BeginSpeaking(0, 300);
        c.MarkAllSentencesDispatched();

        time.Advance(TimeSpan.FromMilliseconds(900));

        Assert.Equal(VoiceState.Listening, c.State);
    }

    [Fact]
    public void Watchdog_is_not_armed_before_dispatch_completes()
    {
        var (c, time) = Create();
        c.BeginSpeaking(0, 300);

        time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(VoiceState.Speaking, c.State);
    }

    [Fact]
    public void SentenceStarted_resets_the_watchdog()
    {
        var (c, time) = Create();
        c.BeginSpeaking(0, 300);
        c.MarkAllSentencesDispatched();

        time.Advance(TimeSpan.FromMilliseconds(700));
        c.SentenceStarted(1);
        time.Advance(TimeSpan.FromMilliseconds(600));

        Assert.Equal(VoiceState.Speaking, c.State);
    }

    [Fact]
    public void Cancel_returns_to_listening_and_trips_the_token()
    {
        var (c, _) = Create();
        c.BeginThinking();
        var token = c.TurnToken;

        c.Cancel("user");

        Assert.True(token.IsCancellationRequested);
        Assert.Equal(VoiceState.Listening, c.State);
    }

    [Fact]
    public void Thinking_replaces_the_previous_turn_token()
    {
        var (c, _) = Create();
        c.BeginThinking();
        var first = c.TurnToken;
        c.BeginThinking();

        Assert.True(first.IsCancellationRequested);
        Assert.False(c.TurnToken.IsCancellationRequested);
    }

    [Fact]
    public void State_changes_are_announced_once()
    {
        var (c, _) = Create();
        var states = new List<VoiceState>();
        c.StateChanged += states.Add;

        c.BeginListening();
        c.BeginThinking();
        c.BeginSpeaking(0, 100);

        Assert.Equal([VoiceState.Listening, VoiceState.Thinking, VoiceState.Speaking], states);
    }

    private static (TurnCoordinator Coordinator, FakeTimeProvider Time) Create()
    {
        var time = new FakeTimeProvider();
        return (new TurnCoordinator(time, Margin), time);
    }
}
