using Microsoft.Extensions.AI;
using OllamaSharp;
using VoiceAgent.Server.Pipeline;

namespace VoiceAgent.Tests.Pipeline;

public sealed class LlmServiceTests
{
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
