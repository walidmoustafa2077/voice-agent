using NAudio.Wave;
using VoiceAgent.Server.Audio;

namespace VoiceAgent.Harness;

/// <summary>
/// Loads any WAV the harness accepts into the 16 kHz mono PCM the pipeline works in.
/// Shared so the benchmark and the comparison tool cannot drift apart.
/// </summary>
public static class Wav16k
{
    public static short[] Read(string path)
    {
        using var reader = new AudioFileReader(path);
        var channels = reader.WaveFormat.Channels;
        var rate = reader.WaveFormat.SampleRate;
        var buffer = new float[rate / 50 * channels];
        var pcm = new List<short>();
        var resampler = new StreamingResampler(rate);

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
