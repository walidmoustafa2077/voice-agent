using System.Diagnostics;
using System.Reflection;
using VoiceAgent.Server;
using VoiceAgent.Server.Pipeline;

namespace VoiceAgent.Harness;

/// <summary>
/// Runs the same WAVs through every streaming recognizer present under models/sherpa/ and prints
/// one row per model, so accuracy and RTF can be compared on identical audio.
/// </summary>
public static class SttModelBench
{
    public static Task<int> RunAsync(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
        {
            Console.Error.WriteLine("usage: VoiceAgent.Harness --stt-models <file.wav>...");
            return Task.FromResult(1);
        }

        var candidates = Candidates();
        if (candidates.Count == 0)
        {
            Console.Error.WriteLine("no streaming recognizer found under models/sherpa");
            return Task.FromResult(1);
        }

        var audio = files.Select(f => (Name: Path.GetFileName(f), Pcm: Wav16k.Read(f))).ToArray();

        foreach (var (name, dir) in candidates)
        {
            using var stt = new SherpaStreamingStt(
                Pick(dir, "encoder"), Pick(dir, "decoder"),
                Pick(dir, "joiner"), Path.Combine(dir, "tokens.txt"));

            var size = SizeMb(dir);
            Console.WriteLine();
            Console.WriteLine($"=== {name} ({size:F1} MB)");

            foreach (var (fileName, pcm) in audio)
            {
                var audioMs = pcm.Length / 16.0;
                var accept = Stopwatch.StartNew();
                using var stream = stt.Begin();
                for (var offset = 0; offset + 320 <= pcm.Length; offset += 320)
                    stream.Accept(pcm.AsSpan(offset, 320));
                accept.Stop();

                var finalize = Stopwatch.StartNew();
                var result = stream.FinalizeAsync(CancellationToken.None).GetAwaiter().GetResult();
                finalize.Stop();

                var total = accept.Elapsed.TotalMilliseconds + finalize.Elapsed.TotalMilliseconds;
                Console.WriteLine(
                    $"  {fileName,-14} {audioMs,6:F0}ms rtf={total / audioMs:F2}x " +
                    $"accept={accept.Elapsed.TotalMilliseconds:F0}ms finalize={finalize.Elapsed.TotalMilliseconds:F0}ms");
                Console.WriteLine($"    {result.Text}");
            }
        }

        return Task.FromResult(0);
    }

    /// <summary>Prints the managed API surface that would be needed for hotword biasing.</summary>
    public static int CheckApi()
    {
        // Reflection by assembly name: the harness does not reference sherpa-onnx at compile time.
        var assembly = AppDomain.CurrentDomain.GetAssemblies()
                           .FirstOrDefault(a => a.GetName().Name == "sherpa-onnx")
                       ?? Assembly.Load("sherpa-onnx");

        Console.WriteLine($"{assembly.FullName}");

        foreach (var candidate in assembly.GetTypes()
                     .Where(t => t.Name.Contains("Online", StringComparison.Ordinal)))
            Console.WriteLine($"  type {candidate.FullName}");

        foreach (var name in new[]
                 {
                     "SherpaOnnx.OnlineRecognizerConfig",
                     "SherpaOnnx.OnlineModelConfig",
                     "SherpaOnnx.OnlineTransducerModelConfig",
                 })
        {
            var type = assembly.GetType(name);
            if (type is null)
            {
                Console.WriteLine($"{name}: not found");
                continue;
            }

            Console.WriteLine($"{type.Name}:");
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                Console.WriteLine($"  {property.PropertyType.Name} {property.Name}");
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                Console.WriteLine($"  {field.FieldType.Name} {field.Name}");
        }

        return 0;
    }

    /// <summary>
    /// Runs the current model twice on the same audio: plain, and with a hotword list. A word the
    /// list repairs shows up as a transcript change, which is the only proof that biasing works.
    /// </summary>
    public static Task<int> HotwordsAsync(IReadOnlyList<string> files, string words)
    {
        if (files.Count == 0 || words.Length == 0)
        {
            Console.Error.WriteLine(
                "usage: VoiceAgent.Harness --stt-hotwords <comma,separated,words> <file.wav>...");
            return Task.FromResult(1);
        }

        var list = Path.Combine(Path.GetTempPath(), "voiceagent-hotwords.txt");
        File.WriteAllLines(
            list, words.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var dir = ModelPaths.SherpaDir;
        var tokens = Path.Combine(dir, "tokens.txt");

        using var plain = new SherpaStreamingStt(
            Pick(dir, "encoder"), Pick(dir, "decoder"), Pick(dir, "joiner"), tokens);

        SherpaStreamingStt? biased = null;
        try
        {
            biased = new SherpaStreamingStt(
                Pick(dir, "encoder"), Pick(dir, "decoder"), Pick(dir, "joiner"), tokens,
                SherpaStreamingStt.Threads, list, bpeVocab: tokens);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"hotwords are not usable with this model: {ex.GetType().Name}: {ex.Message}");
            return Task.FromResult(2);
        }

        using var _ = biased;
        Console.WriteLine($"hotwords: {words} (score 1.5, bpe vocab = tokens.txt)");

        foreach (var file in files)
        {
            var pcm = Wav16k.Read(file);
            Console.WriteLine($"\n=== {Path.GetFileName(file)} ({pcm.Length / 16.0:F0} ms)");
            Console.WriteLine($"  plain : {Text(plain, pcm)}");
            Console.WriteLine($"  biased: {Text(biased, pcm)}");
        }

        return Task.FromResult(0);
    }

    private static string Text(SherpaStreamingStt stt, short[] pcm)
    {
        using var stream = stt.Begin();
        for (var offset = 0; offset + 320 <= pcm.Length; offset += 320)
            stream.Accept(pcm.AsSpan(offset, 320));

        return stream.FinalizeAsync(CancellationToken.None).GetAwaiter().GetResult().Text;
    }

    private static List<(string Name, string Dir)> Candidates()
    {
        var list = new List<(string, string)> { ("int8 left-64 (current)", ModelPaths.SherpaDir) };

        // Extra variants are laid out one directory each with normalized file names.
        foreach (var dir in Directory.GetDirectories(ModelPaths.SherpaDir).OrderBy(d => d))
        {
            if (File.Exists(Path.Combine(dir, "encoder.onnx")))
                list.Add((Path.GetFileName(dir), dir));
        }

        return list;
    }

    private static double SizeMb(string dir) =>
        Directory.GetFiles(dir, "*.onnx").Sum(f => new FileInfo(f).Length) / 1024.0 / 1024.0;

    /// <summary>The current model keeps its published file names; the downloaded ones are plain.</summary>
    private static string Pick(string dir, string prefix) =>
        Directory.GetFiles(dir, prefix + "*.onnx").OrderBy(f => f).First();
}
