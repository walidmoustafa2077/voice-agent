using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace VoiceAgent.Server.Pipeline;

public sealed class LlmService(IChatClient client, string systemPrompt = LlmService.DefaultSystemPrompt)
{
    public const string DefaultSystemPrompt =
        "You are a voice assistant on a phone call. By default, answer in one or two short spoken " +
        "sentences. If the user asks for a story, an explanation, or a longer answer, give it in " +
        "plain spoken sentences for as long as they asked. No markdown, lists, or code. The user's " +
        "words come from a noisy speech recognizer; if a message does not make sense, ask them to " +
        "repeat it instead of guessing.";

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
