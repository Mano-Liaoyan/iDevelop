using System.Collections.Immutable;
using IDevelop.Clients;
using IDevelop.Desktop.Execution;
using EngineState = IDevelop.Engine.NodeState;

namespace IDevelop.Desktop.Canvas;

/// <summary>The engine's state of a node, which its ring, tint, glyph, and status show (ADR 0017).</summary>
public enum NodeState { Idle, Running, InputRequired, Completed, Failed, Stale }

/// <summary>What the node is to another node, drawn over its state: a running review's subject, or a planner with an open proposal.</summary>
public enum NodeRole { None, UnderReview, Proposing }

/// <summary>What a node that needs the person needs, which its glyph tells apart. None for every other state.</summary>
public enum WaitReason { None, Question, Reply, Approval, Conflict, Interrupted, RoundLimit }

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

    /// <summary>What <paramref name="state"/> waits for.</summary>
    public static WaitReason Why(EngineState state) => state switch
    {
        EngineState.InputRequired { Why: EngineState.Approval } => WaitReason.Approval,
        EngineState.InputRequired { Why: EngineState.Reply } => WaitReason.Reply,
        EngineState.InputRequired { Why: EngineState.Conflict } => WaitReason.Conflict,
        EngineState.InputRequired { Why: EngineState.Interrupted } => WaitReason.Interrupted,
        EngineState.InputRequired { Why: EngineState.RoundLimit } => WaitReason.RoundLimit,
        EngineState.InputRequired => WaitReason.Question,
        _ => WaitReason.None,
    };

    /// <summary>The node's status, in the words of the left tree's groups (ADR 0068), which names why it needs the person, such as "Input required · Approval". An interrupted node needs nothing yet, so it says only that.</summary>
    public static string Label(EngineState state) => state switch
    {
        EngineState.Running => "Running",
        EngineState.InputRequired { Why: EngineState.Interrupted } => "Interrupted",
        EngineState.InputRequired => $"Input required · {Need(Why(state))}",
        EngineState.Completed => "Done",
        EngineState.Failed => "Failed",
        EngineState.Stale => "Stale",
        _ => "Not started",
    };

    /// <summary>What a waiting node needs, in the one word that a zoomed out card has room for.</summary>
    public static string Need(WaitReason why) => why switch
    {
        WaitReason.Reply => "Reply",
        WaitReason.Approval => "Approval",
        WaitReason.Conflict => "Conflict",
        WaitReason.Interrupted => "Interrupted",
        WaitReason.RoundLimit => "Round limit",
        _ => "Question",
    };

    /// <summary>
    /// The status of a running node whose client waits for <paramref name="background"/> work, such as "Waiting for a
    /// background subagent" (ADR 0030), and otherwise the state's own.
    /// </summary>
    public static string Label(EngineState state, ImmutableArray<BackgroundTask> background) =>
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
        EngineState.InputRequired { Why: EngineState.Interrupted } => "Closed mid-turn. Continue resumes the session. Retry starts a new attempt.",
        EngineState.InputRequired { Why: EngineState.RoundLimit } =>
            "The review reached its round limit. Approve the work, send it back with a note, or edit the workflow.",
        EngineState.InputRequired waiting => waiting.Why,
        EngineState.Failed failed => failed.Reason,
        EngineState.Stale => "What it ran on changed: its inputs, its instruction, or its model. Run it again for a current result.",
        _ => null,
    };

    /// <summary>
    /// The interrupted state's sentence where Retry is in the More menu, as in the inspector when Continue and Conversation take the header's row.
    /// </summary>
    public const string InterruptedDetailInMenu = "Closed mid-turn. Continue resumes the session. Retry, in the More menu, starts a new attempt.";

    /// <summary>The state's tone. An interrupted node needs nothing yet, so it takes the neutral tone, as its badge's gray disc does.</summary>
    public static StatusTone Tone(NodeState state, WaitReason why) =>
        state == NodeState.InputRequired && why == WaitReason.Interrupted ? StatusTone.Neutral : Tone(state);

    public static StatusTone Tone(NodeState state) => state switch
    {
        NodeState.Running => StatusTone.Running,
        NodeState.Completed => StatusTone.Complete,
        NodeState.Failed => StatusTone.Problem,
        NodeState.InputRequired => StatusTone.Waiting,
        NodeState.Stale => StatusTone.Warning,
        _ => StatusTone.Neutral,
    };
}
