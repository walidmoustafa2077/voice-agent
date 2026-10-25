namespace VoiceAgent.Server;

public static class ModelPaths
{
    public static string Root { get; } = FindRoot();

    public static string WhisperTinyEn => Path.Combine(Root, "ggml-tiny.en.bin");

    public static string SileroVad => Path.Combine(Root, "silero_vad.onnx");

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
