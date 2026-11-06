namespace IDevelop.Desktop.Canvas;

/// <summary>A group of nodes in the run bar: their state's badge, how many there are in words, and what its tooltip adds.</summary>
public sealed record StateChip(NodeState State, WaitReason Why, int Count, string Label, string Tip)
{
    /// <summary>What a tight bar's chip keeps beside its glyph.</summary>
    public string Short => Count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The chip for <paramref name="count"/> nodes, or null when there are none.</summary>
    public static StateChip? Of(NodeState state, WaitReason why, int count, string words, string? tip = null) =>
        count > 0 ? new StateChip(state, why, count, $"{count} {words}", tip ?? $"{count} {words}") : null;
}
