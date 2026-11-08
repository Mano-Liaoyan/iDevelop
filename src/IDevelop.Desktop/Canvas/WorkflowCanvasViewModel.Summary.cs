using IDevelop.Desktop.Theme;

namespace IDevelop.Desktop.Canvas;

/// <summary>What the left area's workflow row says about the workflow's run. All of it is read from the nodes' states.</summary>
public sealed partial class WorkflowCanvasViewModel
{
    /// <summary>How many nodes are in each state.</summary>
    public StateCounts Counts => new(
        Nodes.Count(node => node.State == NodeState.Completed), Nodes.Count(node => node.State == NodeState.Running), WaitingCount,
        Nodes.Count(node => node.WaitReason == WaitReason.Interrupted), Nodes.Count(node => node.State == NodeState.Failed), Nodes.Count(node => node.State == NodeState.Stale), Nodes.Count);

    /// <summary>"5 of 9 · 1 running · 3 waiting", "9 nodes" before anything runs, or "No nodes yet", then "Unsaved" while it applies.</summary>
    public string Summary => string.Join(" · ", new[] { Counts.Summary(), HasUnsavedChanges ? "Unsaved" : null }.OfType<string>());

    /// <summary>How far the work went, at the row's end. The chip before it holds the most urgent state.</summary>
    public string RowProgress => Counts.Total == 0 ? "Empty" : $"{Counts.Completed}/{Counts.Total}";

    /// <summary>
    /// What a folded row says on the name's line: the run bar's most urgent group as a badge, so every row keeps one height, with the
    /// count of all the nodes that need the person, the Nodes section's Needs you group, or else that group's own count. Its tooltip
    /// and name list every group in words, as the run bar says them (ADR 0070).
    /// </summary>
    public IReadOnlyList<StateChip> RowChips => BarChips is [var first, ..]
        ? [first with { Count = NeedsYouCount > 0 ? NeedsYouCount : first.Count, Label = string.Join(", ", BarChips.Select(chip => chip.Label)), Tip = string.Join("\n", BarChips.Select(chip => chip.Tip)) }]
        : [];

    /// <summary>How many nodes the Nodes section's Needs you group holds: what waits for the person, what was interrupted, and what failed.</summary>
    private int NeedsYouCount => Counts.Waiting + Counts.Interrupted + Counts.Failed;

    /// <summary>The row says its most urgent group while any node needs a look. The Nodes section lists the states one by one (ADR 0071).</summary>
    public bool ShowsRowChips => BarChips.Count > 0;

    /// <summary>The workflow row's tooltip: the whole name, which the row may cut, and the whole sum.</summary>
    public string RowTip => $"{Name}\n{Summary}";

    private void OnSummaryChanged()
    {
        OnPropertyChanged(nameof(Counts));
        OnPropertyChanged(nameof(RowProgress));
        OnPropertyChanged(nameof(RowChips));
        OnPropertyChanged(nameof(ShowsRowChips));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(RowTip));
    }
}

