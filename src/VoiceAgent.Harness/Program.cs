using NAudio.Wave;
using VoiceAgent.Harness;
using VoiceAgent.Server;
using VoiceAgent.Server.Audio;
using VoiceAgent.Server.Options;
using VoiceAgent.Server.Pipeline;
using VoiceAgent.Server.Sessions;

// Benchmark mode runs before the file-path check so the flag is not read as a WAV path.
if (args.Length > 0 && args[0] == "--tts-bench")
    return await TtsBench.RunAsync();

if (args.Length > 0 && args[0] == "--stt-bench")
    return SttBench.Run(args.Skip(1).ToArray());

if (args.Length > 0 && args[0] == "--llm-bench")
    return await LlmBench.RunAsync(args.Length > 1 && int.TryParse(args[1], out var n) ? n : 10);

if (args.Length > 0 && args[0] == "--compare")
    return await SttCompare.RunAsync(args.Skip(1).ToArray());

if (args.Length > 1 && args[0] == "--stt-hotwords")
    return await SttModelBench.HotwordsAsync(args.Skip(2).ToArray(), args[1]);

if (args.Length > 0 && args[0] == "--stt-models")
    return await SttModelBench.RunAsync(args.Skip(1).ToArray());

if (args.Length > 0 && args[0] == "--stt-api")
    return SttModelBench.CheckApi();

var path = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));

if (path is null || !File.Exists(path))
{
    Console.Error.WriteLine("usage: VoiceAgent.Harness <file.wav> [--no-audio]");
    Console.Error.WriteLine("       VoiceAgent.Harness --stt-bench <file.wav>...");
    Console.Error.WriteLine("       VoiceAgent.Harness --tts-bench");
    Console.Error.WriteLine("       VoiceAgent.Harness --llm-bench [turns]");
    Console.Error.WriteLine("       VoiceAgent.Harness --compare <file.wav>...");
    Console.Error.WriteLine("       VoiceAgent.Harness --stt-hotwords <a,b,c> <file.wav>...");
    Console.Error.WriteLine("       VoiceAgent.Harness --stt-models <file.wav>...");
    Console.Error.WriteLine("       VoiceAgent.Harness --stt-api");
    return 1;
}

using var vad = new SileroVadModel(ModelPaths.SileroVad);
var cfg = new VoiceConfig();
var segmenter = new SpeechSegmenter(vad, new VadOptions
{
    Threshold = cfg.VadThreshold,
    MinSpeechMs = cfg.MinSpeechMs,
    MinSilenceMs = cfg.MinSilenceMs,
    PreRollMs = cfg.PreRollMs,
});
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
