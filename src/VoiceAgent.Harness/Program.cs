using NAudio.Wave;
using VoiceAgent.Harness;
using VoiceAgent.Server;
using VoiceAgent.Server.Audio;
using VoiceAgent.Server.Pipeline;
using VoiceAgent.Server.Sessions;

// Benchmark mode runs before the file-path check so the flag is not read as a WAV path.
if (args.Length > 0 && args[0] == "--tts-bench")
    return await TtsBench.RunAsync();

if (args.Length > 0 && args[0] == "--stt-bench")
    return SttBench.Run(args.Skip(1).ToArray());

var path = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));

if (path is null || !File.Exists(path))
{
    Console.Error.WriteLine("usage: VoiceAgent.Harness <file.wav> [--no-audio]");
    return 1;
}

using var vad = new SileroVadModel(ModelPaths.SileroVad);
var segmenter = new SpeechSegmenter(vad, new VadOptions());
var turns = new TurnCoordinator(TimeProvider.System, TimeSpan.FromMilliseconds(500));
var stt = SttFactory.Create();
Console.WriteLine($"stt: {SttFactory.Description}");
var tts = TtsFactory.Create();
var llm = new LlmService(
    new OllamaSharp.OllamaApiClient(new Uri("http://localhost:11434"), "gemma4:31b-cloud"));
var sink = new ConsoleVoiceEventSink();
using var pipeline = new VoicePipeline(segmenter, stt, llm, tts, sink, turns);

using var reader = new AudioFileReader(path);
var channels = reader.WaveFormat.Channels;
var rate = reader.WaveFormat.SampleRate;
var resampler = new StreamingResampler(rate);
var frameSamples = rate / 50 * channels;
var buffer = new float[frameSamples];

Console.WriteLine($"file: {path} ({rate} Hz, {channels} ch)");

int read;
while ((read = reader.Read(buffer.AsSpan())) > 0)
{
    var mono = new short[read / channels];

    for (var i = 0; i < mono.Length; i++)
    {
        var sum = 0f;
        for (var c = 0; c < channels; c++) sum += buffer[i * channels + c];
        mono[i] = (short)Math.Clamp(sum / channels * short.MaxValue, short.MinValue, short.MaxValue);
    }

    await pipeline.PushAudioAsync(rate == 16000 ? mono : resampler.Process(mono), CancellationToken.None);
}

// Close the final utterance: the segmenter needs minSilence of quiet before it emits.
for (var i = 0; i < 80; i++)
    await pipeline.PushAudioAsync(new short[320], CancellationToken.None);

if (pipeline.CurrentTurn is { } turn)
{
    try
    {
        await turn.WaitAsync(TimeSpan.FromMinutes(5));
    }
    catch (TimeoutException)
    {
        Console.Error.WriteLine("turn did not finish within 5 minutes");
        return 2;
    }
}

Console.WriteLine();
Console.WriteLine("done");
(tts as IDisposable)?.Dispose();
(stt as IDisposable)?.Dispose();
return 0;
