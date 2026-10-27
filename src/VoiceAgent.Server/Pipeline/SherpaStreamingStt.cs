using System.Diagnostics;
using SherpaOnnx;

namespace VoiceAgent.Server.Pipeline;

/// <summary>
/// Streaming Zipformer transducer (int8, chunk 16 / left 64). Decode happens as audio arrives;
/// only the tail is left at finalize. The recognizer is built once per process and shared by all
/// connections, so the 3 decoder threads are a process-wide budget.
/// </summary>
public sealed class SherpaStreamingStt : IStreamingStt, ISttService
{
    public const int SampleRate = 16000;
    public const int FrameSamples = 320; // 20 ms
    public const int TailPaddingMs = 1000;
    public const int Threads = 3;

    private readonly OnlineRecognizer _recognizer;

    public SherpaStreamingStt(
        string encoder, string decoder, string joiner, string tokens, int threads = Threads,
        string? hotwordsFile = null, string? bpeVocab = null, float hotwordsScore = 1.5f,
        bool beamSearch = false)
    {
        var config = new OnlineRecognizerConfig();
        config.FeatConfig.SampleRate = SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = encoder;
        config.ModelConfig.Transducer.Decoder = decoder;
        config.ModelConfig.Transducer.Joiner = joiner;
        config.ModelConfig.Tokens = tokens;
        config.ModelConfig.NumThreads = threads;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";

        // The pipeline owns endpointing, so the recognizer must not end the stream on its own.
        config.EnableEndpoint = 0;

        // Contextual biasing: the transducer can be nudged towards a short phrase list without
        // retraining. sherpa requires beam search for it, and a BPE vocabulary (tokens.txt).
        if (hotwordsFile is not null)
        {
            config.DecodingMethod = "modified_beam_search";
            config.MaxActivePaths = 4;
            config.HotwordsFile = hotwordsFile;
            config.HotwordsScore = hotwordsScore;

            if (bpeVocab is not null)
            {
                config.ModelConfig.ModelingUnit = "bpe";
                config.ModelConfig.BpeVocab = bpeVocab;
            }
        }

        else if (beamSearch)
        {
            // Several hypotheses can rescue a word whose first-best decoding is wrong. It costs
            // more CPU per frame but no extra latency after the endpoint.
            config.DecodingMethod = "modified_beam_search";
            config.MaxActivePaths = 4;
        }

        _recognizer = new OnlineRecognizer(config);
    }

    public ISttStream Begin() => new Stream(_recognizer);

    /// <summary>Convenience path used by the existing pipeline until it feeds audio incrementally.</summary>
    public async Task<SttResult> TranscribeAsync(short[] pcm16k, CancellationToken ct)
    {
        using var stream = Begin();
        stream.Accept(pcm16k);
        return await stream.FinalizeAsync(ct);
    }

    public void Dispose() => _recognizer.Dispose();

    private sealed class Stream(OnlineRecognizer recognizer) : ISttStream
    {
        private readonly SherpaOnnx.OnlineStream _stream = recognizer.CreateStream();
        private string _text = "";
        private int _seq;
        private int _accepted;
        private double _decodeMs;

        public void Accept(ReadOnlySpan<short> pcm16k)
        {
            for (var offset = 0; offset < pcm16k.Length; offset += FrameSamples)
            {
                var count = Math.Min(FrameSamples, pcm16k.Length - offset);
                var samples = new float[count];
                for (var i = 0; i < count; i++)
                    samples[i] = pcm16k[offset + i] / 32768f;

                _stream.AcceptWaveform(SampleRate, samples);
                _accepted += count;
                Drain();
            }
        }

        public SttSnapshot Current =>
            new(TranscriptCleaner.SentenceCase(_text), _seq, Endpoint: false);

        public Task<SttResult> FinalizeAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            if (_accepted == 0)
                return Task.FromResult(new SttResult("", 0));

            // A transducer cannot emit the final word until it has seen context after it. Feed a
            // short silent tail so the last chunk is decoded instead of truncated.
            // The tail goes in 20 ms frames with a drain after each, exactly like live audio:
            // one large blob leaves the decoder holding the final chunk and truncates the word.
            var tail = new float[FrameSamples];
            for (var fed = 0; fed < SampleRate * TailPaddingMs / 1000; fed += FrameSamples)
            {
                _stream.AcceptWaveform(SampleRate, tail);
                Drain();
            }

            _stream.InputFinished();
            Drain();

            var text = TranscriptCleaner.SentenceCase(_text);
            if (text != _text)
            {
                _text = text;
                _seq++;
            }

            return Task.FromResult(new SttResult(text, _decodeMs));
        }

        private void Drain()
        {
            var sw = Stopwatch.StartNew();
            while (recognizer.IsReady(_stream))
                recognizer.Decode(_stream);
            _decodeMs += sw.Elapsed.TotalMilliseconds;

            var next = recognizer.GetResult(_stream).Text.Trim();
            if (next != _text)
            {
                _text = next;
                _seq++;
            }
        }

        public void Dispose() => _stream.Dispose();
    }
}
