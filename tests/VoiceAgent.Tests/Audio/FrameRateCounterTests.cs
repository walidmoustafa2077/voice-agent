using VoiceAgent.Server.Audio;
using Xunit;

namespace VoiceAgent.Tests.Audio;

public class FrameRateCounterTests
{
    [Fact]
    public void Fifty_20ms_frames_read_as_fifty_per_second()
    {
        var counter = new FrameRateCounter();

        for (var i = 0; i < 50; i++) counter.Add(1);

        Assert.InRange(counter.FramesPerSecond, 48, 52);
    }
}
