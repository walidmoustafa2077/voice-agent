using System.Text;
using VoiceAgent.Server.Contracts;
using VoiceAgent.Server.Options;
using VoiceAgent.Server.Sessions;

namespace VoiceAgent.Server.Pipeline;

/// <summary>
/// One connection's cascade. Audio keeps flowing while a turn runs: the turn is detached from
/// the capture pump so the segmenter can hear barge-in during STT/LLM/TTS.
/// </summary>
public sealed class VoicePipeline : IDisposable
{
    private readonly SpeechSegmenter _segmenter;
    private readonly ISttService _stt;
    private readonly LlmService _llm;
    private readonly ITtsService _tts;
    private readonly IVoiceEventSink _sink;
    private readonly TurnCoordinator _turns;
    private readonly ConversationHistory _history;
    private readonly VoiceConfig _config;
    private readonly System.Diagnostics.Stopwatch _turnClock = new();
    private readonly List<string> _sentences = [];
    private readonly List<double> _durations = [];
    private readonly object _gate = new();

    private Task? _turnTask;
    private short[]? _pendingPcm;
    private short[] _lastUtterancePcm = [];
    private short[]? _mergeBase;
    private bool _userAddedThisTurn;
    private readonly BargeInGate _bargeIn = new(250);
    private readonly EchoFloorGate _echoFloor = new();

    public VoicePipeline(
        SpeechSegmenter segmenter,
        ISttService stt,
        LlmService llm,
        ITtsService tts,
        IVoiceEventSink sink,
        TurnCoordinator turns,
        VoiceConfig? config = null)
    {
        _segmenter = segmenter;
        _stt = stt;
        _llm = llm;
        _tts = tts;
        _sink = sink;
        _turns = turns;
        _config = config ?? new VoiceConfig();
        _history = new ConversationHistory(_config.MaxHistoryMessages);

        _turns.StateChanged += state =>
        {
            _segmenter.Threshold = state == VoiceState.Speaking
                ? _config.SpeakingVadThreshold
                : _config.VadThreshold;
            if (state != VoiceState.Speaking) _echoFloor.EndPlayback();
            _ = _sink.SendAsync(new StateChanged(state));
        };
    }

    public TurnCoordinator Turns => _turns;

    public float CurrentVadThreshold => _segmenter.Threshold;

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
        var state = _turns.State;

        if (state is VoiceState.Thinking or VoiceState.Speaking)
        {
            var userSpeech = _echoFloor.IsUserSpeech(pcm16k, frame.Probability);
            if (_bargeIn.Push(userSpeech, pcm16k.Length / 16))
            {
                _bargeIn.Reset();
                return HandleBargeInAsync(state);
            }
        }
        else
        {
            _bargeIn.Reset();
        }

        foreach (var evt in frame.Events)
        {
            if (evt.IsStart || evt.Pcm is null || evt.Pcm.Length == 0)
                continue;

            var pcm = evt.Pcm;

            lock (_gate)
            {
                if (_mergeBase is not null)
                {
                    pcm = [.. _mergeBase, .. pcm];
                    _mergeBase = null;
                }

                if (_turnTask is { IsCompleted: false })
                {
                    _pendingPcm = pcm;
                    continue;
                }

                _turnTask = RunTurnsAsync(pcm, ct);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the reply without discarding what the user has already said. During Thinking the
    /// aborted utterance becomes the base of the next one, so "summarise this ... no, shorter"
    /// is a single turn rather than two racing answers.
    /// </summary>
    private Task HandleBargeInAsync(VoiceState state)
    {
        var merge = state == VoiceState.Thinking;

        if (merge)
        {
            lock (_gate)
            {
                _mergeBase = _lastUtterancePcm;
            }

            if (_userAddedThisTurn)
                _history.RemoveLastUser();
        }

        _turns.Cancel(merge ? "merge" : "barge");
        return TrySendAsync(new TurnCancelled(merge ? "merge" : "barge"));
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
                // A barge-in cancels this turn, not the session; the merged utterance (if any)
                // arrives as a later segment and starts its own turn.
                if (ct.IsCancellationRequested) return;
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

                // Released under the lock so the next utterance cannot be appended to a loop
                // that has already decided to exit.
                if (pcm is null) _turnTask = null;
            }
        }
    }

    private async Task HandleUtteranceAsync(short[] pcm, CancellationToken ct)
    {
        _turns.BeginThinking();
        _turnClock.Restart();
        var turnId = _turns.TurnId;
        _userAddedThisTurn = false;

        lock (_gate)
        {
            _lastUtterancePcm = pcm;
            _sentences.Clear();
            _durations.Clear();
        }

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

        Console.WriteLine($"stt {result.LatencyMs:F0} ms: {result.Text}");
        await TrySendAsync(new Transcript(result.Text));
        await TrySendAsync(new StageTiming("stt", result.LatencyMs));

        _history.AddUser(result.Text);
        _userAddedThisTurn = true;
        var chunker = new SentenceChunker(_config.MaxChunkChars, _config.MinChunkChars);
        var full = new StringBuilder();
        var jobs = System.Threading.Channels.Channel.CreateUnbounded<(int Index, string Text)>(
            new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });

        var pump = SynthesizeLoopAsync(
            jobs.Reader, _turns.TurnToken, () => _turnClock.Elapsed.TotalMilliseconds);
        var firstDelta = true;

        try
        {
            await foreach (var delta in _llm.StreamAsync(_history.Messages, _turns.TurnToken))
            {
                full.Append(delta);

                if (firstDelta)
                {
                    firstDelta = false;
                    await TrySendAsync(new StageTiming("firstToken", _turnClock.Elapsed.TotalMilliseconds));
                }

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
        CancellationToken ct,
        Func<double>? onFirstSynth = null)
    {
        var first = true;

        await foreach (var (index, text) in reader.ReadAllAsync(ct))
        {
            try
            {
                var chunk = await _tts.SynthesizeAsync(text, ct);

                if (first)
                {
                    first = false;
                    var ms = onFirstSynth?.Invoke() ?? 0;
                    await TrySendAsync(new StageTiming("firstSynth", ms));
                }

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
        _echoFloor.BeginPlayback();
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
