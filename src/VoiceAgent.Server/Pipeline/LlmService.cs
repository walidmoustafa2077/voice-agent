using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace VoiceAgent.Server.Pipeline;

public sealed class LlmService(IChatClient client, string systemPrompt = LlmService.DefaultSystemPrompt)
{
    public const string DefaultSystemPrompt =
        "You are a voice assistant on a phone call. Answer in one short sentence of at most 20 words. " +
        "No markdown, lists, or code.";

    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatMessage> history,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var messages = new List<ChatMessage>(history.Count + 1)
        {
            new(ChatRole.System, systemPrompt),
        };
        messages.AddRange(history);

        await foreach (var update in client.GetStreamingResponseAsync(messages, cancellationToken: ct))
        {
            if (!string.IsNullOrEmpty(update.Text))
                yield return update.Text;
        }
    }
}
