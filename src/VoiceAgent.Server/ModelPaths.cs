namespace VoiceAgent.Server;

public static class ModelPaths
{
    public static string Root { get; } = FindRoot();

    public static string WhisperTinyEn => Path.Combine(Root, "ggml-tiny.en.bin");

    public static string SileroVad => Path.Combine(Root, "silero_vad.onnx");

    public static string Kokoro => Path.Combine(Root, "kokoro.onnx");

    /// <summary>True only when the model file is large enough to be the real 320 MB download.</summary>
    public static bool KokoroReady =>
        File.Exists(Kokoro) && new FileInfo(Kokoro).Length >= 300L * 1024 * 1024;

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
