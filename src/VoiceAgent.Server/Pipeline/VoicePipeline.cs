using System.Text;
using System.Threading.Channels;
using VoiceAgent.Server.Contracts;
using VoiceAgent.Server.Sessions;

namespace VoiceAgent.Server.Pipeline;

public sealed class VoicePipeline(
    SpeechSegmenter segmenter,
    SttService stt,
    LlmService llm,
    ITtsService tts,
    IVoiceEventSink sink,
    TurnCoordinator turns)
{
    private readonly ConversationHistory _history = new();
    private readonly List<string> _sentences = [];
    private short[] _lastUtterancePcm = [];
    private Channel<SentenceJob>? _jobs;

    private readonly record struct SentenceJob(int Index, string Text);

    public TurnCoordinator Turns => turns;

    public short[] LastUtterancePcm => _lastUtterancePcm;

    public IReadOnlyList<string> Sentences => _sentences;

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

        _history.AddUser(result.Text);
        _sentences.Clear();

        // Synthesis runs behind the LLM stream: chunks are spoken as they are produced.
        _jobs = Channel.CreateUnbounded<SentenceJob>(new UnboundedChannelOptions { SingleReader = true });
        var pump = SynthesizeLoopAsync(_jobs.Reader, turns.TurnToken);

        var chunker = new SentenceChunker();
        var full = new StringBuilder();

        try
        {
            await foreach (var delta in llm.StreamAsync(_history.Messages, turns.TurnToken))
            {
                full.Append(delta);
                await sink.SendAsync(new AssistantDelta(delta), ct);

                foreach (var sentence in chunker.Push(delta))
                    Enqueue(sentence);
            }
        }
        catch (OperationCanceledException)
        {
            _jobs.Writer.TryComplete();
            return;
        }

        var tail = chunker.Flush();
        if (tail.Length > 0)
            Enqueue(tail);

        _jobs.Writer.TryComplete();

        try
        {
            await pump;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _history.AddAssistant(full.ToString().Trim());

        if (_sentences.Count == 0)
        {
            await ToListeningAsync(ct);
            return;
        }

        turns.MarkAllSentencesDispatched(_sentences.Count);
        Console.WriteLine($"tts {_sentences.Count} sentence(s) dispatched");
    }

    private void Enqueue(string sentence)
    {
        var index = _sentences.Count;
        _sentences.Add(sentence);
        _jobs!.Writer.TryWrite(new SentenceJob(index, sentence));
    }

    private async Task SynthesizeLoopAsync(ChannelReader<SentenceJob> reader, CancellationToken ct)
    {
        await foreach (var job in reader.ReadAllAsync(ct))
        {
            TtsChunk chunk;
            try
            {
                chunk = await tts.SynthesizeAsync(job.Text, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                await sink.SendAsync(new PipelineError($"tts: {ex.Message}"), ct);
                continue;
            }

            await sink.SendAsync(new AudioChunk(job.Index, chunk.Wav, chunk.DurationMs), ct);
        }
    }

    public async Task SentenceStartedAsync(int index, double durationMs)
    {
        turns.BeginSpeaking(index, durationMs);
        await sink.SendAsync(new StateChanged(VoiceState.Speaking));
    }

    public Task SentencePlayedAsync(int index)
    {
        turns.SentencePlayed(index);
        return Task.CompletedTask;
    }

    public async Task StopSpeakingAsync(string reason)
    {
        turns.Cancel(reason);
        _jobs?.Writer.TryComplete();

        await sink.SendAsync(new TurnCancelled(reason));
        await sink.SendAsync(new StateChanged(VoiceState.Listening));
    }

    private async Task ToListeningAsync(CancellationToken ct)
    {
        if (turns.State == VoiceState.Thinking)
            turns.BeginListening();

        await sink.SendAsync(new StateChanged(VoiceState.Listening), ct);
    }
}
