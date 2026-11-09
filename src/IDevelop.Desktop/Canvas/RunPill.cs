using System.Windows.Input;

namespace IDevelop.Desktop.Canvas;

/// <summary>One pill of the Run Strip: a group of nodes, its State Mark, its count and words, and what a click shows.</summary>
public sealed record RunPill(NodeState State, WaitReason Why, int Count, string Words, ICommand Command, string AutomationId, string Tip)
{
    /// <summary>"3 need you".</summary>
    public string Label => $"{Count} {Words}";

    /// <summary>The pill for <paramref name="count"/> nodes, or null when there are none.</summary>
    public static RunPill? Of(NodeState state, WaitReason why, int count, string words, ICommand command, string automationId, string tip) =>
        count > 0 ? new RunPill(state, why, count, words, command, automationId, tip) : null;
}
