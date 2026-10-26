using Microsoft.Extensions.AI;
using VoiceAgent.Server.Audio;
using VoiceAgent.Server.Contracts;
using VoiceAgent.Server.Pipeline;
using VoiceAgent.Server.Sessions;

namespace VoiceAgent.Tests.Pipeline;

public sealed class DebugAudioTests : IDisposable
{
    private const int Frame = 320;
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "voiceagent-turn-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public async Task A_turn_saves_the_recognizer_audio_and_its_transcript()
    {
        var vad = new RmsVad();
        var segmenter = new SpeechSegmenter(vad, new VadOptions());
        var turns = new TurnCoordinator(TimeProvider.System, TimeSpan.FromMilliseconds(500));

        using var pipeline = new VoicePipeline(
            segmenter,
            new FixedStt("turn on the kitchen lights"),
            new LlmService(new InstantClient()),
            new FakeTts(),
            new Sink(),
            turns,
            debugAudio: new AudioDebugWriter(enabled: true, _dir));

        await PushAsync(pipeline, Loud(30));
        await PushAsync(pipeline, Quiet(40));

        await WaitForAsync(() =>
            Directory.Exists(_dir) && Directory.GetFiles(_dir, "*.wav").Length == 1);

        var wavPath = Directory.GetFiles(_dir, "*.wav").Single();
        var wav = File.ReadAllBytes(wavPath);

        Assert.True(wav.Length > Pcm16Wav.HeaderBytes);
        Assert.Equal(0, (wav.Length - Pcm16Wav.HeaderBytes) % 2);
        Assert.Equal(
            "turn on the kitchen lights",
            File.ReadAllText(Path.ChangeExtension(wavPath, ".stream.txt")));
    }

    private static async Task PushAsync(VoicePipeline pipeline, short[] pcm)
    {
        for (var offset = 0; offset + Frame <= pcm.Length; offset += Frame)
            await pipeline.PushAudioAsync(pcm.AsMemory(offset, Frame).ToArray(), CancellationToken.None);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
            await Task.Delay(10);

        Assert.True(condition(), "the debug WAV and transcript were not written");
    }

    private static short[] Loud(int frames)
    {
        var pcm = new short[frames * Frame];
        for (var i = 0; i < pcm.Length; i++)
            pcm[i] = (short)(Math.Sin(i * 2 * Math.PI * 200 / 16000) * 8000);
        return pcm;
    }

    private static short[] Quiet(int frames) => new short[frames * Frame];

    private sealed class RmsVad : IVadModel
    {
        public void Reset() { }

        public float Predict(ReadOnlySpan<short> pcm)
        {
            var sum = 0.0;
            foreach (var sample in pcm) sum += sample * (double)sample;
            var rms = Math.Sqrt(sum / Math.Max(1, pcm.Length)) / 32768.0;
            return rms > 0.01 ? 0.95f : 0.02f;
        }
    }

    private sealed class FixedStt(string text) : ISttService
    {
        public Task<SttResult> TranscribeAsync(short[] pcm16k, CancellationToken ct) =>
            Task.FromResult(new SttResult(text, 1));
    }

    private sealed class InstantClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
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

    private sealed class Sink : IVoiceEventSink
    {
        public ValueTask SendAsync(VoiceEvent evt, CancellationToken ct = default) =>
            ValueTask.CompletedTask;
    }
}
