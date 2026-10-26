using System.Text;
using VoiceAgent.Server.Audio;
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
    private readonly IStreamingStt? _streaming;
    private readonly LlmService _llm;
    private readonly ITtsService _tts;
    private readonly IVoiceEventSink _sink;
    private readonly TurnCoordinator _turns;
    private readonly ConversationHistory _history;
    private readonly AudioDebugWriter _debugAudio;
    private readonly VoiceConfig _config;
    private readonly TimeProvider _time;
    private readonly System.Diagnostics.Stopwatch _turnClock = new();
    private readonly List<string> _sentences = [];
    private readonly List<double> _durations = [];
    private readonly Queue<(string? Text, short[] Pcm, double SttMs)> _pending = new();
    private readonly object _gate = new();

    private Task? _turnTask;
    private short[] _lastUtterancePcm = [];

    // Live streaming recognizer state. `_stream` is non-null only between speech onset and the
    // endpointer firing; `_finalizedThisUtterance` keeps the segmenter's later end event from
    // starting a second turn for audio already transcribed.
    private ISttStream? _stream;
    private ISttStream? _prewarmed;
    private Endpointer? _endpointer;
    private readonly List<short> _streamPcm = [];
    private int _partialSeq;
    private int _partialCooldown;
    private bool _firstPartialSent;
    private readonly System.Diagnostics.Stopwatch _streamClock = new();
    private bool _finalizedForUtterance;

    // Diagnostics for the endpoint decision: speech that begins just after a finalize (the word
    // may have arrived too late), and speech that begins while the turn is already thinking.
    // Both are cumulative and printed on the turn line, so a real-microphone session can show
    // whether the 600 ms silence window is dropping words before it is tuned.
    private static readonly TimeSpan LateSpeechWindow = TimeSpan.FromSeconds(1);
    private long _endpointAt;
    private bool _previousFrameSpeech;
    private int _lateSpeechAfterEndpoint;
    private int _speechDuringThinking;

    private string? _mergeText;
    private string _lastUtteranceText = "";

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

    // LLM wait is the one stage we do not control, so its two halves are logged separately:
    // time to the first byte of the stream (queueing) and time to the first visible token.
    private double _llmFirstUpdateMs;
    private double _llmFirstTokenMs;
    private double _firstTokenAtMs;
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
        TimeProvider? time = null,
        AudioDebugWriter? debugAudio = null)
    {
        _segmenter = segmenter;
        _stt = stt;
        _streaming = stt as IStreamingStt;
        _llm = llm;
        _tts = tts;
        _sink = sink;
        _turns = turns;
        _config = config ?? new VoiceConfig();
        _time = time ?? TimeProvider.System;
        _history = new ConversationHistory(_config.MaxHistoryMessages);
        _debugAudio = debugAudio ?? AudioDebugWriter.FromEnvironment();

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

    public async Task PushAudioAsync(short[] pcm16k, CancellationToken ct)
    {
        if (_disposed) return;

        var frame = _segmenter.Push(pcm16k);
        var state = _turns.State;

        var frameConsumedBySeed = false;

        if (state is VoiceState.Thinking or VoiceState.Speaking)
        {
            var userSpeech = _echoFloor.IsUserSpeech(pcm16k, frame.Probability);
            if (_bargeIn.Push(userSpeech, pcm16k.Length / 16))
            {
                _bargeIn.Reset();

                // Do not return: the frame that fires a barge-in may also carry this utterance's
                // start event, and dropping that event would leave the stream unowned.
                await HandleBargeInAsync(state);

                // The segmenter may have opened the interruption while the agent was still
                // speaking; its IsStart event was ignored in that state and will not repeat. Open
                // the stream here from the segmenter's buffer (pre-roll plus the first word) so the
                // utterance is transcribed from its beginning instead of at segment end.
                if (_streaming is not null && _stream is null
                    && _segmenter.CurrentUtterance().Length > 0)
                {
                    StartStream();
                    frameConsumedBySeed = true;
                }
            }
        }
        else
        {
            _bargeIn.Reset();
        }

        // Live transcription: feed the recognizer while the user is speaking, and only then. The
        // recognizer is half-duplex by design, so its own echo can never reach it while the agent
        // talks (that window is VAD-only, and a barge-in restarts capture). Idle counts as
        // feedable too: the harness never calls StartSession, and a fresh connection is idle.
        if (_stream is not null && !_finalizedForUtterance
            && state is not VoiceState.Speaking)
        {
            var stream = _stream;

            if (!frameConsumedBySeed)
            {
                // The recognizer needs the trailing silence to endpoint, but the silence is not
                // part of the utterance's audio duration.
                stream.Accept(pcm16k);
                if (frame.IsSpeech)
                {
                    _streamPcm.AddRange(pcm16k);
                    TrimStreamPcm();
                }
            }

            var snapshot = stream.Current;

            if (_partialCooldown > 0) _partialCooldown--;
            if (snapshot.Text.Length > 0 && snapshot.ChangeSeq != _partialSeq && _partialCooldown == 0)
            {
                _partialSeq = snapshot.ChangeSeq;
                _partialCooldown = 5; // at most ~10 updates per second

                if (!_firstPartialSent)
                {
                    _firstPartialSent = true;
                    await TrySendAsync(
                        new StageTiming("firstPartial", _streamClock.Elapsed.TotalMilliseconds));
                }

                await TrySendAsync(new TranscriptPartial(snapshot.Text));
            }

            var endpoint = _endpointer!.Push(frame.IsSpeech, snapshot.Text, pcm16k.Length / 16);
            if (endpoint)
            {
                // The endpoint closes the utterance. Reset the segmenter so any audio after it
                // (a soft word the VAD missed, the start of the next question) forms a fresh
                // segment instead of being discarded with this one's tail.
                _segmenter.Reset();
                await FinalizeStreamAsync(ct);
            }
        }

        var forceFinalize = false;

        foreach (var evt in frame.Events)
        {
            // Start events carry no PCM; they only mark onset and open this utterance's stream.
            if (evt.IsStart)
            {
                var drop = state == VoiceState.Speaking;

                lock (_gate)
                {
                    if (_mergeBase is not null && _time.GetElapsedTime(_mergeAt) > MergeWindow)
                        _mergeBase = null;
                    if (_mergeText is not null && _time.GetElapsedTime(_mergeAt) > MergeWindow)
                        _mergeText = null;

                    // A segment that starts while the agent is speaking is either its own echo
                    // or a blip too short to barge in. Only a confirmed barge-in may turn it
                    // into a user turn; drop it when it ends instead.
                    _dropUtterance = drop;
                    _finalizedForUtterance = false;
                }

                if (!drop && _streaming is not null)
                    StartStream();

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

                // This utterance's transcript was already produced from its stream. The audio
                // must not be transcribed a second time.
                if (_finalizedForUtterance)
                {
                    _finalizedForUtterance = false;
                    continue;
                }

                if (_stream is not null)
                {
                    // The recognizer has not endpointed yet; let it finish rather than running
                    // the whole utterance through a second recognition pass.
                    forceFinalize = true;
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
                    _pending.Enqueue((null, pcm, 0));
                    continue;
                }

                _turnTask = RunTurnsAsync(null, pcm, 0, ct);
            }
        }

        if (forceFinalize)
            await FinalizeStreamAsync(ct);

        // Count speech episodes that start after this utterance was closed, and those that start
        // while the turn is already with the LLM. Both mean the recognizer was no longer (or never
        // was) listening, which is the risk of a silence window tuned to the old fixture.
        var speechStarted = frame.IsSpeech && !_previousFrameSpeech;
        _previousFrameSpeech = frame.IsSpeech;

        if (speechStarted)
        {
            if (_turns.State == VoiceState.Thinking)
            {
                _speechDuringThinking++;
            }
            else if (_stream is null && _endpointAt != 0
                && _time.GetElapsedTime(_endpointAt) <= LateSpeechWindow)
            {
                _lateSpeechAfterEndpoint++;
            }
        }
    }

    /// <summary>
    /// Opens a recognizer stream for the utterance that just started. The segmenter already owns
    /// the pre-roll plus everything said so far, so that buffer seeds the stream and the current
    /// frame is not fed twice.
    /// </summary>
    private void StartStream()
    {
        if (_streaming is null) return;

        ISttStream stream;
        lock (_gate)
        {
            if (_stream is not null)
            {
                // Defensive: a new utterance must never inherit the previous utterance's stream.
                _stream.Dispose();
                _stream = null;
                _endpointer = null;
            }

            stream = _prewarmed ?? _streaming.Begin();
            _prewarmed = null;
        }

        _stream = stream;
        _endpointer = new Endpointer(_config.MinSilenceMs);
        _partialSeq = 0;
        _partialCooldown = 0;
        _firstPartialSent = false;
        _streamClock.Restart();
        _finalizedForUtterance = false;
        _streamPcm.Clear();

        var seed = _segmenter.CurrentUtterance();
        if (seed.Length > 0)
        {
            stream.Accept(seed);
            _streamPcm.AddRange(seed);
            TrimStreamPcm();
        }
    }

    private void TrimStreamPcm()
    {
        if (_streamPcm.Count <= SpeechSegmenter.MaxUtteranceSamples) return;
        _streamPcm.RemoveRange(0, _streamPcm.Count - SpeechSegmenter.MaxUtteranceSamples);
    }

    /// <summary>
    /// Finalizes the live stream and starts the turn from its text. The audio is kept only so the
    /// merge path can still reuse it; no second recognition pass runs for this utterance.
    /// </summary>
    private async Task FinalizeStreamAsync(CancellationToken ct)
    {
        var stream = _stream;
        if (stream is null) return;

        _endpointAt = _time.GetTimestamp();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        SttResult result;
        try
        {
            result = await stream.FinalizeAsync(ct);
        }
        catch (OperationCanceledException)
        {
            stream.Dispose();
            _stream = null;
            _endpointer = null;
            return;
        }
        catch (Exception ex)
        {
            await TrySendAsync(new PipelineError($"stt: {ex.Message}"));
            result = new SttResult("", 0);
        }

        stopwatch.Stop();

        stream.Dispose();
        _stream = null;
        _endpointer = null;
        _finalizedForUtterance = true;
        _partialCooldown = 0;

        PrewarmStream();

        var finalText = TranscriptCleaner.Clean(result.Text);

        // Merging is textual now: the cancelled turn's final transcript is prepended, because
        // the recognizer stream for this utterance heard only the new speech. Keep the merge text
        // when this final is empty, so the next utterance can still carry it.
        if (finalText.Length > 0 && _mergeText is { Length: > 0 })
        {
            finalText = $"{_mergeText} {finalText}";
            _mergeText = null;
        }

        if (finalText.Length == 0) return;

        var audio = _streamPcm.ToArray();
        _streamPcm.Clear();

        lock (_gate)
        {
            if (_turnTask is { IsCompleted: false })
            {
                if (_pending.Count >= PendingLimit) _pending.Dequeue();
                _pending.Enqueue((finalText, audio, stopwatch.Elapsed.TotalMilliseconds));
            }
            else
            {
                _turnTask = RunTurnsAsync(finalText, audio, stopwatch.Elapsed.TotalMilliseconds, ct);
            }
        }
    }

    /// <summary>
    /// Creates the next recognizer stream before it is needed: stream construction and its first
    /// feature frames are a measurable part of the short-utterance overhead.
    /// </summary>
    private void PrewarmStream()
    {
        if (_streaming is null) return;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        _ = Task.Run(() =>
        {
            try
            {
                var stream = _streaming.Begin();
                lock (_gate)
                {
                    _prewarmed?.Dispose();
                    _prewarmed = stream;
                }

                Console.WriteLine($"stream prewarm {stopwatch.Elapsed.TotalMilliseconds:F0} ms");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"stream prewarm failed: {ex.Message}");
            }
        });
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

    private async Task RunTurnsAsync(
        string? text, short[] first, double sttMs, CancellationToken ct)
    {
        var next = (Text: text, Pcm: first, SttMs: sttMs);

        while (true)
        {
            try
            {
                await HandleUtteranceAsync(next.Text, next.Pcm, next.SttMs, ct);
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
                if (_pending.Count > 0)
                {
                    next = _pending.Dequeue();
                }
                else
                {
                    // Released under the lock so the next utterance cannot be appended to a loop
                    // that has already decided to exit.
                    _turnTask = null;
                    return;
                }
            }
        }
    }

    private async Task HandleUtteranceAsync(
        string? text, short[] pcm, double sttMs, CancellationToken sessionCt)
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
            _llmFirstUpdateMs = double.NaN;
            _llmFirstTokenMs = double.NaN;
            _firstTokenAtMs = double.NaN;
        }

        try
        {
            await RunUtteranceAsync(text, pcm, sttMs, turnId, sessionCt);
        }
        finally
        {
            LogTurn(turnId);
        }
    }

    private async Task RunUtteranceAsync(
        string? text, short[] pcm, double sttMs, int turnId, CancellationToken sessionCt)
    {
        var ct = _turns.TurnToken;

        await TrySendAsync(new TurnStarted(turnId, DateTimeOffset.UtcNow));

        SttResult result;
        if (text is not null)
        {
            // The stream already heard this utterance; only the final flush is left, and that
            // cost is what the user feels after they stop talking.
            result = new SttResult(text, sttMs);
            if (sttMs > 0) await TrySendAsync(new StageTiming("finalize", sttMs));
        }
        else
        {
            try
            {
                result = await _stt.TranscribeAsync(pcm, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (ct.IsCancellationRequested) return;

        _sttWallMs = result.LatencyMs;

        // Exactly what the recognizer heard, plus its own transcript, so an accuracy complaint
        // can be reproduced offline instead of guessed at.
        _debugAudio.Write(turnId, pcm, result.Text);

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
            _lastUtteranceText = result.Text;
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
                    _firstTokenAtMs = _turnClock.Elapsed.TotalMilliseconds;
                    _llmFirstUpdateMs = _llm.LastFirstUpdateMs;
                    _llmFirstTokenMs = _llm.LastFirstTokenMs;
                    await TrySendAsync(new StageTiming("firstToken", _firstTokenAtMs));
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

        var reply = full.ToString().Trim();

        lock (_gate)
        {
            if (ct.IsCancellationRequested) return;

            if (reply.Length > 0)
            {
                _history.AddAssistant(reply);
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

                // A cancelled turn must never send audio, even if the blocking synth finished.
                if (ct.IsCancellationRequested) return;

                lock (_gate)
                {
                    _ttsWallMs += sw.Elapsed.TotalMilliseconds;
                    _synthAudioMs += chunk.DurationMs;
                    if (index == 0) _firstChunkChars = text.Length;

                    if (index < _durations.Count)
                        _durations[index] = chunk.DurationMs;
                }

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
                _mergeAt = _time.GetTimestamp();

                if (_streaming is null)
                {
                    _mergeBase = _lastUtterancePcm.Length > 0 ? _lastUtterancePcm : null;
                }
                else
                {
                    // Streaming merges by text: the new stream never heard the cancelled turn.
                    _mergeBase = null;
                    _mergeText = _lastUtteranceText.Length > 0 ? _lastUtteranceText : null;
                }
            }
            else
            {
                _mergeBase = null;
                _mergeText = null;
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
        double firstToken, llmFirst, llmToken;

        lock (_gate)
        {
            audio = _audioSeconds;
            stt = _sttWallMs;
            tts = _ttsWallMs;
            synthMs = _synthAudioMs;
            chunks = _sentences.Count;
            firstChars = _firstChunkChars;
            cancels = _cancelledThisTurn;
            firstToken = _firstTokenAtMs;
            llmFirst = _llmFirstUpdateMs;
            llmToken = _llmFirstTokenMs;
        }

        var sttRtf = audio > 0.05 ? stt / (audio * 1000) : 0;
        var ttsRtf = synthMs > 0.05 ? tts / synthMs : 0;

        Console.WriteLine(
            $"turn {turnId} audio={audio:F1}s stt={stt:F0}ms ({sttRtf:F2}x) " +
            $"tts={tts:F0}ms ({ttsRtf:F2}x) chunks={chunks} firstChunk={firstChars} " +
            $"token={Ms(firstToken)} llm={Ms(llmFirst)}/{Ms(llmToken)} " +
            $"cancelled={cancels} lateSpeech={_lateSpeechAfterEndpoint} " +
            $"thinkingSpeech={_speechDuringThinking}");
    }

    /// <summary>Rounds a millisecond field, or prints a dash when the stage never ran.</summary>
    private static string Ms(double value) => double.IsNaN(value) ? "-" : value.ToString("F0");

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
        ISttStream? stream;
        ISttStream? prewarmed;

        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pending.Clear();
            stream = _stream;
            prewarmed = _prewarmed;
            _stream = null;
            _prewarmed = null;
        }

        stream?.Dispose();
        prewarmed?.Dispose();
        _turns.Cancel("dispose");
    }
}
