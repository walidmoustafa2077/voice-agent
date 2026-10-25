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
    public void Interruption_before_any_sentence_drops_the_message()
    {
        var history = new ConversationHistory();
        history.AddUser("tell me about Moshi");
        history.AddAssistant("First sentence. Second sentence.");

        history.TruncateLastAssistant(["First sentence.", "Second sentence."], lastPlayedIndex: -1);

        Assert.Single(history.Messages);
        Assert.Equal("tell me about Moshi", history.Messages[0].Text);
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
}
