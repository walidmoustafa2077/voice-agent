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
        c.MarkAllSentencesDispatched(1, 1200);

        c.SentencePlayed(c.TurnId, 0);

        Assert.Equal(VoiceState.Listening, c.State);
    }

    [Fact]
    public void Playing_the_first_of_two_sentences_does_not_end_speaking()
    {
        var (c, _) = Create();
        c.BeginThinking();
        c.BeginSpeaking(0, 1200);
        c.MarkAllSentencesDispatched(2, 2400);

        c.SentencePlayed(c.TurnId, 0);

        Assert.Equal(VoiceState.Speaking, c.State);
    }

    [Fact]
    public void Speaking_does_not_end_while_kokoro_is_behind_playback()
    {
        var (c, time) = Create();
        c.BeginSpeaking(0, 300);
        c.SentencePlayed(c.TurnId, 0);

        time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(VoiceState.Speaking, c.State);
    }

    [Fact]
    public void Watchdog_forces_listening_when_sentence_played_never_arrives()
    {
        var (c, time) = Create();
        c.BeginSpeaking(0, 300);
        c.MarkAllSentencesDispatched(1, 300);

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
    public void Watchdog_uses_remaining_audio_not_just_the_last_started_sentence()
    {
        var (c, time) = Create();
        c.BeginThinking();
        c.BeginSpeaking(0, 300);            // fast first sentence
        c.MarkAllSentencesDispatched(2, 6300); // but 6 s still to come, none started yet

        time.Advance(TimeSpan.FromMilliseconds(900));

        Assert.Equal(VoiceState.Speaking, c.State);
        time.Advance(TimeSpan.FromMilliseconds(6000));
        Assert.Equal(VoiceState.Listening, c.State);
    }

    [Fact]
    public void Thinking_without_any_playback_still_returns_to_listening()
    {
        var (c, time) = Create();
        c.BeginThinking();
        c.MarkAllSentencesDispatched(1, 0);

        time.Advance(TimeSpan.FromMilliseconds(600));

        Assert.Equal(VoiceState.Listening, c.State);
    }

    [Fact]
    public void Stale_playback_from_an_earlier_turn_is_ignored()
    {
        var (c, _) = Create();
        c.BeginThinking();
        var firstTurn = c.TurnId;
        c.BeginSpeaking(0, 1000);
        c.MarkAllSentencesDispatched(1, 1000);

        c.BeginThinking();
        c.BeginSpeaking(0, 1000);
        c.MarkAllSentencesDispatched(1, 1000);

        c.SentencePlayed(firstTurn, 0);

        Assert.Equal(VoiceState.Speaking, c.State);
    }

    [Fact]
    public void Watchdog_transition_is_announced()
    {
        var (c, time) = Create();
        var states = new List<VoiceState>();
        c.StateChanged += states.Add;

        c.BeginThinking();
        c.MarkAllSentencesDispatched(0, 0);
        time.Advance(TimeSpan.FromMilliseconds(600));

        Assert.Equal(VoiceState.Listening, states[^1]);
    }

    [Fact]
    public void SentenceStarted_resets_the_watchdog()
    {
        var (c, time) = Create();
        c.BeginSpeaking(0, 300);
        c.MarkAllSentencesDispatched(2, 2300);

        time.Advance(TimeSpan.FromMilliseconds(700));
        c.SentenceStarted(c.TurnId, 1);
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

    [Fact]
    public void Cancel_is_a_turn_boundary_and_late_reports_are_ignored()
    {
        var (c, _) = Create();
        c.BeginThinking();
        var cancelledTurn = c.TurnId;
        c.BeginSpeaking(0, 1000);
        c.MarkAllSentencesDispatched(1, 1000);

        var (reportedTurn, lastPlayed) = c.Cancel("user");

        Assert.Equal(cancelledTurn, reportedTurn);
        Assert.Equal(-1, lastPlayed);
        Assert.Equal(VoiceState.Listening, c.State);
        Assert.NotEqual(cancelledTurn, c.TurnId);

        // Every report for the dead turn is rejected, including one that would otherwise
        // resurrect Speaking with no watchdog armed.
        Assert.False(c.SentenceStarted(cancelledTurn, 0, 1000));
        c.SentencePlayed(cancelledTurn, 0);
        Assert.False(c.MarkAllSentencesDispatched(cancelledTurn, 1, 1000));
        Assert.Equal(VoiceState.Listening, c.State);
        Assert.False(c.AllSentencesDispatched);
    }

    [Fact]
    public void Cancel_reports_the_highest_sentence_the_client_played()
    {
        var (c, _) = Create();
        c.BeginThinking();
        var turn = c.TurnId;
        c.MarkAllSentencesDispatched(3, 3000);
        c.SentencePlayed(turn, 0);
        c.SentencePlayed(turn, 2);

        var (reportedTurn, lastPlayed) = c.Cancel("stop");

        Assert.Equal(turn, reportedTurn);
        Assert.Equal(2, lastPlayed);
    }

    [Fact]
    public void Watchdog_also_invalidates_reports_from_the_timed_out_turn()
    {
        var (c, time) = Create();
        c.BeginThinking();
        var timedOut = c.TurnId;
        c.MarkAllSentencesDispatched(1, 0);

        time.Advance(TimeSpan.FromMilliseconds(600));

        Assert.Equal(VoiceState.Listening, c.State);
        Assert.False(c.SentenceStarted(timedOut, 0, 100));
        Assert.Equal(VoiceState.Listening, c.State);
    }

    private static (TurnCoordinator Coordinator, FakeTimeProvider Time) Create()
    {
        var time = new FakeTimeProvider();
        return (new TurnCoordinator(time, Margin), time);
    }
}
