using VoiceAgent.Server.Audio;

namespace VoiceAgent.Tests.Audio;

public sealed class Pcm16WavTests
{
    [Fact]
    public void Duration_comes_from_the_data_chunk_size()
    {
        var wav = Pcm16Wav.Wrap(new short[24000], sampleRate: 24000).ToArray();

        Assert.InRange(Pcm16Wav.DurationMs(wav), 999, 1001);
    }
}
