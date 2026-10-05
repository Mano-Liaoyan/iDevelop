using IDevelop.Execution;
using IDevelop.Workflows;

namespace IDevelop.Nodes;

/// <summary>Why a node waits for the person. The inspector has one section for each case.</summary>
public abstract record Pending
{
    private Pending() { }

    /// <summary>The agent ended its turn with this question.</summary>
    public sealed record Question(string Text) : Pending;

    /// <summary>A Chat node waits after every turn for the person's reply.</summary>
    public sealed record Reply : Pending;

    /// <summary>The agent ended its turn with a result block iDevelop could not read, so the person decides.</summary>
    public sealed record UnreadableBlock(string Problem) : Pending;

    /// <summary>An approval waits for the person to approve or send back.</summary>
    public sealed record Approval : Pending;
}

/// <summary>What a node does next.</summary>
public abstract record NodeStep
{
    private NodeStep() { }

    /// <summary>Start a client turn with this prompt. A review's turn after a fix round carries what the subject reported.</summary>
    public sealed record RunTurn(string Prompt) : NodeStep
    {
        public FixReport? Report { get; init; }
    }

    /// <summary>
    /// Start a new attempt of the review's subject with this prompt, as fix round <paramref name="Round"/>. It resumes the
    /// subject's latest session when <paramref name="Resumes"/> is true, and otherwise starts a fresh one. The prompt
    /// carries the review's guidance notes up to <paramref name="Guidance"/>.
    /// </summary>
    public sealed record FixRound(string Prompt, int Round, int Guidance, bool Resumes) : NodeStep;

    /// <summary>Nothing to do until the review's subject settles its fix round.</summary>
    public sealed record WaitForSubject : NodeStep;

    public sealed record WaitForPerson(Pending Pending) : NodeStep;

    /// <summary>The node is done. <paramref name="Report"/> is what it hands on.</summary>
    public sealed record Finish(string? Report) : NodeStep;

    public sealed record Fail(string Reason) : NodeStep;
}

/// <summary>A node and what earlier nodes handed on to it.</summary>
public sealed record NodeContext(TaskDefinition Node, string Inputs)
{
    /// <summary>What a node whose agent proposes may fill and place. Null reads as <see cref="PlanningContext.None"/>.</summary>
    public PlanningContext? Planning { get; init; }

    /// <summary>A review's subject, as the review reads it. Null for other nodes and for a review without one.</summary>
    public SubjectView? Subject { get; init; }
}

/// <summary>
/// The node a review reviews, its latest attempt, and its changes as diffs, which the caller reads from Git so that
/// <see cref="INodeWork.Next"/> stays pure.
/// </summary>
public sealed record SubjectView(TaskDefinition Node, AttemptRecord? Latest)
{
    /// <summary>The subject's whole change, from the first turn of its latest conversation to the latest attempt's end.</summary>
    public string? Change { get; init; }

    /// <summary>The latest attempt's own change, which a fix round made.</summary>
    public string? LatestChange { get; init; }

    /// <summary>A fix round can resume the latest attempt's session: it has one, and the subject still uses that client.</summary>
    public bool CanResume { get; init; }
}

/// <summary>
/// The base node interface: what one <see cref="WorkKind"/> does. <see cref="Next"/> is a pure decider. It starts no
/// process and touches no file, so a standalone run and a workflow scheduler call it the same way, and the same history
/// gives the same step.
/// </summary>
public interface INodeWork
{
    WorkKind Kind { get; }

    /// <param name="latest">The node's latest attempt, or null for a fresh start. A turn of it must not be running.</param>
    NodeStep Next(NodeContext context, AttemptRecord? latest);
}

/// <summary>The interaction capability: a work whose node the person can write to.</summary>
public interface IConverses : INodeWork
{
    MessageUse Receive(string message);
}

/// <summary>What a work does with the person's message.</summary>
public abstract record MessageUse
{
    private MessageUse() { }

    /// <summary>The agent's session continues with a turn of this prompt.</summary>
    public sealed record Turn(string Prompt) : MessageUse;

    /// <summary>A review adds the message to its ledger, and both agents read it in their next message.</summary>
    public sealed record Guidance(string Text) : MessageUse;
}

public static class NodeWorks
{
    public static INodeWork For(WorkSpec work) => work.Kind switch
    {
        WorkKind.Agent => AgentWork.Instance,
        WorkKind.Review => ReviewWork.Instance,
        WorkKind.Person => PersonWork.Instance,
    };
}
