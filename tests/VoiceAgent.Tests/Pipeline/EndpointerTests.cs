using VoiceAgent.Server.Pipeline;

namespace VoiceAgent.Tests.Pipeline;

public sealed class EndpointerTests
{
    private const int Frame = 20;

    [Fact]
    public void Stable_text_and_silence_run_in_parallel()
    {
        // 400 ms is the aggressive case; production binds this to the VAD's own silence window
        // so an ordinary mid-sentence pause cannot cut the user off.
        var ep = new Endpointer(silenceMs: 400);

        // Speaking for 600 ms; the text settled 500 ms ago. Both counters now overlap.
        for (var t = 0; t < 600; t += Frame)
            Assert.False(ep.Push(true, "hello there", Frame));

        // Only 400 ms of silence is needed, not 400 + 350.
        for (var t = 0; t < 380; t += Frame)
            Assert.False(ep.Push(false, "hello there", Frame));

        Assert.True(ep.Push(false, "hello there", Frame));
    }

    [Fact]
    public void Changing_text_extends_past_the_silence()
    {
        var ep = new Endpointer(silenceMs: 400);
        for (var t = 0; t < 400; t += Frame) ep.Push(true, "hello", Frame);

        // Silence arrived, but the recognizer revises the text 300 ms into it.
        for (var t = 0; t < 300; t += Frame) Assert.False(ep.Push(false, "hello", Frame));
        Assert.False(ep.Push(false, "hello there", Frame));

        // Stability restarts at that revision; endpoint at 350 ms after it.
        var fired = false;
        for (var t = 0; t < 360; t += Frame) fired = ep.Push(false, "hello there", Frame);
        Assert.True(fired);
    }

    [Fact]
    public void Silence_before_any_speech_never_fires()
    {
        var ep = new Endpointer();
        for (var t = 0; t < 5000; t += Frame)
            Assert.False(ep.Push(false, "", Frame));
    }

    [Fact]
    public void A_recognizer_that_never_settles_is_cut_at_the_hard_cap()
    {
        var ep = new Endpointer();
        var firedAt = -1;

        for (var t = Frame; t < Endpointer.MaxUtteranceMs + 1000; t += Frame)
        {
            if (ep.Push(true, $"word {t}", Frame))
            {
                firedAt = t;
                break;
            }
        }

        Assert.InRange(firedAt, Endpointer.MaxUtteranceMs, Endpointer.MaxUtteranceMs + Frame);
    }
}
