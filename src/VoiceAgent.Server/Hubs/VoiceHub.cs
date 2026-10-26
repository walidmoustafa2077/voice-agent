using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using VoiceAgent.Server.Audio;
using VoiceAgent.Server.Hubs;
using VoiceAgent.Server.Pipeline;
using VoiceAgent.Server.Sessions;

namespace VoiceAgent.Server.Hubs;

public sealed class VoiceHub(
    Lazy<ISttService> stt,
    LlmService llm,
    Lazy<ITtsService> tts,
    IHubContext<VoiceHub> hub,
    VoiceAgent.Server.Options.VoiceConfigStore config) : Hub
{
    private const string SessionKey = "session";

    public async Task<string> StartSession(string? systemPrompt, int micSampleRate)
    {
        if (micSampleRate is not (16000 or 44100 or 48000))
            throw new HubException("micSampleRate must be 16000, 44100 or 48000");

        if (Context.Items.TryGetValue(SessionKey, out var existing) && existing is Session previous)
        {
            Context.Items.Remove(SessionKey);
            await previous.DisposeAsync();
        }

        foreach (var model in ModelDownloader.Required)
        {
            if (!model.IsReady)
            {
                throw new HubException(
                    $"{model.Name} is missing or incomplete at {model.Path}. " +
                    "Restart the server to download it, or delete the partial file and restart.");
            }
        }

        var cts = new CancellationTokenSource();
        var cfg = config.Current;
        var vad = new SileroVadModel(ModelPaths.SileroVad);
        var segmenter = new SpeechSegmenter(vad, new VadOptions
        {
            Threshold = cfg.VadThreshold,
            MinSpeechMs = cfg.MinSpeechMs,
            MinSilenceMs = cfg.MinSilenceMs,
            PreRollMs = cfg.PreRollMs,
        });
        var turns = new TurnCoordinator(TimeProvider.System, TimeSpan.FromMilliseconds(500));
        var sink = new SignalRVoiceEventSink(hub, Context.ConnectionId);
        var pipeline = new VoicePipeline(segmenter, stt.Value, llm, tts.Value, sink, turns, cfg);

        var channel = Channel.CreateBounded<short[]>(new BoundedChannelOptions(50)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        var session = new Session
        {
            MicSampleRate = micSampleRate,
            SystemPrompt = systemPrompt,
            Resampler = new StreamingResampler(micSampleRate),
            Counter = new FrameRateCounter(),
            Segmenter = segmenter,
            Vad = vad,
            Turns = turns,
            Pipeline = pipeline,
            Audio = channel,
            Cancellation = cts,
        };

        session.Pump = Task.Run(async () =>
        {
            try
            {
                await foreach (var chunk in channel.Reader.ReadAllAsync(cts.Token))
                    await pipeline.PushAudioAsync(chunk, cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                await sink.SendAsync(new Contracts.PipelineError(ex.Message), CancellationToken.None);
            }
        });

        Context.Items[SessionKey] = session;

        await Clients.Caller.SendAsync("stateChanged", "listening", cfg.VadThreshold);
        return Guid.NewGuid().ToString("N");
    }

    public async Task SendAudio(byte[] pcm)
    {
        if (!Context.Items.TryGetValue(SessionKey, out var value) || value is not Session session)
        {
            await Clients.Caller.SendAsync("error", "call StartSession first");
            return;
        }

        var expectedSamples = session.MicSampleRate / 50;
        if (pcm.Length != expectedSamples * 2)
        {
            await Clients.Caller.SendAsync("error",
                $"expected {expectedSamples * 2} bytes for {session.MicSampleRate} Hz, got {pcm.Length}");
            return;
        }

        var native = new short[expectedSamples];
        Buffer.BlockCopy(pcm, 0, native, 0, pcm.Length);

        var pcm16k = session.Resampler.Process(native);
        session.Counter.Add(1);

        if (pcm16k.Length > 0)
            session.Audio.Writer.TryWrite(pcm16k);

        await Clients.Caller.SendAsync("level", Rms(pcm16k), session.Counter.FramesPerSecond);
    }

    public Task SentenceStarted(int turnId, int index, double atMs) =>
        GetSession()?.Pipeline.SentenceStartedAsync(turnId, index) ?? Task.CompletedTask;

    public Task SentencePlayed(int turnId, int index) =>
        GetSession()?.Pipeline.SentencePlayedAsync(turnId, index) ?? Task.CompletedTask;

    public Task StopSpeaking() =>
        GetSession()?.Pipeline.StopSpeakingAsync("user") ?? Task.CompletedTask;

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.Remove(SessionKey, out var value) && value is Session session)
            await session.DisposeAsync();

        await base.OnDisconnectedAsync(exception);
    }

    private Session? GetSession() =>
        Context.Items.TryGetValue(SessionKey, out var value) && value is Session session ? session : null;

    private static double Rms(ReadOnlySpan<short> samples)
    {
        if (samples.Length == 0)
            return 0;

        double sum = 0;
        foreach (var sample in samples)
        {
            var value = sample / 32768.0;
            sum += value * value;
        }

        return Math.Sqrt(sum / samples.Length);
    }

    private sealed class Session : IAsyncDisposable
    {
        private int _closed;

        public required int MicSampleRate { get; init; }
        public required string? SystemPrompt { get; init; }
        public required StreamingResampler Resampler { get; init; }
        public required FrameRateCounter Counter { get; init; }
        public required SpeechSegmenter Segmenter { get; init; }
        public required SileroVadModel Vad { get; init; }
        public required TurnCoordinator Turns { get; init; }
        public required VoicePipeline Pipeline { get; init; }
        public required Channel<short[]> Audio { get; init; }
        public required CancellationTokenSource Cancellation { get; init; }
        public Task? Pump { get; set; }

        /// <summary>
        /// Cancels everything, waits briefly for the pump and the detached turn to leave the
        /// models, then disposes. Disposing the VAD under an active Predict can fault the ONNX
        /// session; the wait is bounded so a stuck native call cannot hold the connection open.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
                return;

            Pipeline.Dispose();          // cancels the active turn before the models go away
            Cancellation.Cancel();
            Audio.Writer.TryComplete();

            await WaitQuietAsync(Pump);
            await WaitQuietAsync(Pipeline.CurrentTurn);

            Turns.Dispose();
            Vad.Dispose();
            Cancellation.Dispose();
        }

        private static async Task WaitQuietAsync(Task? task)
        {
            if (task is null) return;

            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch
            {
                // Cancellation, a timeout, or a turn fault: disposal must still complete.
            }
        }
    }
}
