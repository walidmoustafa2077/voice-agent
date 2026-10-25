using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VoiceAgent.Server.Pipeline;

/// <summary>
/// Silero VAD v5. Each call takes the previous 64 samples as context plus the 512-sample window.
/// </summary>
public sealed class SileroVadModel : IVadModel, IDisposable
{
    public const int ContextSamples = 64;
    private const int WindowSamples = 512;
    private const int StateSize = 2 * 1 * 128;

    private readonly InferenceSession _session;
    private readonly long[] _sampleRate = [16000];
    private readonly short[] _context = new short[ContextSamples];
    private float[] _state = new float[StateSize];

    public SileroVadModel(string modelPath)
    {
        _session = new InferenceSession(modelPath);
    }

    public float Predict(ReadOnlySpan<short> window512)
    {
        if (window512.Length != WindowSamples)
        {
            throw new ArgumentException($"Expected {WindowSamples} samples, got {window512.Length}.");
        }

        var input = new float[ContextSamples + WindowSamples];
        for (var i = 0; i < ContextSamples; i++) input[i] = _context[i] / 32768f;
        for (var i = 0; i < WindowSamples; i++) input[ContextSamples + i] = window512[i] / 32768f;

        var inputs = new[]
        {
            NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>(input, [1, ContextSamples + WindowSamples])),
            NamedOnnxValue.CreateFromTensor("state", new DenseTensor<float>(_state, [2, 1, 128])),
            NamedOnnxValue.CreateFromTensor("sr", new DenseTensor<long>(_sampleRate, [1])),
        };

        using var results = _session.Run(inputs);
        var probability = results[0].AsEnumerable<float>().First();
        _state = results[1].AsEnumerable<float>().ToArray();

        window512[^ContextSamples..].CopyTo(_context);
        return probability;
    }

    public float MaxProbability(short[] pcm)
    {
        var max = 0f;
        for (var i = 0; i + WindowSamples <= pcm.Length; i += WindowSamples)
        {
            max = Math.Max(max, Predict(pcm.AsSpan(i, WindowSamples)));
        }
        return max;
    }

    public void Reset()
    {
        Array.Clear(_context);
        Array.Clear(_state);
    }

    public void Dispose() => _session.Dispose();
}
