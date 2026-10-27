using System.Diagnostics;
using VoiceAgent.Server;
using VoiceAgent.Server.Pipeline;

namespace VoiceAgent.Harness;

/// <summary>
/// Compares the recognizer's default greedy decoding with a 4-path beam search on the same
/// audio. Same model and same class of latency; only the hypothesis search differs.
/// </summary>
public static class SttBeamBench
{
    public static async Task<int> RunAsync(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
        {
            Console.Error.WriteLine("usage: VoiceAgent.Harness --stt-beam <file.wav>...");
            return 1;
        }

        if (!ModelPaths.SherpaReady)
        {
            Console.Error.WriteLine("sherpa models missing");
            return 1;
        }

        using var greedy = Create(beam: false);
        using var beam = Create(beam: true);

        foreach (var file in files)
        {
            var pcm = Wav16k.Read(file);
            var audioMs = pcm.Length / 16.0;

            Console.WriteLine();
            Console.WriteLine($"=== {Path.GetFileName(file)} ({audioMs:F0} ms)");

            var first = await DecodeAsync(greedy, pcm, audioMs);
            Console.WriteLine($"  greedy ({first.Ms:F0} ms, {first.Ms / audioMs:F2}x): {first.Text}");

            var second = await DecodeAsync(beam, pcm, audioMs);
            Console.WriteLine($"  beam4  ({second.Ms:F0} ms, {second.Ms / audioMs:F2}x): {second.Text}");
        }

        Console.WriteLine();
        return 0;
    }

    private static SherpaStreamingStt Create(bool beam) => new(
        ModelPaths.SherpaEncoder,
        ModelPaths.SherpaDecoder,
        ModelPaths.SherpaJoiner,
        ModelPaths.SherpaTokens,
        beamSearch: beam);

    private static async Task<(string Text, double Ms)> DecodeAsync(
        SherpaStreamingStt stt, short[] pcm, double audioMs)
    {
        var sw = Stopwatch.StartNew();
        using var stream = stt.Begin();
        for (var offset = 0; offset + 320 <= pcm.Length; offset += 320)
            stream.Accept(pcm.AsSpan(offset, 320));

        var result = await stream.FinalizeAsync(CancellationToken.None);
        sw.Stop();

        return (result.Text, sw.Elapsed.TotalMilliseconds);
    }
}
