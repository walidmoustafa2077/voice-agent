using KokoroSharp;
using KokoroSharp.Core;
using VoiceAgent.Server.Audio;

namespace VoiceAgent.Server.Pipeline;

public interface ITtsService
{
    Task<TtsChunk> SynthesizeAsync(string text, CancellationToken ct);
}

public sealed record TtsChunk(byte[] Wav, double DurationMs);

/// <summary>Kokoro synthesis to a self-contained WAV, with no playback side effects.</summary>
public sealed class TtsService : ITtsService, IDisposable
{
    public const int SampleRate = 24000;

    private readonly KokoroWavSynthesizer _synth;
    private readonly KokoroVoice _voice;

    public TtsService(string modelPath, int threads = 4)
    {
        var length = File.Exists(modelPath) ? new FileInfo(modelPath).Length : 0;
        if (length < ModelPaths.KokoroMinBytes)
        {
            throw new InvalidOperationException(
                $"kokoro.onnx is incomplete or missing ({length / (1024 * 1024)} MB of ~320 MB). " +
                "Delete it and re-run to finish the download.");
        }

        var options = new Microsoft.ML.OnnxRuntime.SessionOptions
        {
            IntraOpNumThreads = threads,
            InterOpNumThreads = 1,
        };

        _synth = new KokoroWavSynthesizer(modelPath, options);
        _voice = KokoroVoiceManager.GetVoice("af_heart");
    }

    public async Task<TtsChunk> SynthesizeAsync(string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Kokoro emits raw PCM16 mono at 24 kHz, not a WAV container.
        var pcm = await _synth.SynthesizeAsync(text, _voice).WaitAsync(ct);
        ct.ThrowIfCancellationRequested();

        var samples = new short[pcm.Length / 2];
        Buffer.BlockCopy(pcm, 0, samples, 0, samples.Length * 2);

        var wav = Pcm16Wav.Wrap(samples, SampleRate).ToArray();
        return new TtsChunk(wav, samples.Length * 1000.0 / SampleRate);
    }

    public void Dispose() => _synth.Dispose();
}
