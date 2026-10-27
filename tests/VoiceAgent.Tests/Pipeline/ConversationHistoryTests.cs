using VoiceAgent.Server.Pipeline;

namespace VoiceAgent.Tests.Pipeline;

public sealed class ConversationHistoryTests
{
    [Fact]
    public void Keeps_only_the_sentences_the_user_heard()
    {
        var history = new ConversationHistory();
        history.AddUser("tell me about Moshi");
        history.AddAssistant("First sentence. Second sentence. Third sentence.");

        history.TruncateLastAssistant(
            ["First sentence.", "Second sentence.", "Third sentence."],
            lastPlayedIndex: 0);

        Assert.Equal("First sentence. [interrupted]", history.Messages[^1].Text);
    }

    [Fact]
    public void Interruption_before_any_sentence_leaves_a_marker()
    {
        var history = new ConversationHistory();
        history.AddUser("tell me about Moshi");
        history.AddAssistant("First sentence. Second sentence.");

        history.TruncateLastAssistant(["First sentence.", "Second sentence."], lastPlayedIndex: -1);

        // Nothing was heard, but the message must not vanish: two adjacent user turns made the
        // model answer the previous question again. A marker keeps the boundary visible.
        Assert.Equal(2, history.Messages.Count);
        Assert.Equal("tell me about Moshi", history.Messages[0].Text);
        Assert.Contains("interrupted", history.Messages[^1].Text);
    }

    [Fact]
    public void RemoveLastUser_supports_turn_merging()
    {
        var history = new ConversationHistory();
        history.AddUser("first question");
        history.AddAssistant("an answer");

        history.RemoveLastUser();

        Assert.DoesNotContain(history.Messages, m => m.Text == "first question");
    }

    [Fact]
    public void Caps_growth_at_the_message_limit()
    {
        var history = new ConversationHistory();

        for (var i = 0; i < 25; i++)
        {
            history.AddUser($"question {i}");
            history.AddAssistant($"answer {i}");
        }

        Assert.True(history.Messages.Count <= 40, $"history grew to {history.Messages.Count}");
        Assert.Equal("answer 24", history.Messages[^1].Text);
    }
}
