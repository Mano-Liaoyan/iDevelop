namespace IDevelop.Desktop.Canvas;

/// <summary>A group of nodes in the run bar: their state's badge, how many there are in words, and what its tooltip adds.</summary>
public sealed record StateChip(NodeState State, WaitReason Why, int Count, string Label, string Tip)
{
    /// <summary>
    /// A tight place shows this chip as its badge and its number. Only a group that does not need the person is shown so:
    /// waiting and failed always keep their words.
    /// </summary>
    public bool IsCompact { get; init; }

    /// <summary>What a row or a bar shows beside the badge: the words, or the number of a compact chip.</summary>
    public string RowText => IsCompact ? Count.ToString(System.Globalization.CultureInfo.InvariantCulture) : Label;

    /// <summary>The nodes need the person, so the chip always keeps its words.</summary>
    public bool IsUrgent => State == NodeState.Failed || State == NodeState.InputRequired && Why != WaitReason.Interrupted;

    /// <summary>The nodes do not change by themselves and need nobody now, so a tight place shows the chip first in short.</summary>
    public bool IsQuiet => State is NodeState.Stale or NodeState.Completed or NodeState.Idle;

    /// <summary>The chip for <paramref name="count"/> nodes, or null when there are none.</summary>
    public static StateChip? Of(NodeState state, WaitReason why, int count, string words, string? tip = null) =>
        count > 0 ? new StateChip(state, why, count, $"{count} {words}", tip ?? $"{count} {words}") : null;

    /// <summary>
    /// The chips with each one that <paramref name="shorten"/> picks shown as its badge and number. An urgent chip keeps its words,
    /// and no chip is hidden, so a person never needs a tooltip to see that nodes wait or failed.
    /// </summary>
    public static IReadOnlyList<StateChip> Shorten(IReadOnlyList<StateChip> chips, Func<StateChip, bool> shorten) =>
        [.. chips.Select(chip => !chip.IsUrgent && shorten(chip) ? chip with { IsCompact = true } : chip)];
}
