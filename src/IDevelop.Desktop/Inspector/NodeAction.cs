using System.Windows.Input;
using IDevelop.Desktop.Canvas;

namespace IDevelop.Desktop.Inspector;

/// <summary>One button of the pinned header's action row. Only the node's next step can be primary.</summary>
/// <param name="Id">The button's automation id.</param>
/// <param name="Label">What the button says, which also names it for UI Automation.</param>
/// <param name="Glyph">The resource key of the button's glyph.</param>
/// <param name="Command">What the button does.</param>
/// <param name="IsPrimary">The button is the row's filled one.</param>
/// <param name="Tip">The tooltip, which holds the reason on a button that is off.</param>
public sealed record NodeAction(string Id, string Label, string Glyph, ICommand Command, bool IsPrimary, string? Tip);

/// <summary>Which actions the pinned header offers a node in each state. Conversation comes first in every state, so it never moves under the pointer, and it goes while the conversation is open.</summary>
internal static class NodeActions
{
    /// <summary>While a proposal is open, accepting it is the one primary action, so the row holds none.</summary>
    public static IReadOnlyList<NodeAction> Of(TaskNodeViewModel node) =>
        node.Proposal is null ? For(node) : [.. For(node).Select(action => action with { IsPrimary = false })];

    private static IReadOnlyList<NodeAction> For(TaskNodeViewModel node)
    {
        // The open conversation holds what the state needs next, and the row must not offer it twice.
        if (node is { IsConversationShown: true, Proposal: null, State: NodeState.Running or NodeState.InputRequired or NodeState.Failed or NodeState.Stale })
        {
            return [];
        }

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
            // Retry would end what the agent waits on, so it stays in the More menu.
            return Row(Conversation(node, "Reply"), node.ShowsStop ? Stop(node) : null, node.ShowsApprove ? Approve(node, primary: false) : null);
        }

        return node.State switch
        {
            NodeState.Idle => Row(Conversation(node, "Conversation", primary: false), new NodeAction("RunTask", "Run", "IconRun", node.RunCommand, true, node.RunRefusal ?? "Runs this node, then each node after it")),
            NodeState.Running => Row(Conversation(node, "Conversation"), Stop(node)),
            NodeState.Failed or NodeState.Stale => Row(Conversation(node, "Conversation", primary: false), Retry(node, primary: true)),
            _ => Row(Conversation(node, "Conversation"), node.ShowsRetry ? Retry(node, primary: !node.HasAgent) : null),
        };
    }

    private static NodeAction[] Row(params NodeAction?[] actions) => [.. actions.OfType<NodeAction>()];

    // An approval node has no agent to talk to, so it takes no conversation button.
    private static NodeAction? Conversation(TaskNodeViewModel node, string label, bool primary = true) => node is { HasAgent: true, IsConversationShown: false }
        ? new NodeAction("OpenConversation", label, "IconChat", node.OpenConversationCommand, primary, $"{node.AttentionLabel ?? "Open the full conversation"} (Enter)")
        : null;

    private static NodeAction Approve(TaskNodeViewModel node, bool primary) =>
        new("Approve", node.ApproveLabel, "IconCheckmark", node.ApproveCommand, primary, null);

    private static NodeAction SendBack(TaskNodeViewModel node) =>
        new("SendBack", "Send back", "IconUndo", node.SendBackCommand, false, "Write a note first. It goes to the nodes before this one.");

    private static NodeAction Retry(TaskNodeViewModel node, bool primary) =>
        new("RetryTask", node.State == NodeState.Completed ? "Run from here" : "Retry", "IconRefresh", node.RetryCommand, primary,
            node.RunRefusal ?? "Runs this node again in a fresh session, then each node after it");

    private static NodeAction Stop(TaskNodeViewModel node) => new("StopTask", "Stop", "IconStop", node.StopCommand, false, null);
}
