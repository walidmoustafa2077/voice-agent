using Microsoft.Extensions.AI;

namespace VoiceAgent.Server.Pipeline;

public sealed class ConversationHistory(int maxMessages = 40)
{
    private readonly object _gate = new();
    private readonly List<ChatMessage> _messages = [];

    /// <summary>A snapshot: the turn thread reads this while the capture pump may merge turns.</summary>
    public IReadOnlyList<ChatMessage> Messages
    {
        get
        {
            lock (_gate) return [.. _messages];
        }
    }

    public void AddUser(string text)
    {
        lock (_gate)
        {
            _messages.Add(new ChatMessage(ChatRole.User, text));
            TrimLocked();
        }
    }

    public void AddAssistant(string text)
    {
        lock (_gate)
        {
            _messages.Add(new ChatMessage(ChatRole.Assistant, text));
            TrimLocked();
        }
    }

    public void RemoveLastUser()
    {
        lock (_gate)
        {
            for (var i = _messages.Count - 1; i >= 0; i--)
            {
                if (_messages[i].Role != ChatRole.User)
                    continue;

                _messages.RemoveAt(i);
                return;
            }
        }
    }

    public void TruncateLastAssistant(IReadOnlyList<string> sentences, int lastPlayedIndex)
    {
        lock (_gate)
        {
            if (_messages.Count == 0 || _messages[^1].Role != ChatRole.Assistant)
                return;

            if (lastPlayedIndex < 0)
            {
                _messages.RemoveAt(_messages.Count - 1);
                return;
            }

            var count = Math.Min(lastPlayedIndex + 1, sentences.Count);
            var heard = string.Join(' ', sentences.Take(count));
            _messages[^1] = new ChatMessage(ChatRole.Assistant, $"{heard} [interrupted]");
        }
    }

    private void TrimLocked()
    {
        while (_messages.Count > maxMessages)
            _messages.RemoveAt(0);
    }
}
