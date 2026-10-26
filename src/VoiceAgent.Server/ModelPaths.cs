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

    /// <summary>Root for every streaming recognizer; the benchmark enumerates its subdirectories.</summary>
    public static string SherpaDir => Path.Combine(Root, "sherpa");

    /// <summary>The default streaming recognizer: Kroko's 2025-08 English Zipformer.</summary>
    public static string SherpaModelDir => Path.Combine(SherpaDir, "kroko-2025-08");

    public static string SherpaEncoder => Path.Combine(SherpaModelDir, "encoder.onnx");

    public static string SherpaDecoder => Path.Combine(SherpaModelDir, "decoder.onnx");

    public static string SherpaJoiner => Path.Combine(SherpaModelDir, "joiner.onnx");

    public static string SherpaTokens => Path.Combine(SherpaModelDir, "tokens.txt");

    public const long SherpaEncoderMinBytes = 60_000_000;
    public const long SherpaDecoderMinBytes = 600_000;
    public const long SherpaJoinerMinBytes = 300_000;
    public const long SherpaTokensMinBytes = 5_000;

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
