using Microsoft.Extensions.Logging;

namespace VoiceAgent.Server;

public sealed record ModelFile(string Name, string Url, string Path, long MinBytes)
{
    public bool IsReady => File.Exists(Path) && new FileInfo(Path).Length >= MinBytes;
}

/// <summary>Downloads the local speech models on first run, never trusting a partial file.</summary>
public static class ModelDownloader
{
    public static ModelFile Silero { get; } = new(
        "Silero VAD",
        "https://raw.githubusercontent.com/snakers4/silero-vad/master/src/silero_vad/data/silero_vad.onnx",
        ModelPaths.SileroVad, 1_000_000);

    public static ModelFile Whisper { get; } = new(
        "Whisper tiny.en",
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.en.bin",
        ModelPaths.WhisperTinyEn, 70_000_000);

    public static ModelFile Kokoro { get; } = new(
        "Kokoro",
        "https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/download/v2.0.0/kokoro.onnx",
        ModelPaths.Kokoro, ModelPaths.KokoroMinBytes);

    private const string SupertonicBase =
        "https://huggingface.co/csukuangfj2/sherpa-onnx-supertonic-tts-int8-2026-03-06/resolve/main/";

    public static ModelFile SupertonicDurationPredictor { get; } = new(
        "Supertonic duration predictor",
        SupertonicBase + "duration_predictor.int8.onnx",
        ModelPaths.SupertonicDurationPredictor, 1_400_000);

    public static ModelFile SupertonicTextEncoder { get; } = new(
        "Supertonic text encoder",
        SupertonicBase + "text_encoder.int8.onnx",
        ModelPaths.SupertonicTextEncoder, 25_000_000);

    public static ModelFile SupertonicVectorEstimator { get; } = new(
        "Supertonic vector estimator",
        SupertonicBase + "vector_estimator.int8.onnx",
        ModelPaths.SupertonicVectorEstimator, 38_000_000);

    public static ModelFile SupertonicVocoder { get; } = new(
        "Supertonic vocoder",
        SupertonicBase + "vocoder.int8.onnx",
        ModelPaths.SupertonicVocoder, 24_000_000);

    public static ModelFile SupertonicConfig { get; } = new(
        "Supertonic config",
        SupertonicBase + "tts.json",
        ModelPaths.SupertonicConfig, 8_000);

    public static ModelFile SupertonicIndexer { get; } = new(
        "Supertonic unicode indexer",
        SupertonicBase + "unicode_indexer.bin",
        ModelPaths.SupertonicIndexer, 250_000);

    public static ModelFile SupertonicVoice { get; } = new(
        "Supertonic voice style",
        SupertonicBase + "voice.bin",
        ModelPaths.SupertonicVoice, 500_000);

    private const string SherpaBase =
        "https://huggingface.co/csukuangfj/sherpa-onnx-streaming-zipformer-en-kroko-2025-08-06/resolve/main/";

    public static ModelFile SherpaEncoder { get; } = new(
        "Kroko streaming Zipformer encoder",
        SherpaBase + "encoder.onnx",
        ModelPaths.SherpaEncoder, ModelPaths.SherpaEncoderMinBytes);

    public static ModelFile SherpaDecoder { get; } = new(
        "Kroko streaming Zipformer decoder",
        SherpaBase + "decoder.onnx",
        ModelPaths.SherpaDecoder, ModelPaths.SherpaDecoderMinBytes);

    public static ModelFile SherpaJoiner { get; } = new(
        "Kroko streaming Zipformer joiner",
        SherpaBase + "joiner.onnx",
        ModelPaths.SherpaJoiner, ModelPaths.SherpaJoinerMinBytes);

    public static ModelFile SherpaTokens { get; } = new(
        "Kroko streaming Zipformer tokens",
        SherpaBase + "tokens.txt",
        ModelPaths.SherpaTokens, ModelPaths.SherpaTokensMinBytes);

    public static IReadOnlyList<ModelFile> Required { get; } = BuildRequired();

    private static IReadOnlyList<ModelFile> BuildRequired()
    {
        var models = new List<ModelFile> { Silero, Whisper };

        if (VoiceAgent.Server.Pipeline.SttFactory.Mode ==
            VoiceAgent.Server.Pipeline.SttMode.Streaming)
        {
            models.AddRange([SherpaEncoder, SherpaDecoder, SherpaJoiner, SherpaTokens]);
        }

        if (VoiceAgent.Server.Pipeline.TtsFactory.IsKokoro)
            models.Add(Kokoro);

        if (VoiceAgent.Server.Pipeline.TtsFactory.IsSupertonic)
        {
            models.AddRange([
                SupertonicDurationPredictor, SupertonicTextEncoder, SupertonicVectorEstimator,
                SupertonicVocoder, SupertonicConfig, SupertonicIndexer, SupertonicVoice]);
        }

        return models;
    }

    public static async Task EnsureAsync(
        ModelFile model, HttpClient http, ILogger logger, CancellationToken ct)
    {
        if (model.IsReady) return;

        Directory.CreateDirectory(Path.GetDirectoryName(model.Path)!);
        var part = model.Path + ".part";
        if (File.Exists(part)) File.Delete(part);

        try
        {
            using var response = await http.GetAsync(
                model.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using (var file = File.Create(part))
            await using (var stream = await response.Content.ReadAsStreamAsync(ct))
            {
                await stream.CopyToAsync(file, ct);
            }

            var length = new FileInfo(part).Length;
            if (length < model.MinBytes)
            {
                throw new InvalidOperationException(
                    $"{model.Name} download is incomplete ({length:N0} of at least {model.MinBytes:N0} bytes). " +
                    "Delete the file and restart to retry.");
            }

            File.Move(part, model.Path, overwrite: true);
            logger.LogInformation("Downloaded {Name} ({Bytes:N0} bytes)", model.Name, length);
        }
        catch
        {
            if (File.Exists(part)) File.Delete(part);
            throw;
        }
    }
}
