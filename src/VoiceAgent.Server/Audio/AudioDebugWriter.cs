namespace VoiceAgent.Server.Audio;

/// <summary>
/// Saves exactly what the recognizer heard, so a bad transcript can be replayed offline instead
/// of guessed at. Enabled with <c>VOICEAGENT_DEBUG_AUDIO=1</c>; off by default because it writes
/// a WAV per turn.
/// </summary>
public sealed class AudioDebugWriter
{
    private readonly string? _directory;

    public AudioDebugWriter(bool enabled, string? directory = null)
    {
        if (!enabled) return;

        _directory = directory ?? DefaultDirectory;
    }

    /// <summary>Reads the environment once at construction; a session snapshots the switch.</summary>
    public static AudioDebugWriter FromEnvironment() => new(IsEnabled());

    public static bool IsEnabled()
    {
        var value = Environment.GetEnvironmentVariable("VOICEAGENT_DEBUG_AUDIO");

        return value is not null &&
            (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));
    }

    public bool Enabled => _directory is not null;

    public string? Directory => _directory;

    /// <summary><c>debug/</c> next to <c>models/</c>, which is gitignored.</summary>
    public static string DefaultDirectory =>
        Path.Combine(Path.GetDirectoryName(ModelPaths.Root) ?? AppContext.BaseDirectory, "debug");

    /// <summary>
    /// Writes <c>turn-{id}.wav</c> (the 16 kHz mono PCM the recognizer received) and
    /// <c>turn-{id}.stream.txt</c> (the transcript it produced). Failures are logged and
    /// swallowed: a debugging aid must never break a turn.
    /// </summary>
    public void Write(int turnId, short[] pcm16k, string transcript)
    {
        if (_directory is null) return;

        try
        {
            System.IO.Directory.CreateDirectory(_directory);

            using var wav = Pcm16Wav.Wrap(pcm16k);
            wav.Position = 0;
            File.WriteAllBytes(Path.Combine(_directory, $"turn-{turnId}.wav"), wav.ToArray());
            File.WriteAllText(Path.Combine(_directory, $"turn-{turnId}.stream.txt"), transcript);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"debug audio turn {turnId} failed: {ex.Message}");
        }
    }
}
