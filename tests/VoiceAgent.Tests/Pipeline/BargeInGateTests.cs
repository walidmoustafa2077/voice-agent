using VoiceAgent.Server.Pipeline;

namespace VoiceAgent.Tests.Pipeline;

public sealed class BargeInGateTests
{
    [Fact]
    public void Fires_after_250ms_of_sustained_speech()
    {
        var gate = new BargeInGate(250);

        for (var i = 0; i < 12; i++)
            Assert.False(gate.Push(true, 20), $"fired early at {20 * (i + 1)} ms");

        Assert.True(gate.Push(true, 20)); // 260 ms
    }

    [Fact]
    public void Does_not_fire_on_a_150ms_burst()
    {
        var gate = new BargeInGate(250);

        for (var i = 0; i < 7; i++)
            Assert.False(gate.Push(true, 20));

        Assert.False(gate.Push(false, 20));
        Assert.False(gate.Push(true, 20));
    }

    [Fact]
    public void Does_not_fire_twice_without_reset()
    {
        var gate = new BargeInGate(60);

        Assert.False(gate.Push(true, 20));
        Assert.False(gate.Push(true, 20));
        Assert.True(gate.Push(true, 20));
        Assert.False(gate.Push(true, 20));

        gate.Reset();

        Assert.False(gate.Push(true, 20));
        Assert.False(gate.Push(true, 20));
        Assert.True(gate.Push(true, 20));
    }
}

public sealed class EchoFloorGateTests
{
    [Fact]
    public void Calibrates_the_floor_during_the_first_200ms_of_playback()
    {
        var gate = new EchoFloorGate();
        gate.BeginPlayback();

        for (var i = 0; i < 10; i++)
            Assert.False(gate.IsUserSpeech(Tone(0.02), 0.95f));

        Assert.InRange(gate.Floor, 0.018f, 0.022f);
    }

    [Fact]
    public void Rejects_echo_at_or_below_the_floor_even_when_vad_says_speech()
    {
        var gate = new EchoFloorGate();
        gate.BeginPlayback();

        for (var i = 0; i < 10; i++)
            gate.IsUserSpeech(Tone(0.02), 0.95f);

        Assert.False(gate.IsUserSpeech(Tone(0.03), 0.95f)); // 0.03 < 0.02 * 2.5
    }

    [Fact]
    public void Accepts_a_voice_well_above_the_floor()
    {
        var gate = new EchoFloorGate();
        gate.BeginPlayback();

        for (var i = 0; i < 10; i++)
            gate.IsUserSpeech(Tone(0.02), 0.95f);

        Assert.True(gate.IsUserSpeech(Tone(0.2), 0.95f));
    }

    [Fact]
    public void Ignores_a_signal_the_vad_does_not_believe_is_speech()
    {
        var gate = new EchoFloorGate();
        gate.EndPlayback();

        Assert.False(gate.IsUserSpeech(Tone(0.5), 0.3f));
    }

    private static short[] Tone(double rms, int samples = 320)
    {
        var amplitude = rms * Math.Sqrt(2) * 32767;
        var pcm = new short[samples];

        for (var i = 0; i < samples; i++)
            pcm[i] = (short)(amplitude * Math.Sin(2 * Math.PI * 200 * i / 16000.0));

        return pcm;
    }
}
