using IDevelop.Clients;
using IDevelop.Workflows;

namespace IDevelop.Engine;

/// <summary>How a turn ended.</summary>
internal abstract record TurnOutcome
{
    private TurnOutcome() { }

    /// <summary>The node is done, and <paramref name="Report"/> is what it hands on.</summary>
    public sealed record Done(string Report) : TurnOutcome;

    /// <summary>The node waits for the person. <paramref name="Text"/> is the agent's last reply, if any.</summary>
    public sealed record Waits(string Why, string? Text) : TurnOutcome;

    public sealed record Failed(string Reason) : TurnOutcome;

    public sealed record Stopped : TurnOutcome;
}

/// <summary>A node's agent: it runs one turn at a time, and the engine keeps everything else.</summary>
internal interface IAgent
{
    /// <summary>Why <paramref name="node"/> cannot start a fresh turn now, or null.</summary>
    string? Problem(TaskDefinition node);

    /// <summary>
    /// Runs one turn in <paramref name="folder"/> with <paramref name="prompt"/>, continuing <paramref name="session"/> when it
    /// is set. It reports what the agent says as it goes, and <paramref name="stop"/> stops the turn.
    /// </summary>
    Task<TurnOutcome> Turn(TaskDefinition node, string folder, string prompt, string? session, Action<AgentEvent> report, CancellationToken stop);
}

/// <summary>An Approval node's agent is the person: each turn waits for them to approve or send back.</summary>
internal sealed class PersonAgent : IAgent
{
    public static readonly PersonAgent Instance = new();

    private PersonAgent() { }

    public string? Problem(TaskDefinition node) => null;

    public Task<TurnOutcome> Turn(TaskDefinition node, string folder, string prompt, string? session, Action<AgentEvent> report, CancellationToken stop) =>
        Task.FromResult<TurnOutcome>(new TurnOutcome.Waits(NodeState.Approval, prompt));
}
