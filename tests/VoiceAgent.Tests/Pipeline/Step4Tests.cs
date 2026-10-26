using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using VoiceAgent.Server.Contracts;
using VoiceAgent.Server.Pipeline;
using VoiceAgent.Server.Sessions;

namespace VoiceAgent.Tests.Pipeline;

/// <summary>
/// Step 4: half-duplex capture, pre-roll seeding on barge-in, textual merge, the long-utterance
/// cap and the empty-final guard.
/// </summary>
public sealed class Step4Tests
{
    [Fact]
    public async Task Speaking_is_half_duplex_and_barge_in_opens_a_pre_roll_seeded_stream()
    {
        var stt = new ScriptedStreamingStt("turn on the kitchen lights");
        var llm = new CountingClient();
        var sink = new RecordingSink();
        var turns = new TurnCoordinator(TimeProvider.System, TimeSpan.FromMilliseconds(500));
        var segmenter = new SpeechSegmenter(new RmsVad(), new VadOptions());
        using var pipeline = new VoicePipeline(
            segmenter, stt, new LlmService(llm), new FakeTts(), sink, turns);

        // Put the session in Speaking without running a turn: the recognizer must stay silent
        // until a barge-in is confirmed.
        turns.BeginThinking();
        Assert.True(turns.SentenceStarted(turns.TurnId, 0, 5000));
        Assert.Equal(VoiceState.Speaking, turns.State);

        await Push(pipeline, Quiet(8));

        // Speech loud enough for the VAD but below the echo floor, so the segmenter opens the
        // utterance first and the barge-in gate confirms it a little later.
        await Push(pipeline, Medium(20));

        Assert.Equal(0, stt.BeginCount);
        Assert.Empty(stt.AllAccepted());

        await Push(pipeline, Loud(20));

        Assert.Equal(VoiceState.Listening, turns.State);
        Assert.Contains(sink.Events, e => e is TurnCancelled);

        var stream = stt.Streams[0];
        var accepted = stream.Accepted;

        // The seed is the segmenter's pre-roll plus the first word, not just the confirming frame.
        Assert.True(accepted.Length >= (150 + 250) * 16,
            $"accepted={accepted.Length / 16.0:F0} ms");
        Assert.All(accepted.Take(120 * 16), sample => Assert.Equal(0, sample));
        Assert.Contains(accepted, sample => sample != 0);

        await Push(pipeline, Quiet(40));
        await (pipeline.CurrentTurn ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(30));

        var transcript = Assert.Single(sink.Events.OfType<Transcript>());
        Assert.Equal("turn on the kitchen lights", transcript.Text);
    }

    [Fact]
    public async Task Thinking_merge_is_textual_and_leaves_one_user_message()
    {
        var stt = new ScriptedStreamingStt("turn on the lights", "in the kitchen");
        var llm = new GatedLlm();
        var sink = new RecordingSink();
        var turns = new TurnCoordinator(TimeProvider.System, TimeSpan.FromMilliseconds(500));
        var segmenter = new SpeechSegmenter(new RmsVad(), new VadOptions());
        using var pipeline = new VoicePipeline(
            segmenter, stt, new LlmService(llm), new FakeTts(), sink, turns);

        await Push(pipeline, Loud(30));
        await Push(pipeline, Quiet(40));

        // The first stream finalized and the turn is with the LLM, which is deliberately slow.
        Assert.Contains(sink.Events, e => e is Transcript { Text: "turn on the lights" });
        Assert.Equal(VoiceState.Thinking, turns.State);

        // The user speaks again before the reply starts: the turn is cancelled and merged.
        await Push(pipeline, Loud(20));
        Assert.Contains(sink.Events, e => e is TurnCancelled { Reason: "merge" });
        llm.Release();

        await Push(pipeline, Quiet(40));
        await (pipeline.CurrentTurn ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(30));

        var transcripts = sink.Events.OfType<Transcript>().ToList();
        Assert.Equal("turn on the lights in the kitchen", transcripts[^1].Text);

        var users = pipeline.History.Where(m => m.Role == ChatRole.User).ToList();
        var user = Assert.Single(users);
        Assert.Equal("turn on the lights in the kitchen", user.Text);

        // The cancelled turn's call never completed; only the merged reply did.
        Assert.Equal(2, llm.Calls);
        Assert.Equal(1, llm.CompletedCalls);
        Assert.DoesNotContain(sink.Events, e => e is TurnCancelled { Reason: "barge" });
    }

    [Fact]
    public async Task Twenty_seconds_of_speech_is_finalized_by_the_hard_cap()
    {
        var stt = new ScriptedStreamingStt("a very long question");
        var sink = new RecordingSink();
        var turns = new TurnCoordinator(TimeProvider.System, TimeSpan.FromMilliseconds(500));
        var segmenter = new SpeechSegmenter(new RmsVad(), new VadOptions());
        using var pipeline = new VoicePipeline(
            segmenter, stt, new LlmService(new CountingClient()), new FakeTts(), sink, turns);

        // No silence at all: only the endpointer's hard cap can close this utterance. Stop as
        // soon as it finalizes so the remaining audio cannot barge in on the new turn.
        for (var i = 0; i < 1200; i++)
        {
            await pipeline.PushAudioAsync(Loud(1), CancellationToken.None);
            if (stt.BeginCount > 0 && stt.Streams[0].FinalizeCalls > 0) break;
        }

        await (pipeline.CurrentTurn ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, stt.Streams[0].FinalizeCalls);
        Assert.Equal(1, stt.UsedStreamCount);
        Assert.Single(sink.Events.OfType<TurnStarted>());
        Assert.Single(sink.Events.OfType<Transcript>());
    }

    [Fact]
    public async Task A_nineteen_second_question_is_one_turn()
    {
        var stt = new ScriptedStreamingStt("a long question");
        var sink = new RecordingSink();
        var turns = new TurnCoordinator(TimeProvider.System, TimeSpan.FromMilliseconds(500));
        var segmenter = new SpeechSegmenter(new RmsVad(), new VadOptions());
        using var pipeline = new VoicePipeline(
            segmenter, stt, new LlmService(new CountingClient()), new FakeTts(), sink, turns);

        // Longer than the segmenter's old 15 s cap but inside the endpointer's 20 s cap.
        await Push(pipeline, Loud(950));
        await Push(pipeline, Quiet(40));
        await (pipeline.CurrentTurn ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, stt.UsedStreamCount);
        Assert.Single(sink.Events.OfType<TurnStarted>());
        Assert.Single(sink.Events.OfType<Transcript>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" (noise) ")]
    [InlineData("[BLANK_AUDIO]")]
    public async Task An_empty_or_noise_only_final_discards_the_turn(string final)
    {
        var stt = new ScriptedStreamingStt(final);
        var llm = new CountingClient();
        var sink = new RecordingSink();
        var turns = new TurnCoordinator(TimeProvider.System, TimeSpan.FromMilliseconds(500));
        var segmenter = new SpeechSegmenter(new RmsVad(), new VadOptions());
        using var pipeline = new VoicePipeline(
            segmenter, stt, new LlmService(llm), new FakeTts(), sink, turns);

        await Push(pipeline, Loud(30));
        await Push(pipeline, Quiet(40));

        Assert.Equal(1, stt.Streams[0].FinalizeCalls);
        Assert.Empty(sink.Events.OfType<TurnStarted>());
        Assert.Empty(sink.Events.OfType<Transcript>());
        Assert.Equal(0, llm.Calls);
    }

    private static async Task Push(VoicePipeline pipeline, short[] pcm)
    {
        for (var offset = 0; offset + 320 <= pcm.Length; offset += 320)
            await pipeline.PushAudioAsync(pcm.AsMemory(offset, 320).ToArray(), CancellationToken.None);
    }

    private static short[] Loud(int frames) => Sine(frames, 8000);

    private static short[] Medium(int frames) => Sine(frames, 1200);

    private static short[] Sine(int frames, double amplitude)
    {
        var pcm = new short[frames * 320];
        for (var i = 0; i < pcm.Length; i++)
            pcm[i] = (short)(amplitude * Math.Sin(2 * Math.PI * 200 * i / 16000.0));
        return pcm;
    }

    private static short[] Quiet(int frames) => new short[frames * 320];

    private sealed class RmsVad : IVadModel
    {
        public float Predict(ReadOnlySpan<short> window)
        {
            double sum = 0;
            foreach (var s in window) sum += s / 32768.0 * (s / 32768.0);
            return Math.Sqrt(sum / window.Length) > 0.01 ? 0.95f : 0.02f;
        }

        public void Reset() { }
    }

    private sealed class ScriptedStreamingStt(params string[] finals) : ISttService, IStreamingStt
    {
        private readonly Queue<string> _finals = new(finals);
        private readonly object _gate = new();
        private readonly List<ScriptedStream> _streams = [];

        public IReadOnlyList<ScriptedStream> Streams
        {
            get
            {
                lock (_gate) return [.. _streams];
            }
        }

        public int BeginCount
        {
            get
            {
                lock (_gate) return _streams.Count;
            }
        }

        /// <summary>Streams that were actually fed, i.e. excluding the background pre-warm.</summary>
        public int UsedStreamCount
        {
            get
            {
                lock (_gate) return _streams.Count(s => s.FinalizeCalls > 0);
            }
        }

        public ISttStream Begin()
        {
            var stream = new ScriptedStream(_finals.Count > 0 ? _finals.Dequeue() : "");
            lock (_gate) _streams.Add(stream);
            return stream;
        }

        /// <summary>These tests exercise the streaming path only; the audio fallback must not run.</summary>
        public Task<SttResult> TranscribeAsync(short[] pcm16k, CancellationToken ct) =>
            throw new NotSupportedException("streaming path expected");

        public short[] AllAccepted()
        {
            lock (_gate) return [.. _streams.SelectMany(s => s.Accepted)];
        }

        public void Dispose() { }
    }

    private sealed class ScriptedStream(string text) : ISttStream
    {
        private readonly List<short> _pcm = [];
        private int _seq;

        public string Text { get; } = text;
        public int FinalizeCalls { get; private set; }

        public short[] Accepted
        {
            get
            {
                lock (_pcm) return [.. _pcm];
            }
        }

        public void Accept(ReadOnlySpan<short> pcm16k)
        {
            lock (_pcm) _pcm.AddRange(pcm16k.ToArray());
            _seq++;
        }

        public SttSnapshot Current => new(Text, _seq, false);

        public Task<SttResult> FinalizeAsync(CancellationToken ct)
        {
            FinalizeCalls++;
            return Task.FromResult(new SttResult(Text, 1));
        }

        public void Dispose() { }
    }

    private sealed class CountingClient : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Sure.");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class GatedLlm : IChatClient
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _calls;
        private int _completed;

        public int Calls => Volatile.Read(ref _calls);

        /// <summary>Calls that produced a reply; a cancelled call never gets here.</summary>
        public int CompletedCalls => Volatile.Read(ref _completed);

        public void Release() => _release.TrySetResult();

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);

            if (call == 1)
                await _release.Task.WaitAsync(cancellationToken);
            else
                await Task.Yield();

            Interlocked.Increment(ref _completed);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Sure.");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class FakeTts : ITtsService
    {
        public Task<TtsChunk> SynthesizeAsync(string text, CancellationToken ct) =>
            Task.FromResult(new TtsChunk(new byte[1000], 100));
    }

    private sealed class RecordingSink : IVoiceEventSink
    {
        public List<VoiceEvent> Events { get; } = [];

        public ValueTask SendAsync(VoiceEvent evt, CancellationToken ct = default)
        {
            Events.Add(evt);
            return ValueTask.CompletedTask;
        }
    }
}
