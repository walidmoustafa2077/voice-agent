using System.Diagnostics;
using System.Text;
using Whisper.net;

namespace VoiceAgent.Server.Pipeline;

public sealed record SttResult(string Text, double LatencyMs);

public interface ISttService
{
    Task<SttResult> TranscribeAsync(short[] pcm16k, CancellationToken ct);
}

public sealed class SttService : ISttService, IDisposable
{
    private readonly WhisperFactory _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly int _threads;

    public SttService(string modelPath, int threads = 4)
    {
        _factory = WhisperFactory.FromPath(modelPath);
        _threads = threads;
    }

    public async Task<SttResult> TranscribeAsync(short[] pcm16k, CancellationToken ct)
    {
        if (TranscriptCleaner.IsSilent(pcm16k)) return new SttResult("", 0);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stopwatch = Stopwatch.StartNew();

            using var processor = _factory.CreateBuilder()
                .WithLanguage("en")
                .WithThreads(_threads)
                .WithNoSpeechThreshold(0.6f)
                .Build();

            using var wav = Audio.Pcm16Wav.Wrap(pcm16k);
            var text = new StringBuilder();

            await foreach (var segment in processor.ProcessAsync(wav, ct).ConfigureAwait(false))
            {
                text.Append(segment.Text);
            }

            return new SttResult(TranscriptCleaner.Clean(text.ToString()), stopwatch.Elapsed.TotalMilliseconds);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _factory.Dispose();
        _gate.Dispose();
    }
}
