using VoiceAgent.Server.Pipeline;
using Xunit;

namespace VoiceAgent.Tests.Pipeline;

public class SpeechSegmenterTests
{
    [Fact]
    public void Emits_start_then_end_after_600ms_of_silence()
    {
        var events = Feed(Speech(500, 1500), seconds: 3.0);

        Assert.Equal(2, events.Count);
        Assert.True(events[0].IsStart);
        Assert.False(events[1].IsStart);
        Assert.InRange(events[0].SampleIndex, 7000, 9000);
    }

    [Fact]
    public void Discards_a_150ms_burst()
    {
        var events = Feed(Speech(500, 650), seconds: 2.0);

        Assert.Empty(events);
    }

    [Fact]
    public void Emitted_pcm_includes_the_150ms_preroll()
    {
        var events = Feed(Speech(1000, 1600), seconds: 3.0);

        var pcm = events[1].Pcm!;
        Assert.InRange(pcm.Length, (int)(0.69 * 16000), (int)(0.81 * 16000));
    }

    [Fact]
    public void Exposes_per_window_speech_decisions()
    {
        var segmenter = new SpeechSegmenter(Speech(0, 1000), new VadOptions());

        SegmenterFrame? last = null;
        for (var i = 0; i < 45; i++) last = segmenter.Push(Frame());

        Assert.True(last!.IsSpeech);
        Assert.True(last.Probability > 0.9f);

        var quiet = segmenter.Push(Frame());
        while (quiet.IsSpeech) quiet = segmenter.Push(Frame());
        Assert.False(quiet.IsSpeech);
    }

    [Fact]
    public void Caps_an_utterance_at_15_seconds()
    {
        var events = Feed(Speech(0, 20000), seconds: 22.0);

        var end = events.LastOrDefault(e => !e.IsStart);
        Assert.NotNull(end);
        Assert.True(end!.Pcm!.Length <= (int)(15.2 * 16000));
    }

    private static short[] Frame() => TestSignals.Sine(200, 16000, 0.02);

    private static ScriptedVad Speech(int startMs, int endMs) =>
        new(ms => ms >= startMs && ms < endMs);

    private static List<SegmentEvent> Feed(ScriptedVad vad, double seconds)
    {
        var segmenter = new SpeechSegmenter(vad, new VadOptions());
        var events = new List<SegmentEvent>();
        var frames = (int)(seconds * 50);

        for (var i = 0; i < frames; i++)
        {
            events.AddRange(segmenter.Push(Frame()).Events);
        }

        return events;
    }

    private sealed class ScriptedVad(Func<int, bool> speechAt) : IVadModel
    {
        private int _ms;

        public float Predict(ReadOnlySpan<short> window)
        {
            var speaking = speechAt(_ms + 16);
            _ms += 32;
            return speaking ? 0.95f : 0.02f;
        }

        public void Reset() => _ms = 0;
    }
}
