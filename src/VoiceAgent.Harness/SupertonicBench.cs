using System.Diagnostics;
using VoiceAgent.Server;
using VoiceAgent.Server.Pipeline;

namespace VoiceAgent.Harness;

/// <summary>
/// Times Supertonic 3 through the same <see cref="ITtsService"/> the pipeline uses, so its
/// numbers are directly comparable with the Kokoro and SAPI blocks in <see cref="TtsBench"/>.
/// </summary>
public static class SupertonicBench
{
    public const string Sentence = "I can help with that. Let me check the details and get back to you.";

    public static async Task<int> RunAsync()
    {
        if (!ModelPaths.SupertonicReady)
        {
            Console.Error.WriteLine("supertonic model files missing or incomplete");
            return 1;
        }

        Console.WriteLine($"sentence: {Sentence}");
        Console.WriteLine($"model: {ModelPaths.SupertonicDir}");
        Console.WriteLine();

        foreach (var threads in new[] { 1, 2, 3 })
        {
            using var tts = new SupertonicTtsService(ModelPaths.SupertonicDir, threads);

            var warm = await tts.SynthesizeAsync("Ready.", CancellationToken.None);
            Console.WriteLine($"Supertonic 3 int8, threads={threads}, {tts.SampleRate} Hz");
            Console.WriteLine($"  warm-up {warm.DurationMs:F0} ms audio");

            var walls = new List<double>();
            double audioMs = 0;

            for (var i = 0; i < 3; i++)
            {
                var sw = Stopwatch.StartNew();
                var chunk = await tts.SynthesizeAsync(Sentence, CancellationToken.None);
                sw.Stop();

                walls.Add(sw.Elapsed.TotalMilliseconds);
                audioMs = chunk.DurationMs;
            }

            walls.Sort();
            var median = walls[1];

            Console.WriteLine(
                $"  wall={median:F0} ms audio={audioMs:F0} ms rtf={median / audioMs:F2}x " +
                $"(runs {walls[0]:F0}/{walls[1]:F0}/{walls[2]:F0})");
        }

        return 0;
    }
}
