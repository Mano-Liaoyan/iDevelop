namespace IDevelop.Desktop.Canvas;

/// <summary>A group of nodes in the run bar: their state's badge, how many there are in words, and what its tooltip adds.</summary>
public sealed record StateChip(NodeState State, WaitReason Why, int Count, string Label, string Tip)
{
    /// <summary>The chip sums other groups in "+N more", so it has no badge, and its tooltip names the groups.</summary>
    public bool IsMore { get; init; }

    /// <summary>What a row or a bar shows beside the badge. A chip never shows a bare count.</summary>
    public string RowText => Label;

    /// <summary>The nodes need the person, so a tight bar keeps this chip.</summary>
    public bool IsUrgent => State == NodeState.Failed || State == NodeState.InputRequired && Why != WaitReason.Interrupted;

    /// <summary>The chip for <paramref name="count"/> nodes, or null when there are none.</summary>
    public static StateChip? Of(NodeState state, WaitReason why, int count, string words, string? tip = null) =>
        count > 0 ? new StateChip(state, why, count, $"{count} {words}", tip ?? $"{count} {words}") : null;

    /// <summary>
    /// The first <paramref name="keep"/> chips with their words, and one "+N more" chip that sums the nodes of the others and
    /// names them in its tooltip. With nothing to fold, the chips stay as they are.
    /// </summary>
    public static IReadOnlyList<StateChip> Fold(IReadOnlyList<StateChip> chips, int keep)
    {
        if (chips.Count <= keep)
        {
            return chips;
        }

        var rest = chips.Skip(keep).ToList();
        var count = rest.Sum(chip => chip.Count);
        return [.. chips.Take(keep), new StateChip(NodeState.Idle, WaitReason.None, count, $"+{count} more", string.Join(", ", rest.Select(chip => chip.Label))) { IsMore = true }];
    }
}
