using System.Diagnostics;
using NAudio.Wave;
using VoiceAgent.Server;
using VoiceAgent.Server.Pipeline;

namespace VoiceAgent.Harness;

/// <summary>
/// Measures the integrated sherpa path the way the pipeline uses it: feed every 20 ms frame,
/// then time FinalizeAsync (tail padding + InputFinished) separately from Accept.
/// </summary>
public static class SttBench
{
    public static int Run(IReadOnlyList<string> files)
    {
        if (!ModelPaths.SherpaReady)
        {
            Console.Error.WriteLine("sherpa models missing");
            return 1;
        }

        using var stt = new SherpaStreamingStt(
            ModelPaths.SherpaEncoder, ModelPaths.SherpaDecoder,
            ModelPaths.SherpaJoiner, ModelPaths.SherpaTokens);

        foreach (var file in files)
        {
            var pcm = Read16k(file);
            using var stream = stt.Begin();

            var accept = Stopwatch.StartNew();
            for (var offset = 0; offset + 320 <= pcm.Length; offset += 320)
                stream.Accept(pcm.AsSpan(offset, 320));
            accept.Stop();

            var finalize = Stopwatch.StartNew();
            var result = stream.FinalizeAsync(CancellationToken.None).GetAwaiter().GetResult();
            finalize.Stop();

            var audioMs = pcm.Length / 16.0;
            var sw = Stopwatch.StartNew();
            var oneShot = stt.TranscribeAsync(pcm, CancellationToken.None).GetAwaiter().GetResult();
            sw.Stop();

            Console.WriteLine(
                $"{Path.GetFileName(file)} audio={audioMs:F0}ms accept={accept.Elapsed.TotalMilliseconds:F0}ms " +
                $"finalize={finalize.Elapsed.TotalMilliseconds:F0}ms " +
                $"rtf={(accept.Elapsed.TotalMilliseconds + finalize.Elapsed.TotalMilliseconds) / audioMs:F2}x " +
                $"decode={result.LatencyMs:F0}ms text=\"{result.Text}\"");
            Console.WriteLine(
                $"  one-shot transcribe={sw.Elapsed.TotalMilliseconds:F0}ms rtf={sw.Elapsed.TotalMilliseconds / audioMs:F2}x " +
                $"decode={oneShot.LatencyMs:F0}ms");
        }

        return 0;
    }

    private static short[] Read16k(string path)
    {
        using var reader = new AudioFileReader(path);
        var channels = reader.WaveFormat.Channels;
        var rate = reader.WaveFormat.SampleRate;
        var buffer = new float[rate / 50 * channels];
        var pcm = new List<short>();
        var resampler = new VoiceAgent.Server.Audio.StreamingResampler(rate);

        int read;
        while ((read = reader.Read(buffer.AsSpan())) > 0)
        {
            var mono = new short[read / channels];
            for (var i = 0; i < mono.Length; i++)
            {
                var sum = 0f;
                for (var c = 0; c < channels; c++) sum += buffer[i * channels + c];
                mono[i] = (short)Math.Clamp(sum / channels * 32767f, short.MinValue, short.MaxValue);
            }

            pcm.AddRange(rate == 16000 ? mono : resampler.Process(mono));
        }

        return [.. pcm];
    }
}
