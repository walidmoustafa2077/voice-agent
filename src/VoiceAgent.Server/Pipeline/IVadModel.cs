namespace VoiceAgent.Server.Pipeline;

/// <summary>Speech probability for one 512-sample window.</summary>
public interface IVadModel
{
    float Predict(ReadOnlySpan<short> window512);
    void Reset();
}
