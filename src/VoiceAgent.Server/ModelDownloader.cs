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

    private const string SherpaBase =
        "https://huggingface.co/csukuangfj/sherpa-onnx-streaming-zipformer-en-2023-06-26/resolve/main/";

    public static ModelFile SherpaEncoder { get; } = new(
        "Streaming Zipformer encoder",
        SherpaBase + "encoder-epoch-99-avg-1-chunk-16-left-64.int8.onnx",
        ModelPaths.SherpaEncoder, ModelPaths.SherpaEncoderMinBytes);

    public static ModelFile SherpaDecoder { get; } = new(
        "Streaming Zipformer decoder",
        SherpaBase + "decoder-epoch-99-avg-1-chunk-16-left-64.int8.onnx",
        ModelPaths.SherpaDecoder, ModelPaths.SherpaDecoderMinBytes);

    public static ModelFile SherpaJoiner { get; } = new(
        "Streaming Zipformer joiner",
        SherpaBase + "joiner-epoch-99-avg-1-chunk-16-left-64.int8.onnx",
        ModelPaths.SherpaJoiner, ModelPaths.SherpaJoinerMinBytes);

    public static ModelFile SherpaTokens { get; } = new(
        "Streaming Zipformer tokens",
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
