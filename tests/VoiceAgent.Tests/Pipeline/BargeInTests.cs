using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using VoiceAgent.Server.Contracts;
using VoiceAgent.Server.Pipeline;
using VoiceAgent.Server.Sessions;

namespace VoiceAgent.Tests.Pipeline;

public sealed class BargeInTests
{
    [Fact]
    public async Task Speech_during_thinking_merges_into_one_turn()
    {
        var stt = new GatedStt();
        var llm = new CountingClient();
        var sink = new RecordingSink();
        var turns = new TurnCoordinator(TimeProvider.System, TimeSpan.FromMilliseconds(500));
        var segmenter = new SpeechSegmenter(new RmsVad(), new VadOptions());
        using var pipeline = new VoicePipeline(
            segmenter, stt, new LlmService(llm), new FakeTts(), sink, turns);

        // Utterance one: 600 ms speech, then enough silence to end it. STT blocks here.
        await Push(pipeline, Loud(30));
        await Push(pipeline, Quiet(31));
        await WaitUntil(() => turns.State == VoiceState.Thinking);

        // The user keeps talking while the agent is thinking: 300 ms of speech.
        await Push(pipeline, Loud(15));

        Assert.Contains(sink.Events, e => e is TurnCancelled { Reason: "merge" });

        // Release the first STT pass; it was cancelled, so only the merged one completes.
        stt.Release();
        await Push(pipeline, Quiet(40));

        await (pipeline.CurrentTurn ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(sink.Events.OfType<Transcript>().Count() == 1,
            $"stt calls={stt.Calls.Count}; turns={turns.State}; events={string.Join(",", sink.Events.Select(e => e.GetType().Name))}");
        Assert.Equal(1, llm.Calls);
        Assert.Equal(2, stt.Calls.Count);
        Assert.True(stt.Calls[1].Length > stt.Calls[0].Length,
            "merged audio should contain the aborted utterance as its opening");
    }

    [Fact]
    public async Task Threshold_is_raised_only_while_speaking()
    {
        var stt = new GatedStt { AutoComplete = true };
        var sink = new RecordingSink();
        var turns = new TurnCoordinator(TimeProvider.System, TimeSpan.FromMilliseconds(500));
        var segmenter = new SpeechSegmenter(new RmsVad(), new VadOptions());
        using var pipeline = new VoicePipeline(
            segmenter, stt, new LlmService(new CountingClient()), new FakeTts(), sink, turns);

        await Push(pipeline, Loud(30));
        await Push(pipeline, Quiet(31));
        await (pipeline.CurrentTurn ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(30));

        var chunk = sink.Events.OfType<AudioChunk>().First();
        var turnId = sink.Events.OfType<TurnStarted>().Last().TurnId;

        await pipeline.SentenceStartedAsync(turnId, chunk.Index);
        Assert.Equal(0.6f, pipeline.CurrentVadThreshold);

        await pipeline.SentencePlayedAsync(turnId, chunk.Index);
        Assert.Equal(0.5f, pipeline.CurrentVadThreshold);
    }

    private static async Task Push(VoicePipeline pipeline, short[] pcm)
    {
        for (var offset = 0; offset + 320 <= pcm.Length; offset += 320)
            await pipeline.PushAudioAsync(pcm.AsMemory(offset, 320).ToArray(), CancellationToken.None);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);

        Assert.True(condition());
    }

    private static short[] Loud(int frames)
    {
        var pcm = new short[frames * 320];
        for (var i = 0; i < pcm.Length; i++)
            pcm[i] = (short)(8000 * Math.Sin(2 * Math.PI * 200 * i / 16000.0));
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

    private sealed class GatedStt : ISttService
    {
        private readonly TaskCompletionSource _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool AutoComplete { get; init; }
        public List<short[]> Calls { get; } = [];

        public void Release() => _gate.TrySetResult();

        public async Task<SttResult> TranscribeAsync(short[] pcm16k, CancellationToken ct)
        {
            Calls.Add(pcm16k);
            if (!AutoComplete) await _gate.Task.WaitAsync(ct);
            return new SttResult("hello", 1);
        }
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
