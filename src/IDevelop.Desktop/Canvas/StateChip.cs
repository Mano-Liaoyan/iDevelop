namespace IDevelop.Desktop.Canvas;

/// <summary>A group of nodes in the run bar: their state's badge, how many there are, and the words for its tooltip.</summary>
public sealed record StateChip(NodeState State, WaitReason Why, int Count, string Label)
{
    /// <summary>The chip for <paramref name="count"/> nodes, or null when there are none.</summary>
    public static StateChip? Of(NodeState state, WaitReason why, int count, string words) =>
        count > 0 ? new StateChip(state, why, count, $"{count} {words}") : null;
}
