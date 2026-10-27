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
                // Nothing was heard. A marker keeps the next user message from reading as a
                // continuation of this question, which made the model answer the old one again.
                _messages[^1] = new ChatMessage(
                    ChatRole.Assistant, "[the answer was interrupted before it was heard]");
                return;
            }

            var count = Math.Min(lastPlayedIndex + 1, sentences.Count);
            var heard = string.Join(' ', sentences.Take(count));
            _messages[^1] = new ChatMessage(ChatRole.Assistant, $"{heard} [interrupted]");
        }
    }

    /// <summary>
    /// Records that a reply was cut off before any assistant message reached history, so the
    /// next user message is not read as a continuation of the interrupted question.
    /// </summary>
    public void MarkInterrupted()
    {
        lock (_gate)
        {
            if (_messages.Count == 0 || _messages[^1].Role != ChatRole.User) return;

            _messages.Add(new ChatMessage(
                ChatRole.Assistant, "[the answer was interrupted by the user]"));
            TrimLocked();
        }
    }

    private void TrimLocked()
    {
        while (_messages.Count > maxMessages)
            _messages.RemoveAt(0);
    }
}
