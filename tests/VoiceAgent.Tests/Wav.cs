using System.Text;

namespace VoiceAgent.Tests;

internal static class Wav
{
    /// <summary>Reads the first data chunk as 16-bit PCM.</summary>
    public static short[] ReadPcm16(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var position = 12;

        while (position + 8 <= bytes.Length)
        {
            var id = Encoding.ASCII.GetString(bytes, position, 4);
            var size = BitConverter.ToInt32(bytes, position + 4);

            if (id == "data")
            {
                var start = position + 8;
                var count = Math.Min(size, bytes.Length - start) / 2;
                var samples = new short[count];
                Buffer.BlockCopy(bytes, start, samples, 0, count * 2);
                return samples;
            }

            position += 8 + size + (size % 2);
        }

        throw new InvalidDataException($"No data chunk in {path}.");
    }

    public static string Fixture(string name) =>
        Path.Combine(Path.GetDirectoryName(typeof(Wav).Assembly.Location)!, "Fixtures", name);
}
