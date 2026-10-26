using IDevelop.Desktop.Canvas;
using EngineState = IDevelop.Engine.NodeState;

namespace IDevelop.Desktop.Conversation;

/// <summary>What a node needs from the person, from its state alone. Opening the conversation does not clear it; only the state moving on does.</summary>
public abstract record Attention(string Label)
{
    /// <summary>The node waits for the person: a reply, an answer, an approval, a resolved conflict, or a choice after an interruption.</summary>
    public sealed record Waiting(string Label) : Attention(Label);

    /// <summary>The node's turn failed.</summary>
    public sealed record Problem(string Label) : Attention(Label);

    public static Attention? Of(EngineState state) => state switch
    {
        EngineState.InputRequired => new Waiting($"Waiting for you: {NodeStates.Detail(state)}"),
        EngineState.Failed failed => new Problem($"Failed: {failed.Reason}"),
        _ => null,
    };
}
