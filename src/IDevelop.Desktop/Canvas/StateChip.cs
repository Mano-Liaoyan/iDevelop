namespace IDevelop.Desktop.Canvas;

/// <summary>A group of nodes in the run bar: their State Mark, how many there are in words, and what its tooltip adds.</summary>
public sealed record StateChip(NodeState State, WaitReason Why, int Count, string Label, string Tip)
{
    /// <summary>The chip for <paramref name="count"/> nodes, or null when there are none.</summary>
    public static StateChip? Of(NodeState state, WaitReason why, int count, string label, string? tip = null) =>
        count > 0 ? new StateChip(state, why, count, label, tip ?? label) : null;
}
