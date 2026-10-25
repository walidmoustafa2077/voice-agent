using System.Text;

namespace VoiceAgent.Server.Audio;

/// <summary>Wraps raw PCM16 in a WAV container. Whisper.net requires a WAV stream.</summary>
public static class Pcm16Wav
{
    public const int HeaderBytes = 44;

    public static MemoryStream Wrap(short[] pcm, int sampleRate = 16000)
    {
        var stream = new MemoryStream(HeaderBytes + pcm.Length * 2);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        var dataBytes = pcm.Length * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        foreach (var sample in pcm) writer.Write(sample);

        stream.Position = 0;
        return stream;
    }

    /// <summary>Duration read from the data chunk, for the playback watchdog.</summary>
    public static double DurationMs(byte[] wav)
    {
        var position = 12;
        while (position + 8 <= wav.Length)
        {
            var id = Encoding.ASCII.GetString(wav, position, 4);
            var size = BitConverter.ToInt32(wav, position + 4);
            if (id == "fmt ")
            {
                var byteRate = BitConverter.ToInt32(wav, position + 16);
                return byteRate <= 0 ? 0 : size * 1000.0 / byteRate;
            }
            position += 8 + size + (size % 2);
        }
        return 0;
    }
}
