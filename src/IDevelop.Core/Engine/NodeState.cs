using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Engine;

/// <summary>
/// What a node shows, in A2A's task states plus Idle and Stale. The engine derives it from the node's log, whether a turn
/// runs now, and the node's current basis, so nothing about it is stored.
/// </summary>
public abstract record NodeState
{
    /// <summary>Why a turn that was running when iDevelop closed waits: the person continues the session or retries.</summary>
    public const string Interrupted = "interrupted";

    /// <summary>Why an Approval node waits: the person approves or sends back.</summary>
    public const string Approval = "approval";

    /// <summary>Why a Chat node waits after each turn: the person replies, or approves the last reply as the result.</summary>
    public const string Reply = "reply";

    private NodeState() { }

    /// <summary>The node has no result and nothing runs.</summary>
    public sealed record Idle : NodeState;

    public sealed record Running : NodeState;

    /// <summary>
    /// The node waits for the person. <paramref name="Why"/> is <see cref="Interrupted"/>, <see cref="Approval"/>,
    /// <see cref="Reply"/>, the agent's question, or why iDevelop could not read the agent's result block.
    /// </summary>
    public sealed record InputRequired(string Why) : NodeState;

    /// <summary>The node's latest result is current.</summary>
    public sealed record Completed : NodeState;

    public sealed record Failed(string Reason) : NodeState;

    /// <summary>The node's latest result, or the turn the engine stopped, rests on a basis that is no longer current.</summary>
    public sealed record Stale : NodeState;
}

/// <summary>The client, model, and effort a node's agent runs with.</summary>
public sealed record AgentModel(ClientId Client, string? Model, string? Effort);

/// <summary>
/// Everything a node's output depends on: the ids of its inputs (the latest results of its dependency predecessors, in
/// predecessor order), its instruction (the rendered template and fields), and its model. A result is stale when the basis
/// it recorded differs from the current one.
/// </summary>
public sealed record Basis(ImmutableArray<string> Inputs, string Instruction, AgentModel? Model)
{
    // ImmutableArray compares by reference, so equality compares the ids themselves.
    public bool Equals(Basis? other) =>
        other is not null && Inputs.SequenceEqual(other.Inputs) && Instruction == other.Instruction && Model == other.Model;

    public override int GetHashCode() => HashCode.Combine(Inputs.Length, Instruction, Model);
}

/// <summary>What a node hands on: a report and, once nodes work in Git, a commit, on the basis it was made from.</summary>
public sealed record NodeResult(string Id, string Report, string? Commit, Basis Basis);
