using System.Windows.Input;
using IDevelop.Desktop.Canvas;

namespace IDevelop.Desktop.Inspector;

/// <summary>The panel header's one button.</summary>
/// <param name="Id">The button's automation id.</param>
/// <param name="Label">What the button says, which also names it for UI Automation.</param>
/// <param name="Glyph">The resource key of the button's glyph.</param>
/// <param name="Command">What the button does.</param>
/// <param name="IsPrimary">The button is the row's filled one.</param>
/// <param name="Tip">The tooltip, which holds the reason on a button that is off.</param>
public sealed record NodeAction(string Id, string Label, string Glyph, ICommand Command, bool IsPrimary, string? Tip);

/// <summary>The header's slot: the node's one action. While the node needs a person, failed, or is stale, the Action Dock holds it instead, so no action appears twice.</summary>
internal static class NodeActions
{
    /// <summary>Run, Retry, or Stop. It is empty while the Action Dock owns the action.</summary>
    public static IReadOnlyList<NodeAction> HeaderOf(TaskNodeViewModel node)
    {
        if (node.IsAsking || node.IsWaiting || node.State is NodeState.Failed or NodeState.Stale)
        {
            return [];
        }

        return node.State switch
        {
            NodeState.Idle => [new NodeAction("RunTask", "Run", "IconRun", node.RunCommand, node.Proposal is null, node.RunRefusal ?? "Runs this node, then each node after it")],
            NodeState.Running => [new NodeAction("StopTask", "Stop", "IconStop", node.StopCommand, false, null)],
            _ => node.ShowsRetry
                ? [new NodeAction("RetryTask", node.RetryLabel, "IconRefresh", node.RetryCommand, node.Proposal is null && !node.HasAgent,
                    node.RunRefusal ?? "Starts a new attempt in a fresh session, then runs each node after it")]
                : [],
        };
    }
}
