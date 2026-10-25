using VoiceAgent.Server.Pipeline;
using Xunit;

namespace VoiceAgent.Tests.Pipeline;

public class TranscriptCleanerTests
{
    [Theory]
    [InlineData("[BLANK_AUDIO]", "")]
    [InlineData("(silence)", "")]
    [InlineData("  ...  ", "")]
    [InlineData("[Music] hello there", "hello there")]
    [InlineData("  hello   world  ", "hello world")]
    [InlineData("Hello. (coughs) How are you?", "Hello. How are you?")]
    public void Strips_whisper_artifacts(string raw, string expected) =>
        Assert.Equal(expected, TranscriptCleaner.Clean(raw));

    [Fact]
    public void Detects_digital_silence() =>
        Assert.True(TranscriptCleaner.IsSilent(new short[32000]));

    [Fact]
    public void Does_not_call_speech_silent() =>
        Assert.False(TranscriptCleaner.IsSilent(TestSignals.Sine(200, 16000, 1.0)));
}
