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
}
