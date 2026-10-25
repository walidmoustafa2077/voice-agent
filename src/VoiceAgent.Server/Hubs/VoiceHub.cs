using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using VoiceAgent.Server.Audio;
using VoiceAgent.Server.Hubs;
using VoiceAgent.Server.Pipeline;
using VoiceAgent.Server.Sessions;

namespace VoiceAgent.Server.Hubs;

public sealed class VoiceHub(Lazy<SttService> stt, IHubContext<VoiceHub> hub) : Hub
{
    private const string SessionKey = "session";

    public async Task<string> StartSession(string? systemPrompt, int micSampleRate)
    {
        if (micSampleRate is not (16000 or 44100 or 48000))
            throw new HubException("micSampleRate must be 16000, 44100 or 48000");

        if (Context.Items.TryGetValue(SessionKey, out var existing) && existing is Session previous)
        {
            previous.Dispose();
            Context.Items.Remove(SessionKey);
        }

        if (!File.Exists(ModelPaths.SileroVad))
            throw new HubException($"missing VAD model at {ModelPaths.SileroVad}");

        var cts = new CancellationTokenSource();
        var vad = new SileroVadModel(ModelPaths.SileroVad);
        var segmenter = new SpeechSegmenter(vad, new VadOptions());
        var turns = new TurnCoordinator(TimeProvider.System, TimeSpan.FromMilliseconds(500));
        var sink = new SignalRVoiceEventSink(hub, Context.ConnectionId);
        var pipeline = new VoicePipeline(segmenter, stt.Value, sink, turns);

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

        await Clients.Caller.SendAsync("stateChanged", "listening");
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

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.Remove(SessionKey, out var value) && value is Session session)
            session.Dispose();

        return base.OnDisconnectedAsync(exception);
    }

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

    private sealed class Session : IDisposable
    {
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

        public void Dispose()
        {
            Cancellation.Cancel();
            Audio.Writer.TryComplete();
            Turns.Dispose();
            Vad.Dispose();
            Cancellation.Dispose();
        }
    }
}
