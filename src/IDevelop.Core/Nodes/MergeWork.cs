using IDevelop.Execution;
using IDevelop.Workflows;

namespace IDevelop.Nodes;

/// <summary>
/// A Merge node's agent, which runs only to resolve a merge conflict of a run. Each conflict starts its own session in a
/// folder that holds the conflicting merge, and iDevelop commits the merge once the agent ends its turn.
/// </summary>
public sealed class MergeWork : INodeWork
{
    public static readonly MergeWork Instance = new();

    private MergeWork() { }

    public WorkKind Kind => WorkKind.Merge;

    public NodeStep Next(NodeContext context, AttemptRecord? latest) => latest switch
    {
        null => new NodeStep.RunTurn(Prompt(context)),
        { Status: AttemptStatus.Running } => throw new ArgumentException("A turn of this attempt is running.", nameof(latest)),
        { Status: AttemptStatus.Succeeded } => new NodeStep.Finish(latest.Result),
        _ => new NodeStep.Fail(latest.Detail ?? $"The attempt ended {latest.Status}."),
    };

    /// <summary>The rendered template, with the conflict the agent resolves.</summary>
    internal static string Prompt(NodeContext context)
    {
        var node = context.Node;
        return ((WorkSpec.Merge)node.Blueprint.Work).Template.Render(name => name switch
        {
            "title" => node.Title,
            "inputs" => context.Inputs,
            "conflict" => context.Conflict ?? "",
            _ => node.Field(name),
        });
    }
}
