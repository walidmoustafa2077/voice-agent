using System.Diagnostics;
using NAudio.Wave;
using VoiceAgent.Server;
using VoiceAgent.Server.Pipeline;

namespace VoiceAgent.Harness;

/// <summary>
/// Task 0 follow-up: is Kokoro's 1.53x realtime a thread-configuration problem, and is a lighter
/// local engine materially faster on the same sentence? Read-only measurement, no pipeline change.
/// </summary>
public static class TtsBench
{
    public const string Sentence =
        "I can help with that. Let me check the details and get back to you.";

    public static async Task<int> RunAsync()
    {
        Console.WriteLine($"sentence: \"{Sentence}\" ({Sentence.Length} chars)");
        Console.WriteLine();

        Console.WriteLine("Kokoro af_heart, intra-op threads:");
        foreach (var threads in new[] { 1, 2, 3, 4 })
        {
            using var tts = new TtsService(ModelPaths.Kokoro, threads);

            // One warm-up synthesis so graph allocation is not part of the measurement.
            var warm = await tts.SynthesizeAsync("Ready.", CancellationToken.None);
            Console.WriteLine($"  warm-up {warm.DurationMs:F0} ms audio");

            var walls = new List<double>();
            var audioMs = 0.0;

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
            var rtf = audioMs > 1 ? median / audioMs : 0;
            Console.WriteLine(
                $"  threads={threads} wall={median:F0} ms audio={audioMs:F0} ms rtf={rtf:F2}x " +
                $"(runs {string.Join('/', walls.Select(w => w.ToString("F0")))})");
        }

        Console.WriteLine();
        Console.WriteLine("Windows SAPI (offline OS voice):");

        if (OperatingSystem.IsWindows())
        {
            try
            {
                var wall = SapiSynthesize(Sentence, out var audioMs, out var voice);
                var rtf = audioMs > 1 ? wall / audioMs : 0;
                Console.WriteLine($"  voice={voice} wall={wall:F0} ms audio={audioMs:F0} ms rtf={rtf:F2}x");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  unavailable: {ex.GetType().Name}: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine("  unavailable: not Windows");
        }

        return 0;
    }

    /// <summary>Renders through the built-in SAPI voice to a temp WAV; returns wall ms.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static double SapiSynthesize(string text, out double audioMs, out string voice)
    {
        var wav = Path.Combine(Path.GetTempPath(), $"voice-agent-sapi-{Guid.NewGuid():N}.wav");

        var voiceType = Type.GetTypeFromProgID("SAPI.SpVoice")
            ?? throw new InvalidOperationException("SAPI.SpVoice is not registered");
        var streamType = Type.GetTypeFromProgID("SAPI.SpFileStream")
            ?? throw new InvalidOperationException("SAPI.SpFileStream is not registered");

        dynamic speaker = Activator.CreateInstance(voiceType)!;
        dynamic stream = Activator.CreateInstance(streamType)!;

        // SSFMCreateForWrite = 3, do not autobind the default device.
        stream.Open(wav, 3, false);
        speaker.AudioOutputStream = stream;
        voice = (string)speaker.Voice.GetDescription();

        var sw = Stopwatch.StartNew();
        speaker.Speak(text);
        sw.Stop();

        // The RCW keeps the WAV open even after Close(), so release the COM objects and
        // let the finalizers run before reading the file back.
        speaker.AudioOutputStream = null;
        stream.Close();

        System.Runtime.InteropServices.Marshal.FinalReleaseComObject(stream);
        System.Runtime.InteropServices.Marshal.FinalReleaseComObject(speaker);
        GC.Collect();
        GC.WaitForPendingFinalizers();

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var reader = new AudioFileReader(wav);
                audioMs = reader.TotalTime.TotalMilliseconds;
                break;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(50);
            }
        }

        File.Delete(wav);

        return sw.Elapsed.TotalMilliseconds;
    }
}
