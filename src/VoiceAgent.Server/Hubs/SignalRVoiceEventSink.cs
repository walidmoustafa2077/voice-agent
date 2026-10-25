using Microsoft.AspNetCore.SignalR;
using VoiceAgent.Server.Contracts;
using VoiceAgent.Server.Hubs;

namespace VoiceAgent.Server.Hubs;

public sealed class SignalRVoiceEventSink(IHubContext<VoiceHub> hub, string connectionId)
    : IVoiceEventSink
{
    public ValueTask SendAsync(VoiceEvent evt, CancellationToken ct = default)
    {
        var client = hub.Clients.Client(connectionId);
        var task = evt switch
        {
            StateChanged e => client.SendAsync("stateChanged",
                e.State.ToString().ToLowerInvariant(), e.VadThreshold, ct),
            TurnStarted e => client.SendAsync("turnStarted", e.TurnId, e.At.ToUnixTimeMilliseconds(), ct),
            Transcript e => client.SendAsync("transcript", e.TurnId, e.Text, ct),
            AssistantDelta e => client.SendAsync("assistantDelta", e.TurnId, e.Text, ct),
            AudioChunk e => client.SendAsync("audioChunk", e.TurnId, e.Index, e.Wav, e.DurationMs, ct),
            TurnCancelled e => client.SendAsync("turnCancelled",
                e.TurnId, e.Reason, e.LastPlayedIndex, e.HeardText, e.RemoveLastUser, ct),
            StageTiming e => client.SendAsync("stageTiming", e.Stage, e.Ms, ct),
            PipelineError e => client.SendAsync("error", e.Message, ct),
            _ => Task.CompletedTask,
        };

        return new ValueTask(task);
    }
}
