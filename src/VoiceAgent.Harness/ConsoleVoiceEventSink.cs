using VoiceAgent.Server.Contracts;

namespace VoiceAgent.Harness;

public sealed class ConsoleVoiceEventSink : IVoiceEventSink
{
    public ValueTask SendAsync(VoiceEvent evt, CancellationToken ct = default)
    {
        switch (evt)
        {
            case TurnStarted e:
                Console.WriteLine($"turn {e.TurnId}");
                break;

            case Transcript e:
                Console.WriteLine($"USER: {e.Text}");
                break;

            case AssistantDelta e:
                Console.Write(e.Text);
                break;

            case StageTiming e:
                Console.WriteLine($"  [{e.Stage} {e.Ms:F0} ms]");
                break;

            case TurnCancelled e:
                Console.WriteLine($"  (cancelled: {e.Reason})");
                break;

            case PipelineError e:
                Console.Error.WriteLine($"  error: {e.Message}");
                break;
        }

        return ValueTask.CompletedTask;
    }
}
