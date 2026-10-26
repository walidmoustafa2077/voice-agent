namespace VoiceAgent.Server;

public static class ModelPaths
{
    public static string Root { get; } = FindRoot();

    public static string WhisperTinyEn => Path.Combine(Root, "ggml-tiny.en.bin");

    /// <summary>Optional second Whisper size for the offline accuracy comparison; never downloaded.</summary>
    public static string WhisperBaseEn => Path.Combine(Root, "ggml-base.en.bin");

    /// <summary>True when <see cref="WhisperBaseEn"/> is present and plausibly complete (~142 MB).</summary>
    public static bool WhisperBaseEnReady => Ready(WhisperBaseEn, 100_000_000);

    public static string SileroVad => Path.Combine(Root, "silero_vad.onnx");

    public static string Kokoro => Path.Combine(Root, "kokoro.onnx");

    /// <summary>Streaming Zipformer transducer (int8, chunk-16, left-64).</summary>
    public static string SherpaDir => Path.Combine(Root, "sherpa");

    public static string SherpaEncoder =>
        Path.Combine(SherpaDir, "encoder-epoch-99-avg-1-chunk-16-left-64.int8.onnx");

    public static string SherpaDecoder =>
        Path.Combine(SherpaDir, "decoder-epoch-99-avg-1-chunk-16-left-64.int8.onnx");

    public static string SherpaJoiner =>
        Path.Combine(SherpaDir, "joiner-epoch-99-avg-1-chunk-16-left-64.int8.onnx");

    public static string SherpaTokens => Path.Combine(SherpaDir, "tokens.txt");

    public const long SherpaEncoderMinBytes = 70_000_000;
    public const long SherpaDecoderMinBytes = 1_000_000;
    public const long SherpaJoinerMinBytes = 200_000;
    public const long SherpaTokensMinBytes = 4_000;

    public static bool SherpaReady =>
        Ready(SherpaEncoder, SherpaEncoderMinBytes) &&
        Ready(SherpaDecoder, SherpaDecoderMinBytes) &&
        Ready(SherpaJoiner, SherpaJoinerMinBytes) &&
        Ready(SherpaTokens, SherpaTokensMinBytes);

    private static bool Ready(string path, long minBytes) =>
        File.Exists(path) && new FileInfo(path).Length >= minBytes;

    /// <summary>Minimum size that can be the real ~320 MB Kokoro download.</summary>
    public const long KokoroMinBytes = 300L * 1024 * 1024;

    public static bool KokoroReady => Ready(Kokoro, KokoroMinBytes);

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VoiceAgent.slnx")))
                return Path.Combine(dir.FullName, "models");
            dir = dir.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "models");
    }
}
