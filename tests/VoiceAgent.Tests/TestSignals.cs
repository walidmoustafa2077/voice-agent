namespace VoiceAgent.Tests;

public static class TestSignals
{
    public static short[] Sine(double hz, int rate, double seconds)
    {
        var samples = (int)(rate * seconds);
        var data = new short[samples];
        for (var i = 0; i < samples; i++)
        {
            var value = Math.Sin(2 * Math.PI * hz * i / rate);
            data[i] = (short)(value * 12000);
        }
        return data;
    }

    public static double Rms(ReadOnlySpan<short> data)
    {
        if (data.Length == 0) return 0;
        double sum = 0;
        foreach (var s in data) sum += (double)s * s;
        return Math.Sqrt(sum / data.Length);
    }

    public static int MaxAbsDiff(ReadOnlySpan<short> a, ReadOnlySpan<short> b)
    {
        var max = 0;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            max = Math.Max(max, Math.Abs(a[i] - b[i]));
        }
        return max;
    }
}
