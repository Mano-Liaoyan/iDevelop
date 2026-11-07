namespace IDevelop.Desktop.Canvas;

/// <summary>A group of nodes in the run bar: their state's badge, how many there are in words, and what its tooltip adds.</summary>
public sealed record StateChip(NodeState State, WaitReason Why, int Count, string Label, string Tip)
{
    /// <summary>The nodes need the person, so a tight bar keeps this chip.</summary>
    public bool IsUrgent => State == NodeState.Failed || State == NodeState.InputRequired && Why != WaitReason.Interrupted;

    /// <summary>The chip stands for the other groups, so it has words and no badge.</summary>
    public bool IsRest => State == NodeState.Idle;

    /// <summary>One chip, "+3", for <paramref name="groups"/>. Its tooltip names them.</summary>
    public static StateChip Rest(IReadOnlyCollection<StateChip> groups) =>
        new(NodeState.Idle, WaitReason.None, groups.Sum(chip => chip.Count), $"+{groups.Count}", string.Join(", ", groups.Select(chip => chip.Label)));

    /// <summary>The chip for <paramref name="count"/> nodes, or null when there are none.</summary>
    public static StateChip? Of(NodeState state, WaitReason why, int count, string words, string? tip = null) =>
        count > 0 ? new StateChip(state, why, count, $"{count} {words}", tip ?? $"{count} {words}") : null;
}
