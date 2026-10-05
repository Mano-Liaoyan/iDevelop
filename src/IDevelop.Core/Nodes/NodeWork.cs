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
}

/// <summary>What a node does next.</summary>
public abstract record NodeStep
{
    private NodeStep() { }

    /// <summary>Start a client turn with this prompt.</summary>
    public sealed record RunTurn(string Prompt) : NodeStep;

    public sealed record WaitForPerson(Pending Pending) : NodeStep;

    /// <summary>The node is done. <paramref name="Report"/> is what it hands on.</summary>
    public sealed record Finish(string? Report) : NodeStep;

    public sealed record Fail(string Reason) : NodeStep;
}

/// <summary>A node and what earlier nodes handed on to it.</summary>
public sealed record NodeContext(TaskDefinition Node, string Inputs);

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
    /// <summary>The prompt of the turn that carries the person's message.</summary>
    string Reply(string message);
}

public static class NodeWorks
{
    public static INodeWork For(WorkSpec work) => work.Kind switch
    {
        WorkKind.Agent => AgentWork.Instance,
    };
}
