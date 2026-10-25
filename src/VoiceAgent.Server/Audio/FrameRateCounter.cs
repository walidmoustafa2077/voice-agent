using System.Diagnostics;

namespace VoiceAgent.Server.Audio;

/// <summary>Frames captured in the last second. One instance per connection.</summary>
public sealed class FrameRateCounter
{
    private static readonly long WindowTicks = Stopwatch.Frequency;
    private readonly Queue<long> _ticks = new();

    public void Add(int frames)
    {
        var now = Stopwatch.GetTimestamp();
        for (var i = 0; i < frames; i++) _ticks.Enqueue(now);
        Trim(now);
    }

    public double FramesPerSecond
    {
        get
        {
            Trim(Stopwatch.GetTimestamp());
            return _ticks.Count;
        }
    }

    private void Trim(long now)
    {
        while (_ticks.Count > 0 && now - _ticks.Peek() > WindowTicks) _ticks.Dequeue();
    }
}
