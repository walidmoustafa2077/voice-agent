using VoiceAgent.Server.Pipeline;
using Xunit;

namespace VoiceAgent.Tests.Pipeline;

public class SttServiceTests
{
    [Fact]
    public async Task Transcribes_the_hello_fixture()
    {
        var pcm = Wav.ReadPcm16(Wav.Fixture("hello.wav"));
        using var stt = new SttService(ModelPath());

        var result = await stt.TranscribeAsync(pcm, CancellationToken.None);

        Assert.Contains("hello", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.LatencyMs > 0);
    }

    [Fact]
    public async Task Returns_empty_for_silence_without_running_whisper()
    {
        using var stt = new SttService(ModelPath());

        var result = await stt.TranscribeAsync(new short[32000], CancellationToken.None);

        Assert.Equal("", result.Text);
    }

    [Fact]
    public async Task Serializes_concurrent_calls()
    {
        var pcm = Wav.ReadPcm16(Wav.Fixture("hello.wav"));
        using var stt = new SttService(ModelPath());

        var results = await Task.WhenAll(
            stt.TranscribeAsync(pcm, CancellationToken.None),
            stt.TranscribeAsync(pcm, CancellationToken.None));

        Assert.All(results, r => Assert.Contains("hello", r.Text, StringComparison.OrdinalIgnoreCase));
    }

    private static string ModelPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VoiceAgent.slnx")))
        {
            dir = dir.Parent;
        }

        var root = dir?.FullName ?? AppContext.BaseDirectory;
        return Path.Combine(root, "models", "ggml-tiny.en.bin");
    }
}
