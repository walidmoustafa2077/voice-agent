using Microsoft.Extensions.AI;
using OllamaSharp;
using VoiceAgent.Server.Pipeline;

namespace VoiceAgent.Tests.Pipeline;

public sealed class LlmServiceTests
{
    [Fact]
    public void Default_prompt_keeps_replies_short_but_allows_elaboration()
    {
        var prompt = LlmService.DefaultSystemPrompt;

        Assert.Contains("one or two short spoken sentences", prompt);
        Assert.Contains("story", prompt);
        Assert.Contains("as long as they asked", prompt);
        Assert.Contains("No markdown, lists, or code", prompt);

        // Speech starts at the first finished sentence, so a long answer is cheap; an absolute
        // word limit would make the assistant refuse a legitimate request for detail.
        Assert.DoesNotContain("20 words", prompt);
        Assert.DoesNotContain("one short sentence", prompt);
    }

    [Fact]
    public void Default_prompt_tells_the_model_to_ask_again_on_garbled_input()
    {
        var prompt = LlmService.DefaultSystemPrompt;

        Assert.Contains("noisy speech recognizer", prompt);
        Assert.Contains("ask them to repeat", prompt);
        Assert.Contains("instead of guessing", prompt);
    }

    [Fact]
    public async Task Timing_separates_first_byte_from_first_visible_token()
    {
        var client = new SlowClient();
        var llm = new LlmService(client);
        var history = new ConversationHistory();
        history.AddUser("hello");

        var deltas = new List<string>();
        await foreach (var delta in llm.StreamAsync(history.Messages)) deltas.Add(delta);

        // The empty update that opens the stream must not be spoken, but it does mark the first
        // byte, so queueing time is visible separately from the first real token.
        Assert.Equal(["hello"], deltas);
        Assert.False(double.IsNaN(llm.LastFirstUpdateMs));
        Assert.True(llm.LastFirstTokenMs >= llm.LastFirstUpdateMs);
        Assert.True(llm.LastTotalMs >= llm.LastFirstTokenMs);
    }

    [Fact]
    public async Task Streams_tokens_from_gemma()
    {
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            await probe.GetAsync("http://localhost:11434/api/tags");
        }
        catch
        {
            return; // Ollama not running: integration test is advisory
        }

        var llm = new LlmService(new OllamaApiClient(new Uri("http://localhost:11434"), "gemma4:31b-cloud"));
        var history = new ConversationHistory();
        history.AddUser("Say hello in five words or fewer.");

        var deltas = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await foreach (var delta in llm.StreamAsync(history.Messages, cts.Token))
            deltas.Add(delta);

        Assert.True(deltas.Count >= 2, $"got {deltas.Count} deltas");
        Assert.False(string.IsNullOrWhiteSpace(string.Concat(deltas)));
    }

    /// <summary>Opens the stream with an update that carries no visible text, then speaks.</summary>
    private sealed class SlowClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(20, cancellationToken);

            // Same shape as a thinking chunk: the stream is open, but there is nothing to say yet.
            yield return new ChatResponseUpdate(ChatRole.Assistant, "");

            await Task.Delay(20, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "hello");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
