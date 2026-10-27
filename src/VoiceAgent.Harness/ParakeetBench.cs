using System.Diagnostics;
using SherpaOnnx;
using VoiceAgent.Server;

namespace VoiceAgent.Harness;

/// <summary>
/// Decodes the same WAVs with NVIDIA Parakeet TDT 0.6B (sherpa-onnx native int8 export) so its
/// transcripts can be compared word for word with the streaming recognizer's on saved audio.
/// </summary>
public static class ParakeetBench
{
    private static readonly string Dir = Path.Combine(ModelPaths.Root, "parakeet-sherpa");

    public static Task<int> RunAsync(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
        {
            Console.Error.WriteLine("usage: VoiceAgent.Harness --parakeet-bench <file.wav>...");
            return Task.FromResult(1);
        }

        var encoder = Path.Combine(Dir, "encoder.int8.onnx");
        var decoder = Path.Combine(Dir, "decoder.int8.onnx");
        var joiner = Path.Combine(Dir, "joiner.int8.onnx");
        var tokens = Path.Combine(Dir, "tokens.txt");

        if (!File.Exists(encoder) || !File.Exists(decoder) || !File.Exists(joiner) || !File.Exists(tokens))
        {
            Console.Error.WriteLine($"parakeet model missing under {Dir}");
            return Task.FromResult(1);
        }

        var config = new OfflineRecognizerConfig();
        config.ModelConfig.Transducer.Encoder = encoder;
        config.ModelConfig.Transducer.Decoder = decoder;
        config.ModelConfig.Transducer.Joiner = joiner;
        config.ModelConfig.Tokens = tokens;
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.NumThreads = 2;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";

        using var recognizer = new OfflineRecognizer(config);
        Console.WriteLine($"parakeet: {Dir} (int8, 2 threads)");

        foreach (var file in files)
        {
            var pcm = Wav16k.Read(file);
            var audioMs = pcm.Length / 16.0;
            var samples = new float[pcm.Length];
            for (var i = 0; i < pcm.Length; i++) samples[i] = pcm[i] / 32768f;

            var sw = Stopwatch.StartNew();
            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform(16000, samples);
            recognizer.Decode(stream);
            var text = stream.Result.Text.Trim();
            sw.Stop();

            Console.WriteLine();
            Console.WriteLine($"=== {Path.GetFileName(file)} ({audioMs:F0} ms)");
            Console.WriteLine(
                $"  parakeet ({sw.Elapsed.TotalMilliseconds:F0} ms, " +
                $"{sw.Elapsed.TotalMilliseconds / audioMs:F2}x): {text}");

            var saved = Path.ChangeExtension(file, ".stream.txt");
            if (File.Exists(saved))
                Console.WriteLine($"  kroko    : {File.ReadAllText(saved).Trim()}");
        }

        return Task.FromResult(0);
    }
}
