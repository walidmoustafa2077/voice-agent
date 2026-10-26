using VoiceAgent.Server.Pipeline;

namespace VoiceAgent.Tests.Pipeline;

public sealed class SapiTtsServiceTests
{
    [Fact]
    public async Task Renders_a_wav_with_a_positive_duration()
    {
        if (!OperatingSystem.IsWindows()) return; // SAPI is a Windows component

        using var tts = new SapiTtsService();

        var chunk = await tts.SynthesizeAsync("Testing one two three.", CancellationToken.None);

        Assert.True(chunk.Wav.Length > 44, "expected a WAV body, not just a header");
        Assert.True(chunk.DurationMs > 300, $"duration was {chunk.DurationMs} ms");
    }

    [Fact]
    public async Task Cancel_before_synthesis_never_returns_audio()
    {
        if (!OperatingSystem.IsWindows()) return; // SAPI is a Windows component

        using var tts = new SapiTtsService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            await tts.SynthesizeAsync("This must never be spoken.", cts.Token);
            Assert.Fail("expected the pre-call cancellation check to throw");
        }
        catch (OperationCanceledException)
        {
            // expected: no COM call and no audio for a dead turn
        }
    }
}
