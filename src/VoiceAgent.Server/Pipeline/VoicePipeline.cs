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
    private static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(5);
    private const int PendingLimit = 2;

    private readonly SpeechSegmenter _segmenter;
    private readonly ISttService _stt;
    private readonly LlmService _llm;
    private readonly ITtsService _tts;
    private readonly IVoiceEventSink _sink;
    private readonly TurnCoordinator _turns;
    private readonly ConversationHistory _history;
    private readonly VoiceConfig _config;
    private readonly TimeProvider _time;
    private readonly System.Diagnostics.Stopwatch _turnClock = new();
    private readonly List<string> _sentences = [];
    private readonly List<double> _durations = [];
    private readonly Queue<short[]> _pending = new();
    private readonly object _gate = new();

    private Task? _turnTask;
    private short[] _lastUtterancePcm = [];
    private short[]? _mergeBase;
    private long _mergeAt;
    private bool _userAddedThisTurn;
    private bool _transcriptSentThisTurn;
    private bool _assistantAddedThisTurn;
    private bool _dropUtterance;
    private bool _disposed;

    // Per-turn measurements. Reset at turn start, read by LogTurn in the turn's finally.
    private double _audioSeconds;
    private double _sttWallMs;
    private double _ttsWallMs;
    private double _synthAudioMs;
    private int _firstChunkChars;
    private int _cancelledThisTurn;
    private readonly BargeInGate _bargeIn = new(250);
    private readonly EchoFloorGate _echoFloor = new();

    public VoicePipeline(
        SpeechSegmenter segmenter,
        ISttService stt,
        LlmService llm,
        ITtsService tts,
        IVoiceEventSink sink,
        TurnCoordinator turns,
        VoiceConfig? config = null,
        TimeProvider? time = null)
    {
        _segmenter = segmenter;
        _stt = stt;
        _llm = llm;
        _tts = tts;
        _sink = sink;
        _turns = turns;
        _config = config ?? new VoiceConfig();
        _time = time ?? TimeProvider.System;
        _history = new ConversationHistory(_config.MaxHistoryMessages);

        _turns.StateChanged += state =>
        {
            _segmenter.Threshold = state == VoiceState.Speaking
                ? _config.SpeakingVadThreshold
                : _config.VadThreshold;
            if (state != VoiceState.Speaking) _echoFloor.EndPlayback();
            _ = SendStateSafeAsync(new StateChanged(state, _segmenter.Threshold));
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

    /// <summary>Snapshot for tests; the client never sees this directly.</summary>
    public IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> History => _history.Messages;

    /// <summary>Exposed so tests can await the detached turn.</summary>
    public Task? CurrentTurn
    {
        get { lock (_gate) return _turnTask; }
    }

    public Task PushAudioAsync(short[] pcm16k, CancellationToken ct)
    {
        if (_disposed) return Task.CompletedTask;

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
            // Start events carry no PCM; they only mark onset.
            if (evt.IsStart)
            {
                lock (_gate)
                {
                    if (_mergeBase is not null && _time.GetElapsedTime(_mergeAt) > MergeWindow)
                        _mergeBase = null;

                    // A segment that starts while the agent is speaking is either its own echo
                    // or a blip too short to barge in. Only a confirmed barge-in may turn it
                    // into a user turn; drop it when it ends instead.
                    _dropUtterance = state == VoiceState.Speaking;
                }

                continue;
            }

            if (evt.Pcm is null || evt.Pcm.Length == 0)
                continue;

            lock (_gate)
            {
                if (_dropUtterance)
                {
                    _dropUtterance = false;
                    continue;
                }

                var pcm = evt.Pcm;

                if (_mergeBase is not null)
                {
                    pcm = [.. _mergeBase, .. pcm];
                    _mergeBase = null;

                    // A merge may not exceed what the segmenter itself would ever emit; keeping
                    // the tail preserves the newest request and bounds the Whisper input.
                    if (pcm.Length > SpeechSegmenter.MaxUtteranceSamples)
                        pcm = pcm[^SpeechSegmenter.MaxUtteranceSamples..];
                }

                if (_turnTask is { IsCompleted: false })
                {
                    if (_pending.Count >= PendingLimit) _pending.Dequeue();
                    _pending.Enqueue(pcm);
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
        var cancelled = CancelTurn(merge ? "merge" : "barge", merge);
        return TrySendAsync(cancelled);
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
                var cancelled = CancelTurn("error", merge: false, keepUtterance: false);
        await TrySendAsync(cancelled);
            }

            lock (_gate)
            {
                pcm = _pending.Count > 0 ? _pending.Dequeue() : null;

                // Released under the lock so the next utterance cannot be appended to a loop
                // that has already decided to exit.
                if (pcm is null) _turnTask = null;
            }
        }
    }

    private async Task HandleUtteranceAsync(short[] pcm, CancellationToken sessionCt)
    {
        if (_disposed) return;

        _turns.BeginThinking();
        _turnClock.Restart();
        var turnId = _turns.TurnId;

        lock (_gate)
        {
            _lastUtterancePcm = pcm;
            _sentences.Clear();
            _durations.Clear();
            _userAddedThisTurn = false;
            _transcriptSentThisTurn = false;
            _assistantAddedThisTurn = false;

            // One machine-readable latency line per turn, measured from TurnStarted.
            _audioSeconds = pcm.Length / 16000.0;
            _sttWallMs = 0;
            _ttsWallMs = 0;
            _synthAudioMs = 0;
            _firstChunkChars = 0;
            _cancelledThisTurn = 0;
        }

        try
        {
            await RunUtteranceAsync(pcm, turnId, sessionCt);
        }
        finally
        {
            LogTurn(turnId);
        }
    }

    private async Task RunUtteranceAsync(short[] pcm, int turnId, CancellationToken sessionCt)
    {
        var ct = _turns.TurnToken;

        await TrySendAsync(new TurnStarted(turnId, DateTimeOffset.UtcNow));

        SttResult result;
        try
        {
            result = await _stt.TranscribeAsync(pcm, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested) return;

        _sttWallMs = result.LatencyMs;

        if (result.Text.Length == 0)
        {
            _turns.BeginListening();
            return;
        }

        // Marked and checked under the lock so a merge either sees the bubble (and removes it)
        // or cancels before anything is sent.
        lock (_gate)
        {
            if (ct.IsCancellationRequested) return;
            _transcriptSentThisTurn = true;
        }

        await TrySendAsync(new Transcript(turnId, result.Text));
        await TrySendAsync(new StageTiming("stt", result.LatencyMs));

        lock (_gate)
        {
            if (ct.IsCancellationRequested) return;

            _history.AddUser(result.Text);
            _userAddedThisTurn = true;
        }

        var chunker = new SentenceChunker(_config.MaxChunkChars, _config.MinChunkChars);
        var full = new StringBuilder();
        var jobs = System.Threading.Channels.Channel.CreateUnbounded<(int Index, string Text)>(
            new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });

        var pump = SynthesizeLoopAsync(
            jobs.Reader, turnId, ct, () => _turnClock.Elapsed.TotalMilliseconds);
        var firstDelta = true;

        try
        {
            await foreach (var delta in _llm.StreamAsync(_history.Messages, ct))
            {
                full.Append(delta);

                if (ct.IsCancellationRequested) break;

                if (firstDelta)
                {
                    firstDelta = false;
                    await TrySendAsync(new StageTiming("firstToken", _turnClock.Elapsed.TotalMilliseconds));
                }

                await TrySendAsync(new AssistantDelta(turnId, delta));

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

        if (ct.IsCancellationRequested) return;

        double total;
        int count;

        lock (_gate)
        {
            count = _sentences.Count;
            total = _durations.Sum();
        }

        var text = full.ToString().Trim();

        lock (_gate)
        {
            if (ct.IsCancellationRequested) return;

            if (text.Length > 0)
            {
                _history.AddAssistant(text);
                _assistantAddedThisTurn = true;
            }
        }

        if (count == 0)
        {
            _turns.BeginListening();
            return;
        }

        _turns.MarkAllSentencesDispatched(turnId, count, total);
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
        int turnId,
        CancellationToken ct,
        Func<double>? onFirstSynth = null)
    {
        var first = true;

        await foreach (var (index, text) in reader.ReadAllAsync(ct))
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var chunk = await _tts.SynthesizeAsync(text, ct);
                sw.Stop();

                lock (_gate)
                {
                    _ttsWallMs += sw.Elapsed.TotalMilliseconds;
                    _synthAudioMs += chunk.DurationMs;
                    if (index == 0) _firstChunkChars = text.Length;

                    if (index < _durations.Count)
                        _durations[index] = chunk.DurationMs;
                }

                if (ct.IsCancellationRequested) return;

                if (first)
                {
                    first = false;
                    var ms = onFirstSynth?.Invoke() ?? 0;
                    await TrySendAsync(new StageTiming("firstSynth", ms));
                }

                await TrySendAsync(new AudioChunk(turnId, index, chunk.Wav, chunk.DurationMs));
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

        // BeginPlayback runs inside the coordinator lock, so calibration cannot start for a
        // report that a concurrent cancel has just invalidated.
        _turns.SentenceStarted(turnId, index, duration, _echoFloor.BeginPlayback);
        return Task.CompletedTask;
    }

    public Task SentencePlayedAsync(int turnId, int index)
    {
        _turns.SentencePlayed(turnId, index);
        return Task.CompletedTask;
    }

    public async Task StopSpeakingAsync(string reason)
    {
        if (_turns.State is VoiceState.Listening or VoiceState.Idle)
            return;

        var cancelled = CancelTurn(reason, merge: false, keepUtterance: false);
        await TrySendAsync(cancelled);
    }

    /// <summary>
    /// Cancels the active turn and captures what was actually heard before the coordinator
    /// resets its played set. Also trims history to the heard boundary.
    /// </summary>
    private TurnCancelled CancelTurn(string reason, bool merge, bool keepUtterance = true)
    {
        var wasSpeaking = _turns.State == VoiceState.Speaking;

        // Recorded before the turn token is cancelled so the unwinding turn's finally sees it.
        lock (_gate) _cancelledThisTurn++;

        var (turnId, lastPlayed) = _turns.Cancel(reason);

        string? heard = null;
        bool removeUser;

        lock (_gate)
        {
            removeUser = merge && _transcriptSentThisTurn;

            if (merge)
            {
                if (_userAddedThisTurn) _history.RemoveLastUser();
                _mergeBase = _lastUtterancePcm.Length > 0 ? _lastUtterancePcm : null;
                _mergeAt = _time.GetTimestamp();
            }
            else
            {
                _mergeBase = null;
            }

            var sentences = _sentences.ToArray();

            if (lastPlayed >= 0 && sentences.Length > 0)
            {
                heard = string.Join(' ', sentences.Take(Math.Min(lastPlayed + 1, sentences.Length)));
            }

            // Only trim if this turn actually added its assistant message; otherwise the last
            // assistant message belongs to an earlier turn.
            if (_assistantAddedThisTurn)
                _history.TruncateLastAssistant(sentences, lastPlayed);

            _assistantAddedThisTurn = false;
            _userAddedThisTurn = false;
            _transcriptSentThisTurn = false;

            // A manual stop while speaking should not turn an in-flight echo segment into a new
            // turn. A confirmed barge-in is the opposite: that open segment is the user's.
            _dropUtterance = !keepUtterance && wasSpeaking;
        }

        return new TurnCancelled(turnId, reason, lastPlayed, heard, removeUser);
    }

    /// <summary>
    /// One line per turn, so the stage costs can be read without guessing. RTF is wall time over
    /// the audio actually processed: utterance seconds for STT, synthesized seconds for TTS.
    /// </summary>
    private void LogTurn(int turnId)
    {
        double audio, stt, tts, synthMs;
        int chunks, firstChars, cancels;

        lock (_gate)
        {
            audio = _audioSeconds;
            stt = _sttWallMs;
            tts = _ttsWallMs;
            synthMs = _synthAudioMs;
            chunks = _sentences.Count;
            firstChars = _firstChunkChars;
            cancels = _cancelledThisTurn;
        }

        var sttRtf = audio > 0.05 ? stt / (audio * 1000) : 0;
        var ttsRtf = synthMs > 0.05 ? tts / synthMs : 0;

        Console.WriteLine(
            $"turn {turnId} audio={audio:F1}s stt={stt:F0}ms ({sttRtf:F2}x) " +
            $"tts={tts:F0}ms ({ttsRtf:F2}x) chunks={chunks} firstChunk={firstChars} " +
            $"cancelled={cancels}");
    }

    private async Task SendStateSafeAsync(VoiceEvent evt)
    {
        try
        {
            await _sink.SendAsync(evt, CancellationToken.None);
        }
        catch
        {
            // A closing connection must not fault the state machine.
        }
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
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pending.Clear();
        }

        _turns.Cancel("dispose");
    }
}
