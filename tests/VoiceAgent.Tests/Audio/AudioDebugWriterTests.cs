using VoiceAgent.Server.Audio;

namespace VoiceAgent.Tests.Audio;

public sealed class AudioDebugWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "voiceagent-debug-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void Disabled_writes_nothing()
    {
        var writer = new AudioDebugWriter(enabled: false, _dir);

        writer.Write(1, [1, 2, 3], "hello");

        Assert.False(writer.Enabled);
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public void Enabled_writes_the_exact_pcm_as_a_wav_and_the_transcript_beside_it()
    {
        var writer = new AudioDebugWriter(enabled: true, _dir);
        short[] pcm = [0, 1000, -1000, 32767];

        writer.Write(7, pcm, "hello there");

        Assert.True(writer.Enabled);
        Assert.Equal(_dir, writer.Directory);

        var wav = File.ReadAllBytes(Path.Combine(_dir, "turn-7.wav"));
        Assert.Equal(Pcm16Wav.HeaderBytes + pcm.Length * 2, wav.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));

        Assert.Equal("hello there", File.ReadAllText(Path.Combine(_dir, "turn-7.stream.txt")));
    }

    [Fact]
    public void An_unwritable_directory_does_not_throw()
    {
        // A debugging aid must never be able to fail a turn.
        Directory.CreateDirectory(_dir);
        var blocked = Path.Combine(_dir, "blocked");
        File.WriteAllText(blocked, "a file where the directory should be");

        var writer = new AudioDebugWriter(enabled: true, blocked);

        writer.Write(1, [1, 2, 3], "hello");
    }
}
