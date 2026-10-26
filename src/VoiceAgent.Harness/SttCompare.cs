using System.Diagnostics;
using VoiceAgent.Server;
using VoiceAgent.Server.Pipeline;

namespace VoiceAgent.Harness;

/// <summary>
/// Prints the streaming recognizer's transcript next to Whisper's for the same WAV, so an
/// accuracy complaint can be checked by eye on the exact audio the server heard.
/// </summary>
public static class SttCompare
{
    public static async Task<int> RunAsync(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
        {
            Console.Error.WriteLine("usage: VoiceAgent.Harness --compare <file.wav>...");
            return 1;
        }

        using var sherpa = ModelPaths.SherpaReady
            ? new SherpaStreamingStt(
                ModelPaths.SherpaEncoder, ModelPaths.SherpaDecoder,
                ModelPaths.SherpaJoiner, ModelPaths.SherpaTokens)
            : null;

        using var tiny = File.Exists(ModelPaths.WhisperTinyEn)
            ? new SttService(ModelPaths.WhisperTinyEn)
            : null;

        using var baseEn = ModelPaths.WhisperBaseEnReady
            ? new SttService(ModelPaths.WhisperBaseEn)
            : null;

        if (sherpa is null) Console.WriteLine("sherpa models missing; no live transcript");
        if (tiny is null) Console.WriteLine("whisper tiny.en missing; no comparison");
        if (baseEn is null) Console.WriteLine("whisper base.en not downloaded; skipped");
        if (sherpa is null && tiny is null) return 1;

        foreach (var file in files)
        {
            var pcm = Wav16k.Read(file);
            var audioMs = pcm.Length / 16.0;

            Console.WriteLine();
            Console.WriteLine($"=== {file} ({audioMs:F0} ms)");

            string? live = null;

            if (sherpa is not null)
            {
                var sw = Stopwatch.StartNew();
                using var stream = sherpa.Begin();
                for (var offset = 0; offset + 320 <= pcm.Length; offset += 320)
                    stream.Accept(pcm.AsSpan(offset, 320));
                var result = await stream.FinalizeAsync(CancellationToken.None);
                sw.Stop();

                live = result.Text;
                Console.WriteLine(
                    $"  live    ({sw.Elapsed.TotalMilliseconds:F0} ms, " +
                    $"{sw.Elapsed.TotalMilliseconds / audioMs:F2}x): {result.Text}");
            }

            // A saved debug pair carries the transcript the live session actually produced, which
            // is more useful than re-decoding when the stream has since been tuned.
            var savedPath = Path.ChangeExtension(file, ".stream.txt");
            var saved = File.Exists(savedPath) ? File.ReadAllText(savedPath).Trim() : null;
            if (saved is not null)
                Console.WriteLine($"  saved   : {saved}");

            string? tinyText = null;
            if (tiny is not null)
            {
                (tinyText, var row) = await WhisperRowAsync(tiny, pcm, audioMs);
                Console.WriteLine($"  tiny.en : {row}");
            }

            if (baseEn is not null)
            {
                var (_, row) = await WhisperRowAsync(baseEn, pcm, audioMs);
                Console.WriteLine($"  base.en : {row}");
            }

            if (saved is not null && live is not null && saved != live)
                Console.WriteLine($"  diff(saved/live): only-saved=[{Join(Difference(saved, live))}] " +
                                  $"only-live=[{Join(Difference(live, saved))}]");

            var reference = live ?? saved;
            if (reference is not null && tinyText is not null)
                Console.WriteLine($"  diff(live/tiny): only-live=[{Join(Difference(reference, tinyText))}] " +
                                  $"only-tiny=[{Join(Difference(tinyText, reference))}]");
        }

        Console.WriteLine();
        return 0;
    }

    /// <summary>Runs one WAV through the whole-utterance adapter and reports its cost.</summary>
    private static async Task<(string Text, string Row)> WhisperRowAsync(
        SttService whisper, short[] pcm, double audioMs)
    {
        var sw = Stopwatch.StartNew();
        using var buffered = new BufferedWhisperStt(whisper);
        var result = await buffered.TranscribeAsync(pcm, CancellationToken.None);
        sw.Stop();

        var row = $"({sw.Elapsed.TotalMilliseconds:F0} ms, " +
                  $"{sw.Elapsed.TotalMilliseconds / audioMs:F2}x) {result.Text}";
        return (result.Text, row);
    }

    /// <summary>Words in <paramref name="text"/> that do not appear in <paramref name="other"/>.</summary>
    private static string[] Difference(string text, string other)
    {
        var known = Words(other).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. Words(text).Where(w => !known.Contains(w)).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static IEnumerable<string> Words(string text) =>
        text.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()))
            .Where(w => w.Length > 0);

    private static string Join(string[] words) => string.Join(' ', words);
}
