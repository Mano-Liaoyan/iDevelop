using System.Collections.Immutable;
using System.Text.Json.Serialization;
using IDevelop.Clients;
using IDevelop.Nodes;
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

    /// <summary>
    /// Why a node waits when its predecessors' commits conflict and no Merge node takes the conflict: the person resolves
    /// it in the node's worktree and marks it resolved.
    /// </summary>
    public const string Conflict = "conflict";

    /// <summary>
    /// Why a reviewer waits once its round limit is reached: the person approves its subject's result, sends back a note, or
    /// edits the workflow.
    /// </summary>
    public const string RoundLimit = "roundLimit";

    private NodeState() { }

    /// <summary>The node has no result and nothing runs.</summary>
    public sealed record Idle : NodeState;

    public sealed record Running : NodeState;

    /// <summary>
    /// The node waits for the person. <paramref name="Why"/> is <see cref="Interrupted"/>, <see cref="Approval"/>,
    /// <see cref="Reply"/>, <see cref="Conflict"/>, <see cref="RoundLimit"/>, the agent's question, or why iDevelop could not read the
    /// agent's result block or verdict.
    /// </summary>
    public sealed record InputRequired(string Why) : NodeState
    {
        /// <summary>The question the running turn waits on, which <see cref="WorkflowEngine.Answer"/> answers, or null when no turn runs.</summary>
        public AgentQuestion? Question { get; init; }
    }

    /// <summary>The node's latest result is current.</summary>
    public sealed record Completed : NodeState;

    public sealed record Failed(string Reason) : NodeState;

    /// <summary>The node's latest result, or the turn that was stopped, rests on a basis that is no longer current.</summary>
    public sealed record Stale : NodeState;
}

/// <summary>
/// What a running turn's agent asks the person on its client's own channel, each question with its choices. The engine
/// owns <paramref name="Key"/>, which an answer names.
/// </summary>
public sealed record AgentQuestion(string Key, ImmutableArray<AskedQuestion> Questions)
{
    public string Text => string.Join("\n\n", Questions.Select(question => question.Text));
}

/// <summary>The client, model, and effort a node's agent runs with.</summary>
public sealed record AgentModel(ClientId Client, string? Model, string? Effort);

/// <summary>A dependency predecessor and the id of its latest result, or null while it has none.</summary>
public sealed record BasisInput(TaskId Node, string? Result);

/// <summary>
/// Everything a node's output depends on: its inputs (each dependency predecessor with its latest result, in predecessor
/// order), its instruction (the rendered template and fields), and its model. A result is stale when the basis it recorded
/// differs from the current one.
/// </summary>
public sealed record Basis(ImmutableArray<BasisInput> Inputs, string Instruction, AgentModel? Model)
{
    // ImmutableArray compares by reference, so equality compares the inputs themselves.
    public bool Equals(Basis? other) =>
        other is not null && Inputs.SequenceEqual(other.Inputs) && Instruction == other.Instruction && Model == other.Model;

    public override int GetHashCode() => HashCode.Combine(Inputs.Length, Instruction, Model);
}

/// <summary>
/// What a node hands on: a report and, for a node that edits the project, the commit of its worktree, on the basis it was
/// made from.
/// </summary>
public sealed record NodeResult(string Id, string Report, string? Commit, Basis Basis)
{
    /// <summary>The verdict a review's report ends with, or null for a report without one.</summary>
    [JsonIgnore]
    public Verdict? Verdict => Verdict.Read(Report).Verdict;
}
