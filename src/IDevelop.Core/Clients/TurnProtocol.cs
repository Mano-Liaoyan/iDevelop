using System.Collections.Immutable;
using System.Text.Json;

namespace IDevelop.Clients;

internal sealed record ProtocolOutput(ImmutableArray<AgentEvent> Events, ImmutableArray<string> Writes, bool CloseInput)
{
    public bool IsOneShotPrompt { get; init; }

    public static ProtocolOutput Empty { get; } = new([], [], false);
}

internal sealed class ProtocolException(string message) : Exception(message);

internal abstract class TurnProtocol
{
    public abstract ProtocolOutput Start();

    public abstract ProtocolOutput Read(string line);

    public abstract ProtocolOutput Answer(string requestId, QuestionsReply reply);

    public abstract ProtocolOutput Decline(string requestId);

    public abstract ProtocolOutput? Interrupt();

    protected static string Frame(object value) => JsonSerializer.Serialize(value);

    protected static string RequestId(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.String => "s:" + id.GetString(),
        JsonValueKind.Number => "n:" + id.GetRawText(),
        _ => throw new ProtocolException("The client supplied an invalid request id."),
    };
}

internal sealed class OneShotProtocol(LaunchArguments launch, Func<string, ImmutableArray<AgentEvent>> interpret) : TurnProtocol
{
    private readonly Dictionary<string, string> _steps = [];
    private int _ordinal;
    private string _messageId = "local:0";

    public override ProtocolOutput Start() => new([], [launch.Stdin], true) { IsOneShotPrompt = true };

    public override ProtocolOutput Read(string line)
    {
        using var json = JsonDocument.Parse(line);
        var root = json.RootElement;
        if (root.String("type") == "message_start" && root.Property("message")?.String("role") == "assistant")
        {
            _messageId = "local:" + _ordinal++;
        }

        if (root.String("type") == "message_update" && root.Property("assistantMessageEvent") is { } update
            && update.String("type") == "text_delta" && update.String("delta") is { } delta)
        {
            return new([new AgentEvent.MessageDelta(_messageId, delta)], [], false);
        }

        if (root.String("event") == "step_update" && root.Property("step_update") is { } step && step.String("step_type") == "agent_response")
        {
            var id = step.Property("step_index")?.GetRawText() is { } index ? "step:" + index : _messageId;
            if (step.String("text_delta") is { } text)
            {
                _steps[id] = _steps.GetValueOrDefault(id, "") + text;
                if (step.String("state") != "DONE")
                {
                    return new([new AgentEvent.MessageDelta(id, text)], [], false);
                }
            }

            if (step.String("state") == "DONE" && _steps.Remove(id, out var complete))
            {
                return new([new AgentEvent.Message(complete.TrimEnd()) { Id = id }], [], false);
            }

            return ProtocolOutput.Empty;
        }

        var events = interpret(line).Select(e => e is AgentEvent.Message message ? message with { Id = _messageId } : e).ToImmutableArray();
        return new(events, [], false);
    }

    public override ProtocolOutput Answer(string requestId, QuestionsReply reply) => throw new ProtocolException("This client has no answer channel.");

    public override ProtocolOutput Decline(string requestId) => throw new ProtocolException("This client has no permission channel.");

    public override ProtocolOutput? Interrupt() => null;
}
