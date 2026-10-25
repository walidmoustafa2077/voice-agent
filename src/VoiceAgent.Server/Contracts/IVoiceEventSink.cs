namespace VoiceAgent.Server.Contracts;

/// <summary>Transport seam: the SignalR hub and the console harness both implement this.</summary>
public interface IVoiceEventSink
{
    ValueTask SendAsync(VoiceEvent evt, CancellationToken ct = default);
}
