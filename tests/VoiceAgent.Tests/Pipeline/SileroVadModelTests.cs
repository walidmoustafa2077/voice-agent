using VoiceAgent.Server.Pipeline;
using Xunit;

namespace VoiceAgent.Tests.Pipeline;

public class SileroVadModelTests
{
    [Fact]
    public void Speech_scores_higher_than_silence()
    {
        var modelPath = Path.Combine(RepoRoot(), "models", "silero_vad.onnx");
        if (!File.Exists(modelPath)) return;   // model downloads on first run

        using var model = new SileroVadModel(modelPath);

        var speech = Wav.ReadPcm16(Wav.Fixture("speech.wav"));
        var silence = new short[16000];

        var speechScore = model.MaxProbability(speech);
        model.Reset();
        var silenceScore = model.MaxProbability(silence);

        Assert.True(speechScore > 0.8f, $"speech scored {speechScore}");
        Assert.True(silenceScore < 0.2f, $"silence scored {silenceScore}");
    }

    [Fact]
    public void Prepend_context_is_exactly_64_samples()
    {
        Assert.Equal(64, SileroVadModel.ContextSamples);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VoiceAgent.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
