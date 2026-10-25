namespace VoiceAgent.Server.Audio;

/// <summary>
/// Stateful 16 kHz resampler. Biquad state and interpolation phase survive across calls,
/// so 20 ms frames produce the same output as one long buffer.
/// </summary>
public sealed class StreamingResampler
{
    private const int OutputRate = 16000;
    private const double CutoffHz = 7000;
    private static readonly double[] SectionQ = [0.50980, 0.60134, 0.89998, 2.56292];

    private readonly bool _passthrough;
    private readonly double _step;
    private readonly Biquad[] _sections;

    private double _prev;
    private double _phase;
    private bool _primed;

    public StreamingResampler(int inputRate)
    {
        if (inputRate <= 0) throw new ArgumentOutOfRangeException(nameof(inputRate));
        _passthrough = inputRate == OutputRate;
        _step = (double)inputRate / OutputRate;
        _sections = [.. SectionQ.Select(q => Biquad.LowPass(inputRate, CutoffHz, q))];
    }

    public short[] Process(short[] input) => Process(input.AsSpan());

    public short[] Process(ReadOnlySpan<short> input)
    {
        if (_passthrough) return input.ToArray();

        var output = new List<short>((int)(input.Length / _step) + 2);

        foreach (var sample in input)
        {
            var filtered = (double)sample;
            foreach (var section in _sections) filtered = section.Process(filtered);

            if (!_primed)
            {
                _prev = filtered;
                _primed = true;
            }

            while (_phase < 1.0)
            {
                var interpolated = _prev + (filtered - _prev) * _phase;
                output.Add((short)Math.Round(Math.Clamp(interpolated, short.MinValue, short.MaxValue)));
                _phase += _step;
            }

            _phase -= 1.0;
            _prev = filtered;
        }

        return [.. output];
    }

    private sealed class Biquad
    {
        private readonly double _b0, _b1, _b2, _a1, _a2;
        private double _x1, _x2, _y1, _y2;

        private Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
        {
            _b0 = b0 / a0;
            _b1 = b1 / a0;
            _b2 = b2 / a0;
            _a1 = a1 / a0;
            _a2 = a2 / a0;
        }

        public static Biquad LowPass(int rate, double cutoffHz, double q)
        {
            var w0 = 2 * Math.PI * cutoffHz / rate;
            var cos = Math.Cos(w0);
            var alpha = Math.Sin(w0) / (2 * q);

            return new Biquad(
                (1 - cos) / 2,
                1 - cos,
                (1 - cos) / 2,
                1 + alpha,
                -2 * cos,
                1 - alpha);
        }

        public double Process(double x)
        {
            var y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1;
            _x1 = x;
            _y2 = _y1;
            _y1 = y;
            return y;
        }
    }
}
