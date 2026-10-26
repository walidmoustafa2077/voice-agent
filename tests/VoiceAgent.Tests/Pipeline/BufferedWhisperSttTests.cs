using VoiceAgent.Server;
using VoiceAgent.Server.Pipeline;
using Xunit;

namespace VoiceAgent.Tests.Pipeline;

/// <summary>
/// Step 1 of the streaming-STT migration: the buffered implementation must be indistinguishable
/// from the old whole-utterance path, so the rest of the pipeline can move to a stream API safely.
/// </summary>
public sealed class BufferedWhisperSttTests
{
    [Fact]
    public async Task Matches_the_whole_utterance_transcript()
    {
        var pcm = Wav.ReadPcm16(Wav.Fixture("hello.wav"));
        using var whisper = new SttService(ModelPaths.WhisperTinyEn);
        using var buffered = new BufferedWhisperStt(whisper);

        var expected = await whisper.TranscribeAsync(pcm, CancellationToken.None);
        var actual = await buffered.TranscribeAsync(pcm, CancellationToken.None);

        Assert.Contains("hello", actual.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expected.Text, actual.Text);
    }

    [Fact]
    public async Task Emits_no_partial_before_finalize()
    {
        var pcm = Wav.ReadPcm16(Wav.Fixture("hello.wav"));
        using var whisper = new SttService(ModelPaths.WhisperTinyEn);
        using var buffered = new BufferedWhisperStt(whisper);
        using var stream = buffered.Begin();

        stream.Accept(pcm);

        // Buffered mode has no recognizer running while the user talks; the first text is the
        // final one. The pipeline relies on that to avoid flickering partials.
        Assert.Equal("", stream.Current.Text);
        Assert.False(stream.Current.Endpoint);

        var result = await stream.FinalizeAsync(CancellationToken.None);
        Assert.Contains("hello", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(result.Text, stream.Current.Text);
        Assert.True(stream.Current.ChangeSeq > 0);
    }

    [Fact]
    public async Task Silence_finalizes_to_empty_without_running_whisper()
    {
        using var whisper = new SttService(ModelPaths.WhisperTinyEn);
        using var buffered = new BufferedWhisperStt(whisper);
        using var stream = buffered.Begin();

        var empty = await stream.FinalizeAsync(CancellationToken.None);
        Assert.Equal("", empty.Text);

        stream.Accept(new short[16000]);
        var silent = await stream.FinalizeAsync(CancellationToken.None);
        Assert.Equal("", silent.Text);
    }

    [Fact]
    public async Task A_cancelled_token_aborts_before_transcribing()
    {
        var pcm = Wav.ReadPcm16(Wav.Fixture("hello.wav"));
        using var whisper = new SttService(ModelPaths.WhisperTinyEn);
        using var buffered = new BufferedWhisperStt(whisper);
        using var stream = buffered.Begin();
        stream.Accept(pcm);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => stream.FinalizeAsync(cts.Token));
    }
}
