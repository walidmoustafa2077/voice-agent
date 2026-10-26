using VoiceAgent.Server;
using VoiceAgent.Server.Pipeline;
using Xunit;

namespace VoiceAgent.Tests.Pipeline;

/// <summary>
/// The sherpa-onnx path, including the pinned-pairing guard: both this recognizer and Silero load a
/// native onnxruntime in the same process, and only the Microsoft package's copy is used.
/// </summary>
public sealed class SherpaStreamingSttTests
{
    [Fact]
    public async Task Finalizes_the_hello_fixture()
    {
        if (!ModelPaths.SherpaReady) return;

        using var stt = Create();
        var pcm = Wav.ReadPcm16(Wav.Fixture("hello.wav"));

        var result = await stt.TranscribeAsync(pcm, CancellationToken.None);

        Assert.Contains("hello", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.LatencyMs > 0, "decode time should be measured");
    }

    [Fact]
    public void Emits_partials_while_audio_arrives()
    {
        if (!ModelPaths.SherpaReady) return;

        using var stt = Create();
        using var stream = stt.Begin();
        var pcm = Wav.ReadPcm16(Wav.Fixture("hello.wav"));

        // Feed about two thirds of the clip in 20 ms frames, exactly as the capture pump would.
        var twoThirds = pcm.Length * 2 / 3;
        for (var offset = 0; offset < twoThirds; offset += SherpaStreamingStt.FrameSamples)
        {
            var count = Math.Min(SherpaStreamingStt.FrameSamples, twoThirds - offset);
            stream.Accept(pcm.AsSpan(offset, count));
        }

        var snapshot = stream.Current;
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Text), "a streaming recognizer must have text before EOF");
        Assert.True(snapshot.ChangeSeq > 0);
        Assert.False(snapshot.Endpoint);
    }

    [Fact]
    public async Task Silence_finalizes_to_empty()
    {
        if (!ModelPaths.SherpaReady) return;

        using var stt = Create();
        var result = await stt.TranscribeAsync(new short[16000], CancellationToken.None);

        Assert.Equal("", result.Text);
    }

    [Fact]
    public async Task Coexists_with_silero_in_one_process()
    {
        if (!ModelPaths.SherpaReady || !File.Exists(ModelPaths.SileroVad)) return;

        // Both native stacks must be loadable together: this is the guard for the pinned
        // onnxruntime pairing (sherpa ships its own DLL; Microsoft's copy wins the build).
        using var vad = new SileroVadModel(ModelPaths.SileroVad);
        using var stt = Create();

        var pcm = Wav.ReadPcm16(Wav.Fixture("hello.wav"));
        var result = await stt.TranscribeAsync(pcm, CancellationToken.None);

        Assert.Contains("hello", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_cancelled_token_aborts_before_finalizing()
    {
        if (!ModelPaths.SherpaReady) return;

        using var stt = Create();
        using var stream = stt.Begin();
        stream.Accept(Wav.ReadPcm16(Wav.Fixture("hello.wav")));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => stream.FinalizeAsync(cts.Token));
    }

    private static SherpaStreamingStt Create() => new(
        ModelPaths.SherpaEncoder,
        ModelPaths.SherpaDecoder,
        ModelPaths.SherpaJoiner,
        ModelPaths.SherpaTokens);
}
