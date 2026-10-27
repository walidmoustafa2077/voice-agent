using Microsoft.Extensions.AI;
using VoiceAgent.Server.Pipeline;

namespace VoiceAgent.Tests.Pipeline;

public sealed class InterruptionMarkerTests
{
    [Fact]
    public void An_unheard_answer_leaves_a_marker_instead_of_two_user_messages()
    {
        var history = new ConversationHistory();
        history.AddUser("What is git?");
        history.AddAssistant("Git is a version control system.");

        history.TruncateLastAssistant(["Git is a version control system."], lastPlayedIndex: -1);

        Assert.Equal(ChatRole.Assistant, history.Messages[^1].Role);
        Assert.Contains("interrupted", history.Messages[^1].Text ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MarkInterrupted_adds_a_marker_only_after_a_user_message()
    {
        var history = new ConversationHistory();
        history.AddUser("What is a time machine?");

        history.MarkInterrupted();

        Assert.Equal(2, history.Messages.Count);
        Assert.Equal(ChatRole.Assistant, history.Messages[^1].Role);
        Assert.Contains("interrupted", history.Messages[^1].Text ?? "", StringComparison.OrdinalIgnoreCase);

        // The last message is an assistant marker now; a second cancel must not duplicate it.
        history.MarkInterrupted();
        Assert.Equal(2, history.Messages.Count);
    }

    [Theory]
    [InlineData("Sgt.", true)]
    [InlineData("sgt", true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("Hmm.", true)]
    [InlineData("Yes.", false)]
    [InlineData("No.", false)]
    [InlineData("My.", false)]
    [InlineData("Hello there.", false)]
    [InlineData("What is git?", false)]
    public void Fragmentary_finals_are_implausible(string text, bool implausible) =>
        Assert.Equal(implausible, TranscriptCleaner.IsImplausible(text));
}
