using Microsoft.Extensions.AI;

namespace VoiceAgent.Server.Pipeline;

public sealed class ConversationHistory
{
    private readonly List<ChatMessage> _messages = [];

    public IReadOnlyList<ChatMessage> Messages => _messages;

    public void AddUser(string text) => _messages.Add(new ChatMessage(ChatRole.User, text));

    public void AddAssistant(string text) => _messages.Add(new ChatMessage(ChatRole.Assistant, text));

    public void RemoveLastUser()
    {
        for (var i = _messages.Count - 1; i >= 0; i--)
        {
            if (_messages[i].Role != ChatRole.User)
                continue;

            _messages.RemoveAt(i);
            return;
        }
    }

    public void TruncateLastAssistant(IReadOnlyList<string> sentences, int lastPlayedIndex)
    {
        if (_messages.Count == 0 || _messages[^1].Role != ChatRole.Assistant)
            return;

        if (lastPlayedIndex < 0)
        {
            _messages.RemoveAt(_messages.Count - 1);
            return;
        }

        var heard = string.Join(' ', sentences.Take(lastPlayedIndex + 1));
        _messages[^1] = new ChatMessage(ChatRole.Assistant, $"{heard} [interrupted]");
    }
}
