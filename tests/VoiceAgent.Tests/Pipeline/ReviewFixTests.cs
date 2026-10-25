using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;
using VoiceAgent.Server.Contracts;
using VoiceAgent.Server.Pipeline;
using VoiceAgent.Server.Sessions;

namespace VoiceAgent.Tests.Pipeline;

/// <summary>
/// Regression tests for the review findings: cancellation must be a hard boundary, history and the
/// client bubble must shrink to what was heard, and echo must not start turns.
/// </summary>
public sealed class ReviewFixTests
{
    private const string Reply =
        "The first sentence is long enough to synthesize on its own. " +
        "The second sentence is also long enough and must be cut when the user interrupts.";

    [Fact]
    public async Task Stop_during_playback_trims_history_and_reports_what_was_heard()
    {
        var h = await CreatePipeline();
        using var pipeline = h.Pipeline;

        var sentences = pipeline.Sentences;
        Assert.True(sentences.Count >= 2, $"expected at least two sentences, got {sentences.Count}");

        var turnId = h.Turns.TurnId;
        await pipeline.SentenceStartedAsync(turnId, 0);
        await pipeline.SentencePlayedAsync(turnId, 0);
        Assert.Equal(VoiceState.Speaking, h.Turns.State);

        await pipeline.StopSpeakingAsync("user");

        var cancelled = Assert.Single(h.Sink.Events.OfType<TurnCancelled>());
        Assert.Equal(turnId, cancelled.TurnId);
        Assert.Equal(0, cancelled.LastPlayedIndex);
        Assert.Equal(sentences[0], cancelled.HeardText);
        Assert.False(cancelled.RemoveLastUser);
        Assert.Equal(VoiceState.Listening, h.Turns.State);

        var assistant = Assert.Single(pipeline.History, m => m.Role == ChatRole.Assistant);
        Assert.Contains(sentences[0], assistant.Text!);
        Assert.Contains("[interrupted]", assistant.Text!);
        Assert.DoesNotContain(sentences[1], assistant.Text!);
    }

    [Fact]
    public async Task Echo_that_starts_while_speaking_never_starts_a_turn()
    {
        var h = await CreatePipeline();
        using var pipeline = h.Pipeline;

        var turnId = h.Turns.TurnId;
        await pipeline.SentenceStartedAsync(turnId, 0);
        var sttCalls = h.Stt.Calls;

        // A 0.02 RMS tone passes the VAD while speaking but sits below the calibrated echo floor.
        // It starts and ends entirely while the agent is speaking.
        await Push(pipeline, Echo(30));
        await Push(pipeline, Quiet(40));

        Assert.Equal(sttCalls, h.Stt.Calls);
        Assert.Single(h.Sink.Events.OfType<TurnStarted>());
        Assert.DoesNotContain(h.Sink.Events, e => e is TurnCancelled);
        Assert.Equal(VoiceState.Speaking, h.Turns.State);
    }

    [Fact]
    public async Task A_confirmed_barge_in_during_speaking_starts_the_next_turn()
    {
        var h = await CreatePipeline();
        using var pipeline = h.Pipeline;

        var turnId = h.Turns.TurnId;
        await pipeline.SentenceStartedAsync(turnId, 0);
        var sttCalls = h.Stt.Calls;

        await Push(pipeline, Loud(15)); // 300 ms: enough to confirm an interruption
        Assert.Contains(h.Sink.Events, e => e is TurnCancelled { Reason: "barge" });

        // The utterance keeps going; its end must still become the next user turn.
        await Push(pipeline, Loud(30));
        await Push(pipeline, Quiet(40));
        await (pipeline.CurrentTurn ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(sttCalls + 1, h.Stt.Calls);
        var turnEvents = h.Sink.Events.OfType<TurnStarted>().ToList();
        Assert.Equal(2, turnEvents.Count);

        // Finish the new turn through playback so the watchdog is not the thing that ends it.
        var newTurn = turnEvents[^1].TurnId;
        foreach (var chunk in h.Sink.Events.OfType<AudioChunk>().Where(c => c.TurnId == newTurn))
        {
            await pipeline.SentenceStartedAsync(newTurn, chunk.Index);
            await pipeline.SentencePlayedAsync(newTurn, chunk.Index);
        }

        Assert.Equal(VoiceState.Listening, h.Turns.State);
    }

    private static async Task<Harness> CreatePipeline()
    {
        var time = new FakeTimeProvider();
        var turns = new TurnCoordinator(time, TimeSpan.FromMilliseconds(500));
        var stt = new FakeStt();
        var tts = new FakeTts();
        var sink = new RecordingSink();
        var pipeline = new VoicePipeline(
            new SpeechSegmenter(new RmsVad(), new VadOptions()),
            stt,
            new LlmService(new ScriptedClient(Reply)),
            tts,
            sink,
            turns,
            time: time);

        // A completed UTC wall-clock instant is irrelevant here; only the fake timers matter.
        await Push(pipeline, Loud(30));
        await Push(pipeline, Quiet(40));
        await (pipeline.CurrentTurn ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, pipeline.Sentences.Count);
        Assert.True(turns.AllSentencesDispatched);

        return new Harness(pipeline, stt, sink, turns);
    }

    private static async Task Push(VoicePipeline pipeline, short[] pcm)
    {
        for (var offset = 0; offset + 320 <= pcm.Length; offset += 320)
            await pipeline.PushAudioAsync(pcm.AsMemory(offset, 320).ToArray(), CancellationToken.None);
    }

    private static short[] Loud(int frames)
    {
        var pcm = new short[frames * 320];
        for (var i = 0; i < pcm.Length; i++)
            pcm[i] = (short)(8000 * Math.Sin(2 * Math.PI * 200 * i / 16000.0));
        return pcm;
    }

    private static short[] Echo(int frames)
    {
        var pcm = new short[frames * 320];
        for (var i = 0; i < pcm.Length; i++)
            pcm[i] = (short)(655 * Math.Sin(2 * Math.PI * 200 * i / 16000.0));
        return pcm;
    }

    private static short[] Quiet(int frames) => new short[frames * 320];

    private sealed record Harness(
        VoicePipeline Pipeline,
        FakeStt Stt,
        RecordingSink Sink,
        TurnCoordinator Turns);

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

    private sealed class FakeStt : ISttService
    {
        public int Calls { get; private set; }

        public Task<SttResult> TranscribeAsync(short[] pcm16k, CancellationToken ct)
        {
            Calls++;
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);

            return Task.FromResult(new SttResult("hello", 1));
        }
    }

    private sealed class FakeTts : ITtsService
    {
        public Task<TtsChunk> SynthesizeAsync(string text, CancellationToken ct) =>
            Task.FromResult(new TtsChunk(new byte[1000], 100));
    }

    private sealed class RecordingSink : IVoiceEventSink
    {
        private readonly object _gate = new();
        private readonly List<VoiceEvent> _events = [];

        /// <summary>A snapshot: the detached turn keeps sending while the test reads.</summary>
        public IReadOnlyList<VoiceEvent> Events
        {
            get
            {
                lock (_gate) return [.. _events];
            }
        }

        public ValueTask SendAsync(VoiceEvent evt, CancellationToken ct = default)
        {
            lock (_gate) _events.Add(evt);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedClient(string reply) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var word in reply.Split(' '))
            {
                await Task.Yield();
                yield return new ChatResponseUpdate(ChatRole.Assistant, word + " ");
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
