using Microsoft.AspNetCore.SignalR;
using VoiceAgent.Server.Audio;

namespace VoiceAgent.Server.Hubs;

public sealed class VoiceHub : Hub
{
    private sealed class Session
    {
        public required int MicSampleRate { get; init; }
        public required StreamingResampler Resampler { get; init; }
        public required FrameRateCounter Counter { get; init; }
        public string? SystemPrompt { get; init; }
    }

    private const string SessionKey = "session";

    public Task<string> StartSession(string? systemPrompt, int micSampleRate)
    {
        if (micSampleRate is not (16000 or 44100 or 48000))
        {
            throw new HubException($"Unsupported mic rate {micSampleRate}. Use 16000, 44100 or 48000.");
        }

        Context.Items[SessionKey] = new Session
        {
            MicSampleRate = micSampleRate,
            Resampler = new StreamingResampler(micSampleRate),
            Counter = new FrameRateCounter(),
            SystemPrompt = systemPrompt,
        };

        return Task.FromResult(Context.ConnectionId);
    }

    public async Task SendAudio(byte[] pcm)
    {
        if (Context.Items[SessionKey] is not Session session)
        {
            await Clients.Caller.SendAsync("error", "Call StartSession before SendAudio.");
            return;
        }

        var expectedSamples = session.MicSampleRate / 50;
        if (pcm.Length != expectedSamples * 2)
        {
            await Clients.Caller.SendAsync(
                "error",
                $"Expected {expectedSamples * 2} bytes for 20 ms at {session.MicSampleRate} Hz, got {pcm.Length}.");
            return;
        }

        var samples = new short[pcm.Length / 2];
        Buffer.BlockCopy(pcm, 0, samples, 0, pcm.Length);

        var resampled = session.Resampler.Process(samples);
        session.Counter.Add(1);

        await Clients.Caller.SendAsync(
            "level",
            Rms(resampled),
            session.Counter.FramesPerSecond);
    }

    private static double Rms(ReadOnlySpan<short> data)
    {
        if (data.Length == 0) return 0;
        double sum = 0;
        foreach (var s in data) sum += (double)s * s;
        return Math.Sqrt(sum / data.Length) / short.MaxValue;
    }
}
