using System.Windows.Input;
using IDevelop.Desktop.Canvas;

namespace IDevelop.Desktop.Inspector;

/// <summary>One button of the pinned header's action row. The first in a row is the node's next step, and only it can be primary.</summary>
/// <param name="Id">The button's automation id.</param>
/// <param name="Label">What the button says, which also names it for UI Automation.</param>
/// <param name="Glyph">The resource key of the button's glyph.</param>
/// <param name="Command">What the button does.</param>
/// <param name="IsPrimary">The button is the row's filled one.</param>
/// <param name="Tip">The tooltip, which holds the reason on a button that is off.</param>
public sealed record NodeAction(string Id, string Label, string Glyph, ICommand Command, bool IsPrimary, string? Tip);

/// <summary>Which actions the pinned header offers a node in each state, in order: the likeliest next step first.</summary>
internal static class NodeActions
{
    /// <summary>While a proposal is open, accepting it is the one primary action, so the row holds none.</summary>
    public static IReadOnlyList<NodeAction> Of(TaskNodeViewModel node) =>
        node.Proposal is null ? For(node) : [.. For(node).Select(action => action with { IsPrimary = false })];

    private static IReadOnlyList<NodeAction> For(TaskNodeViewModel node)
    {
        if (node.ShowsSendBack)
        {
            return [Approve(node, primary: true), SendBack(node)];
        }

        if (node.ShowsMarkResolved)
        {
            return
            [
                new("MarkResolved", "Mark resolved", "IconCheckmark", node.MarkResolvedCommand, true, null),
                new("OpenConflict", "Open worktree", "IconFolder", node.OpenWorktreeCommand, false, null),
            ];
        }

        if (node.ShowsContinue)
        {
            return [new("ContinueTask", "Continue", "IconRun", node.ContinueCommand, true, null), Retry(node, primary: false)];
        }

        if (node.IsAsking || node.IsWaiting)
        {
            return Row(Conversation(node, "Reply"), node.ShowsRetry ? Retry(node, primary: false) : null,
                node.ShowsStop ? Stop(node) : null, node.ShowsApprove ? Approve(node, primary: false) : null);
        }

        return node.State switch
        {
            NodeState.Idle => Row(new NodeAction("RunTask", "Run", "IconRun", node.RunCommand, true, node.RunRefusal), Conversation(node, "Conversation", primary: false)),
            NodeState.Running => Row(Conversation(node, "Conversation"), Stop(node)),
            NodeState.Failed or NodeState.Stale => Row(Retry(node, primary: true), Conversation(node, "Conversation", primary: false)),
            _ => Row(Conversation(node, "Conversation"), node.ShowsRetry ? Retry(node, primary: !node.HasAgent) : null),
        };
    }

    private static NodeAction[] Row(params NodeAction?[] actions) => [.. actions.OfType<NodeAction>()];

    // An approval node has no agent to talk to, so it takes no conversation button.
    private static NodeAction? Conversation(TaskNodeViewModel node, string label, bool primary = true) => node.HasAgent
        ? new NodeAction("OpenConversation", label, "IconChat", node.OpenConversationCommand, primary, node.AttentionLabel ?? "Open the full conversation")
        : null;

    private static NodeAction Approve(TaskNodeViewModel node, bool primary) =>
        new("Approve", "Approve", "IconCheckmark", node.ApproveCommand, primary, null);

    private static NodeAction SendBack(TaskNodeViewModel node) =>
        new("SendBack", "Send back", "IconUndo", node.SendBackCommand, false, "Write a note first. It goes to the nodes before this one.");

    private static NodeAction Retry(TaskNodeViewModel node, bool primary) =>
        new("RetryTask", node.State == NodeState.Completed ? "Run again" : "Retry", "IconRefresh", node.RetryCommand, primary, node.RunRefusal ?? "Runs it again in a fresh session");

    private static NodeAction Stop(TaskNodeViewModel node) => new("StopTask", "Stop", "IconStop", node.StopCommand, false, null);
}
