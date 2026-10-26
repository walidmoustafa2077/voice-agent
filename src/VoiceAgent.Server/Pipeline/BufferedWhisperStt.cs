namespace VoiceAgent.Server.Pipeline;

/// <summary>
/// The fallback recognizer: buffers the utterance and runs whole-utterance Whisper at finalize.
/// It emits no partials, so the pipeline behaves exactly as it did before streaming existed. It is
/// also the parity check for the streaming engine — same audio in, same transcript out.
/// </summary>
public sealed class BufferedWhisperStt(SttService whisper) : IStreamingStt, ISttService
{
    public ISttStream Begin() => new Buffer(whisper);

    public async Task<SttResult> TranscribeAsync(short[] pcm16k, CancellationToken ct)
    {
        using var stream = Begin();
        stream.Accept(pcm16k);
        return await stream.FinalizeAsync(ct);
    }

    /// <summary>The buffer owns no native resources; the shared Whisper factory outlives it.</summary>
    public void Dispose()
    {
    }

    private sealed class Buffer(SttService whisper) : ISttStream
    {
        private readonly List<short> _pcm = [];
        private string _text = "";
        private int _seq;

        public void Accept(ReadOnlySpan<short> pcm16k)
        {
            _pcm.AddRange(pcm16k);

            // The segmenter never emits more than this, but a caller may stitch utterances
            // together; keep the newest audio and let the oldest fall away.
            var overflow = _pcm.Count - SpeechSegmenter.MaxUtteranceSamples;
            if (overflow > 0)
                _pcm.RemoveRange(0, overflow);
        }

        public SttSnapshot Current => new(_text, _seq, Endpoint: false);

        public async Task<SttResult> FinalizeAsync(CancellationToken ct)
        {
            var result = _pcm.Count == 0
                ? new SttResult("", 0)
                : await whisper.TranscribeAsync([.. _pcm], ct);

            if (result.Text != _text)
            {
                _text = result.Text;
                _seq++;
            }

            return result;
        }

        public void Dispose() => _pcm.Clear();
    }
}
