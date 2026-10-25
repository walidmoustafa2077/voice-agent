using VoiceAgent.Server.Contracts;
using VoiceAgent.Server.Sessions;

namespace VoiceAgent.Server.Pipeline;

public sealed class VoicePipeline(
    SpeechSegmenter segmenter,
    SttService stt,
    IVoiceEventSink sink,
    TurnCoordinator turns)
{
    private short[] _lastUtterancePcm = [];

    public TurnCoordinator Turns => turns;

    public short[] LastUtterancePcm => _lastUtterancePcm;

    public async Task PushAudioAsync(short[] pcm16k, CancellationToken ct)
    {
        var frame = segmenter.Push(pcm16k);

        foreach (var evt in frame.Events)
        {
            if (evt.IsStart || evt.Pcm is null || evt.Pcm.Length == 0)
                continue;

            await HandleUtteranceAsync(evt.Pcm, ct);
        }
    }

    private async Task HandleUtteranceAsync(short[] pcm, CancellationToken ct)
    {
        turns.BeginThinking();
        await sink.SendAsync(new TurnStarted(DateTimeOffset.UtcNow), ct);
        await sink.SendAsync(new StateChanged(VoiceState.Thinking), ct);

        SttResult result;
        try
        {
            result = await stt.TranscribeAsync(pcm, turns.TurnToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (result.Text.Length == 0)
        {
            await ToListeningAsync(ct);
            return;
        }

        _lastUtterancePcm = pcm;
        Console.WriteLine($"stt {result.LatencyMs:F0} ms: {result.Text}");

        await sink.SendAsync(new Transcript(result.Text), ct);
        await ToListeningAsync(ct);
    }

    private async Task ToListeningAsync(CancellationToken ct)
    {
        if (turns.State == VoiceState.Thinking)
            turns.BeginListening();

        await sink.SendAsync(new StateChanged(VoiceState.Listening), ct);
    }
}
