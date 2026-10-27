using SherpaOnnx;
using VoiceAgent.Server.Audio;

namespace VoiceAgent.Server.Pipeline;

/// <summary>
/// Supertonic 3 (int8) through sherpa-onnx's OfflineTts. It is a flow-matching model whose
/// quality measured close to Kokoro while decoding several times faster on a CPU, which matters
/// here because synthesis shares four cores with the LLM stream and the recognizer.
/// </summary>
public sealed class SupertonicTtsService : ITtsService, IDisposable
{
    private readonly OfflineTts _tts;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SupertonicTtsService(string directory, int threads = 2)
    {
        var config = new OfflineTtsConfig();

        config.Model.Supertonic.DurationPredictor = Path.Combine(directory, "duration_predictor.int8.onnx");
        config.Model.Supertonic.TextEncoder = Path.Combine(directory, "text_encoder.int8.onnx");
        config.Model.Supertonic.VectorEstimator = Path.Combine(directory, "vector_estimator.int8.onnx");
        config.Model.Supertonic.Vocoder = Path.Combine(directory, "vocoder.int8.onnx");
        config.Model.Supertonic.TtsJson = Path.Combine(directory, "tts.json");
        config.Model.Supertonic.UnicodeIndexer = Path.Combine(directory, "unicode_indexer.bin");
        config.Model.Supertonic.VoiceStyle = Path.Combine(directory, "voice.bin");

        config.Model.NumThreads = threads;
        config.Model.Provider = "cpu";
        config.Model.Debug = 0;

        // The pipeline already splits the reply into sentences; letting sherpa split again would
        // return several utterances per call and break the chunk timing the player relies on.
        config.MaxNumSentences = 1;

        _tts = new OfflineTts(config);
    }

    public int SampleRate => _tts.SampleRate;

    public async Task<TtsChunk> SynthesizeAsync(string text, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();

            // Generate is synchronous and CPU-bound; keep it off the caller's thread so a
            // cancelled turn can still unwind while the model finishes its current sentence.
            var audio = await Task.Run(() => _tts.Generate(text, 1.0f, 0), ct);

            var rate = audio.SampleRate > 0 ? audio.SampleRate : _tts.SampleRate;
            var samples = audio.Samples;
            var pcm = new short[samples.Length];

            for (var i = 0; i < samples.Length; i++)
                pcm[i] = (short)Math.Clamp(samples[i] * 32767f, short.MinValue, short.MaxValue);

            using var wav = Pcm16Wav.Wrap(pcm, rate);
            return new TtsChunk(wav.ToArray(), pcm.Length * 1000.0 / rate);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
        _tts.Dispose();
    }
}
