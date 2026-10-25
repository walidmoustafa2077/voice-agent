using VoiceAgent.Server.Audio;
using Xunit;

namespace VoiceAgent.Tests.Audio;

public class StreamingResamplerTests
{
    [Fact]
    public void One_shot_44100_to_16000_preserves_a_1khz_tone()
    {
        var input = TestSignals.Sine(1000, 44100, seconds: 1.0);
        var output = new StreamingResampler(44100).Process(input);

        Assert.InRange(output.Length, 16000 - 32, 16000 + 32);
        Assert.InRange(
            TestSignals.Rms(output),
            TestSignals.Rms(input) * 0.95,
            TestSignals.Rms(input) * 1.05);
    }

    [Fact]
    public void Streaming_in_twenty_ms_frames_matches_one_shot()
    {
        var input = TestSignals.Sine(1000, 44100, seconds: 1.0);
        var oneShot = new StreamingResampler(44100).Process(input);

        var streamed = new StreamingResampler(44100);
        var frames = input.Chunk(882).SelectMany(streamed.Process).ToArray();

        Assert.Equal(oneShot.Length, frames.Length);
        Assert.InRange(TestSignals.MaxAbsDiff(oneShot, frames), 0, 2);
    }

    [Fact]
    public void Attenuates_a_10khz_tone_above_the_new_nyquist()
    {
        var input = TestSignals.Sine(10000, 48000, seconds: 1.0);
        var output = new StreamingResampler(48000).Process(input);

        Assert.True(
            TestSignals.Rms(output) < TestSignals.Rms(input) * 0.2,
            $"expected attenuation, got {TestSignals.Rms(output):F1} vs {TestSignals.Rms(input):F1}");
    }

    [Fact]
    public void Passes_16k_through_unchanged()
    {
        var input = TestSignals.Sine(440, 16000, seconds: 0.5);
        var output = new StreamingResampler(16000).Process(input);

        Assert.Equal(input, output);
    }
}
