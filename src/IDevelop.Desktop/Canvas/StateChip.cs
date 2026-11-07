namespace IDevelop.Desktop.Canvas;

/// <summary>A group of nodes in the run bar: their state's badge, how many there are in words, and what its tooltip adds.</summary>
public sealed record StateChip(NodeState State, WaitReason Why, int Count, string Label, string Tip)
{
    /// <summary>A tight row or bar gives a group its badge and count, and its tooltip the words, so no group hides.</summary>
    public bool IsCounted { get; init; }

    /// <summary>What a row or a bar shows beside the badge.</summary>
    public string RowText => IsCounted ? Count.ToString() : Label;

    /// <summary>The nodes need the person, so a tight bar keeps this chip.</summary>
    public bool IsUrgent => State == NodeState.Failed || State == NodeState.InputRequired && Why != WaitReason.Interrupted;

    /// <summary>The chip for <paramref name="count"/> nodes, or null when there are none.</summary>
    public static StateChip? Of(NodeState state, WaitReason why, int count, string words, string? tip = null) =>
        count > 0 ? new StateChip(state, why, count, $"{count} {words}", tip ?? $"{count} {words}") : null;
}
