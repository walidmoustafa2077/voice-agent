using VoiceAgent.Server.Pipeline;

namespace VoiceAgent.Harness;

/// <summary>
/// Measures the remote model's wait, which is the one stage the box cannot speed up. Each call
/// reports first byte (queueing) separately from first visible token, and the run ends with the
/// distribution that decides whether a timeout or a local fallback is worth it.
/// </summary>
public static class LlmBench
{
    private static readonly string[] Prompts =
    [
        "What is the weather usually like in Lisbon in spring?",
        "Give me one short sentence about why the sky is blue.",
        "What should I cook for dinner with eggs and rice?",
        "Name three countries in South America.",
        "Why do cats purr?",
    ];

    public static async Task<int> RunAsync(int turns)
    {
        if (turns <= 0) turns = 10;

        var llm = new LlmService(
            new OllamaSharp.OllamaApiClient(new Uri("http://localhost:11434"), "gemma4:31b-cloud"));

        var firsts = new List<double>(turns);
        var tokens = new List<double>(turns);
        var totals = new List<double>(turns);

        for (var i = 0; i < turns; i++)
        {
            var history = new List<Microsoft.Extensions.AI.ChatMessage>
            {
                new(Microsoft.Extensions.AI.ChatRole.User, Prompts[i % Prompts.Length]),
            };

            var text = new System.Text.StringBuilder();

            try
            {
                await foreach (var delta in llm.StreamAsync(history, CancellationToken.None))
                    text.Append(delta);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"llm {i + 1} failed: {ex.Message}");
                continue;
            }

            firsts.Add(llm.LastFirstUpdateMs);
            tokens.Add(llm.LastFirstTokenMs);
            totals.Add(llm.LastTotalMs);

            Console.WriteLine(
                $"llm {i + 1} firstByte={llm.LastFirstUpdateMs:F0}ms " +
                $"firstToken={llm.LastFirstTokenMs:F0}ms total={llm.LastTotalMs:F0}ms " +
                $"chars={text.Length}");
        }

        if (firsts.Count > 0)
        {
            Console.WriteLine(
                $"firstByte ms  min={Min(firsts):F0} median={Median(firsts):F0} max={Max(firsts):F0}");
            Console.WriteLine(
                $"firstToken ms min={Min(tokens):F0} median={Median(tokens):F0} max={Max(tokens):F0}");
            Console.WriteLine(
                $"total ms      min={Min(totals):F0} median={Median(totals):F0} max={Max(totals):F0}");
        }

        return firsts.Count == turns ? 0 : 1;
    }

    private static double Min(List<double> values) => values.Min();

    private static double Max(List<double> values) => values.Max();

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        var mid = sorted.Length / 2;

        return sorted.Length % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
