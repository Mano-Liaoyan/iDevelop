using IDevelop.Desktop.Execution;
using EngineState = IDevelop.Engine.NodeState;

namespace IDevelop.Desktop.Canvas;

/// <summary>The engine's state of a node, which its ring, tint, glyph, and status show (ADR 0017).</summary>
public enum NodeState { Idle, Running, InputRequired, Completed, Failed, Stale }

/// <summary>What the node is to another node, drawn over its state: a running review's subject, or a planner with an open proposal.</summary>
public enum NodeRole { None, UnderReview, Proposing }

public static class NodeStates
{
    public static NodeState Of(EngineState state) => state switch
    {
        EngineState.Running => NodeState.Running,
        EngineState.InputRequired => NodeState.InputRequired,
        EngineState.Completed => NodeState.Completed,
        EngineState.Failed => NodeState.Failed,
        EngineState.Stale => NodeState.Stale,
        _ => NodeState.Idle,
    };

    /// <summary>The card's status, which names why a node needs the person, such as "Input required · approval".</summary>
    public static string Label(EngineState state) => state switch
    {
        EngineState.Running => "Running",
        EngineState.InputRequired waiting => $"Input required · {Reason(waiting.Why)}",
        EngineState.Completed => "Completed",
        EngineState.Failed => "Failed",
        EngineState.Stale => "Stale",
        _ => "Idle",
    };

    /// <summary>
    /// The status of a running node whose client waits for <paramref name="background"/> work, such as "Waiting for a
    /// background subagent" (ADR 0030), and otherwise the state's own.
    /// </summary>
    public static string Label(EngineState state, System.Collections.Immutable.ImmutableArray<IDevelop.Clients.BackgroundTask> background) =>
        state is not EngineState.Running || background.IsDefaultOrEmpty ? Label(state)
        : background.Any(task => task.IsSubagent) ? "Waiting for a background subagent"
        : "Waiting for background work";

    /// <summary>What the node needs from the person, or why it stopped, in a sentence, or null when its status says enough.</summary>
    public static string? Detail(EngineState state) => state switch
    {
        EngineState.InputRequired { Why: EngineState.Approval } => "Approve what the nodes before it handed on, or send it back with a note.",
        EngineState.InputRequired { Why: EngineState.Reply } => "The agent replied. Write back, or approve its reply as the result.",
        EngineState.InputRequired { Why: EngineState.Conflict } =>
            "The work handed on conflicts. Resolve the conflict in the node's worktree, then mark it resolved.",
        EngineState.InputRequired { Why: EngineState.Interrupted } => "iDevelop closed during its turn. Continue the session, or retry.",
        EngineState.InputRequired { Why: EngineState.RoundLimit } =>
            "The review reached its round limit. Approve the work, send it back with a note, or edit the workflow.",
        EngineState.InputRequired waiting => waiting.Why,
        EngineState.Failed failed => failed.Reason,
        EngineState.Stale => "What it ran on changed: its inputs, its instruction, or its model. Run it again for a current result.",
        _ => null,
    };

    public static StatusTone Tone(NodeState state) => state switch
    {
        NodeState.Running => StatusTone.Running,
        NodeState.Completed => StatusTone.Complete,
        NodeState.Failed => StatusTone.Problem,
        NodeState.InputRequired => StatusTone.Waiting,
        NodeState.Stale => StatusTone.Warning,
        _ => StatusTone.Neutral,
    };

    private static string Reason(string why) => why switch
    {
        EngineState.Approval => "approval",
        EngineState.Reply => "reply",
        EngineState.Conflict => "conflict",
        EngineState.Interrupted => "interrupted",
        EngineState.RoundLimit => "round limit",
        _ => "question",
    };
}
