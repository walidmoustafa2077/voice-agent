using System.Text;
using VoiceAgent.Server.Contracts;
using VoiceAgent.Server.Sessions;

namespace VoiceAgent.Server.Pipeline;

/// <summary>
/// One connection's cascade. Audio keeps flowing while a turn runs: the turn is detached from
/// the capture pump so the segmenter can hear barge-in during STT/LLM/TTS.
/// </summary>
public sealed class VoicePipeline : IDisposable
{
    private readonly SpeechSegmenter _segmenter;
    private readonly SttService _stt;
    private readonly LlmService _llm;
    private readonly ITtsService _tts;
    private readonly IVoiceEventSink _sink;
    private readonly TurnCoordinator _turns;
    private readonly ConversationHistory _history = new();
    private readonly List<string> _sentences = [];
    private readonly List<double> _durations = [];
    private readonly object _gate = new();

    private Task? _turnTask;
    private short[]? _pendingPcm;
    private short[] _lastUtterancePcm = [];

    public VoicePipeline(
        SpeechSegmenter segmenter,
        SttService stt,
        LlmService llm,
        ITtsService tts,
        IVoiceEventSink sink,
        TurnCoordinator turns)
    {
        _segmenter = segmenter;
        _stt = stt;
        _llm = llm;
        _tts = tts;
        _sink = sink;
        _turns = turns;

        _turns.StateChanged += state => _ = _sink.SendAsync(new StateChanged(state));
    }

    public TurnCoordinator Turns => _turns;

    public short[] LastUtterancePcm
    {
        get { lock (_gate) return _lastUtterancePcm; }
    }

    public IReadOnlyList<string> Sentences
    {
        get { lock (_gate) return [.. _sentences]; }
    }

    /// <summary>Exposed so tests can await the detached turn.</summary>
    public Task? CurrentTurn
    {
        get { lock (_gate) return _turnTask; }
    }

    public Task PushAudioAsync(short[] pcm16k, CancellationToken ct)
    {
        var frame = _segmenter.Push(pcm16k);

        foreach (var evt in frame.Events)
        {
            if (evt.IsStart || evt.Pcm is null || evt.Pcm.Length == 0)
                continue;

            lock (_gate)
            {
                if (_turnTask is { IsCompleted: false })
                {
                    // Task 7 merges this into the running turn instead of replacing it.
                    _pendingPcm = evt.Pcm;
                    continue;
                }

                _turnTask = RunTurnsAsync(evt.Pcm, ct);
            }
        }

        return Task.CompletedTask;
    }

    private async Task RunTurnsAsync(short[] first, CancellationToken ct)
    {
        var pcm = first;

        while (pcm is not null)
        {
            try
            {
                await HandleUtteranceAsync(pcm, ct);
            }
            catch (OperationCanceledException)
            {
                // Cancelled by barge-in or disconnect: the cancel path already told the client.
                return;
            }
            catch (Exception ex)
            {
                await TrySendAsync(new PipelineError($"turn failed: {ex.Message}"));
                _turns.Cancel("error");
            }

            lock (_gate)
            {
                pcm = _pendingPcm;
                _pendingPcm = null;
            }
        }
    }

    private async Task HandleUtteranceAsync(short[] pcm, CancellationToken ct)
    {
        _turns.BeginThinking();
        var turnId = _turns.TurnId;

        await TrySendAsync(new TurnStarted(turnId, DateTimeOffset.UtcNow));

        SttResult result;
        try
        {
            result = await _stt.TranscribeAsync(pcm, _turns.TurnToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (result.Text.Length == 0)
        {
            _turns.BeginListening();
            return;
        }

        lock (_gate)
        {
            _lastUtterancePcm = pcm;
            _sentences.Clear();
            _durations.Clear();
        }

        Console.WriteLine($"stt {result.LatencyMs:F0} ms: {result.Text}");
        await TrySendAsync(new Transcript(result.Text));

        _history.AddUser(result.Text);
        var chunker = new SentenceChunker();
        var full = new StringBuilder();
        var jobs = System.Threading.Channels.Channel.CreateUnbounded<(int Index, string Text)>(
            new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });

        var pump = SynthesizeLoopAsync(jobs.Reader, _turns.TurnToken);

        try
        {
            await foreach (var delta in _llm.StreamAsync(_history.Messages, _turns.TurnToken))
            {
                full.Append(delta);
                await TrySendAsync(new AssistantDelta(delta));

                foreach (var sentence in chunker.Push(delta))
                    Enqueue(jobs.Writer, sentence);
            }
        }
        catch (OperationCanceledException)
        {
            jobs.Writer.TryComplete();
            await pump;
            return;
        }

        var tail = chunker.Flush();
        if (tail.Length > 0)
            Enqueue(jobs.Writer, tail);

        jobs.Writer.TryComplete();
        await pump;

        double total;
        int count;

        lock (_gate)
        {
            count = _sentences.Count;
            total = _durations.Sum();
        }

        if (count == 0)
        {
            _history.AddAssistant(full.ToString().Trim());
            _turns.BeginListening();
            return;
        }

        _history.AddAssistant(full.ToString().Trim());
        _turns.MarkAllSentencesDispatched(count, total);
    }

    private void Enqueue(
        System.Threading.Channels.ChannelWriter<(int Index, string Text)> writer,
        string sentence)
    {
        int index;

        lock (_gate)
        {
            index = _sentences.Count;
            _sentences.Add(sentence);
            _durations.Add(0);
        }

        writer.TryWrite((index, sentence));
    }

    private async Task SynthesizeLoopAsync(
        System.Threading.Channels.ChannelReader<(int Index, string Text)> reader,
        CancellationToken ct)
    {
        await foreach (var (index, text) in reader.ReadAllAsync(ct))
        {
            try
            {
                var chunk = await _tts.SynthesizeAsync(text, ct);

                lock (_gate)
                {
                    if (index < _durations.Count)
                        _durations[index] = chunk.DurationMs;
                }

                await TrySendAsync(new AudioChunk(index, chunk.Wav, chunk.DurationMs));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                await TrySendAsync(new PipelineError($"tts: {ex.Message}"));
            }
        }
    }

    public Task SentenceStartedAsync(int turnId, int index)
    {
        if (turnId != _turns.TurnId)
            return Task.CompletedTask;

        double duration;
        lock (_gate)
        {
            duration = index >= 0 && index < _durations.Count ? _durations[index] : 0;
        }

        _turns.BeginSpeaking(index, duration);
        return Task.CompletedTask;
    }

    public Task SentencePlayedAsync(int turnId, int index)
    {
        _turns.SentencePlayed(turnId, index);
        return Task.CompletedTask;
    }

    public async Task StopSpeakingAsync(string reason)
    {
        _turns.Cancel(reason);
        await TrySendAsync(new TurnCancelled(reason));
    }

    private async Task TrySendAsync(VoiceEvent evt)
    {
        try
        {
            await _sink.SendAsync(evt, CancellationToken.None);
        }
        catch
        {
            // A dead connection must not take the pipeline with it.
        }
    }

    public void Dispose()
    {
        _turns.Cancel("dispose");
    }
}
