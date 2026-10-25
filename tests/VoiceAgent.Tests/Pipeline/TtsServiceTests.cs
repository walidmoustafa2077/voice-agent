using VoiceAgent.Server;
using VoiceAgent.Server.Audio;
using VoiceAgent.Server.Pipeline;

namespace VoiceAgent.Tests.Pipeline;

public sealed class TtsServiceTests
{
    [Fact]
    public async Task Produces_a_playable_wav_with_duration()
    {
        if (!ModelPaths.KokoroReady)
            return; // ~320 MB, downloaded on first run; observed separately when absent

        using var tts = new TtsService(ModelPaths.Kokoro);
        var chunk = await tts.SynthesizeAsync("Hello there.", CancellationToken.None);

        Assert.True(chunk.Wav.Length > 1000, $"expected audio, got {chunk.Wav.Length} bytes");
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(chunk.Wav, 0, 4));
        Assert.Equal(24000, BitConverter.ToInt32(chunk.Wav, 24)); // sample rate in fmt chunk
        Assert.InRange(chunk.DurationMs, 200, 5000);
    }

    [Fact]
    public async Task Honours_cancellation_before_work_starts()
    {
        if (!ModelPaths.KokoroReady)
            return;

        using var tts = new TtsService(ModelPaths.Kokoro);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tts.SynthesizeAsync("This should never be spoken.", cts.Token));
    }

    [Fact]
    public void Rejects_a_truncated_model_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kokoro-{Guid.NewGuid():N}.onnx");
        File.WriteAllBytes(path, new byte[1024]);

        try
        {
            var error = Assert.Throws<InvalidOperationException>(() => new TtsService(path));
            Assert.Contains("incomplete", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
