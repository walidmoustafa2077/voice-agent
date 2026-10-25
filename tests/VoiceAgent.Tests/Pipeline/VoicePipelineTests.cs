using Microsoft.Extensions.AI;
using VoiceAgent.Server;
using VoiceAgent.Server.Contracts;
using VoiceAgent.Server.Pipeline;
using VoiceAgent.Server.Sessions;

namespace VoiceAgent.Tests.Pipeline;

public sealed class VoicePipelineTests
{
    [Fact]
    public async Task Emits_transcripts_and_assistant_deltas_for_a_speech_fixture()
    {
        var pcm = Wav.ReadPcm16(Wav.Fixture("hello.wav"));
        Assert.True(pcm.Length > 16000, "fixture should be at least one second");

        var vad = new RmsVad(threshold: 0.01f);
        var segmenter = new SpeechSegmenter(vad, new VadOptions());
        using var stt = new SttService(ModelPaths.WhisperTinyEn);
        var llm = new LlmService(new ScriptedChatClient("Sure. Here you go."));
        var sink = new RecordingSink();
        using var turns = new TurnCoordinator(TimeProvider.System, TimeSpan.FromMilliseconds(500));
        var pipeline = new VoicePipeline(segmenter, stt, llm, sink, turns);

        var padded = pcm.Concat(new short[16000]).ToArray();
        foreach (var chunk in padded.Chunk(320))
            await pipeline.PushAudioAsync(chunk, CancellationToken.None);

        var transcripts = sink.Events.OfType<Transcript>().ToList();
        Assert.NotEmpty(transcripts);
        var joined = string.Join(" ", transcripts.Select(t => t.Text));
        Assert.Contains("hello", joined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("voice agent", joined, StringComparison.OrdinalIgnoreCase);

        Assert.Contains(sink.Events, e => e is AssistantDelta);
        Assert.True(turns.AllSentencesDispatched);
        Assert.Contains(sink.Events, e => e is StateChanged { State: VoiceState.Listening });
    }

    private sealed class RmsVad(float threshold) : IVadModel
    {
        public float Predict(ReadOnlySpan<short> window)
        {
            if (window.Length == 0)
                return 0;

            double sum = 0;
            foreach (var sample in window)
            {
                var value = sample / 32768.0;
                sum += value * value;
            }

            return Math.Sqrt(sum / window.Length) > threshold ? 0.95f : 0.02f;
        }

        public void Reset()
        {
        }
    }

    private sealed class ScriptedChatClient(string reply) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var word in reply.Split(' '))
            {
                await Task.Yield();
                yield return new ChatResponseUpdate(ChatRole.Assistant, word + " ");
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
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
