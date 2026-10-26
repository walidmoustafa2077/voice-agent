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

    private readonly System.Diagnostics.Stopwatch _clock = new();
    private double _firstUpdateMs = double.NaN;
    private double _firstTokenMs = double.NaN;
    private double _totalMs = double.NaN;

    /// <summary>
    /// Time from the request starting to the first streamed update. Ollama writes each NDJSON chunk
    /// as it is produced, so this is the closest measurement of time-to-first-byte that the
    /// IChatClient abstraction exposes; the gap between it and first-token is pre-token queueing.
    /// </summary>
    public double LastFirstUpdateMs => _firstUpdateMs;

    /// <summary>Time from the request starting to the first update that carried visible text.</summary>
    public double LastFirstTokenMs => _firstTokenMs;

    /// <summary>Time from the request starting to the end of the stream; NaN while still running.</summary>
    public double LastTotalMs => _totalMs;

    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatMessage> history,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var messages = new List<ChatMessage>(history.Count + 1)
        {
            new(ChatRole.System, systemPrompt),
        };
        messages.AddRange(history);

        // One LLM call is in flight per pipeline (single-user prototype), so the latest timing is
        // unambiguous. A second concurrent session would overwrite it.
        _clock.Restart();
        _firstUpdateMs = double.NaN;
        _firstTokenMs = double.NaN;
        _totalMs = double.NaN;

        await foreach (var update in client.GetStreamingResponseAsync(messages, cancellationToken: ct))
        {
            if (double.IsNaN(_firstUpdateMs))
                _firstUpdateMs = _clock.Elapsed.TotalMilliseconds;

            if (string.IsNullOrEmpty(update.Text))
                continue;

            if (double.IsNaN(_firstTokenMs))
                _firstTokenMs = _clock.Elapsed.TotalMilliseconds;

            yield return update.Text;
        }

        _totalMs = _clock.Elapsed.TotalMilliseconds;
    }
}
