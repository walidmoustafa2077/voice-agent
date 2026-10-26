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

    [Theory]
    [InlineData("AFTER EARLY NIGHTFALL THE LAMPS WOULD LIGHT UP", "After early nightfall the lamps would light up.")]
    [InlineData("HELLO THERE.", "Hello there.")]
    [InlineData("hello there", "Hello there.")]
    [InlineData("Hello there!", "Hello there!")]
    [InlineData("what is the time?", "What is the time?")]
    [InlineData("", "")]
    [InlineData("123", "123.")]
    public void Turns_recognizer_shouting_into_sentence_case(string raw, string expected) =>
        Assert.Equal(expected, TranscriptCleaner.SentenceCase(raw));

    [Fact]
    public void Sentence_case_is_idempotent() =>
        Assert.Equal(
            "After early nightfall the lamps would light up.",
            TranscriptCleaner.SentenceCase("After early nightfall the lamps would light up."));
}
