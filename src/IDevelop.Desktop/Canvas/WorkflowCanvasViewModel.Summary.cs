using IDevelop.Desktop.Theme;

namespace IDevelop.Desktop.Canvas;

/// <summary>What the left area's workflow row says about the workflow's run. All of it is read from the nodes' states.</summary>
public sealed partial class WorkflowCanvasViewModel
{
    /// <summary>How many nodes are in each state.</summary>
    public StateCounts Counts => new(
        Nodes.Count(node => node.State == NodeState.Completed), Nodes.Count(node => node.State == NodeState.Running), WaitingCount,
        Nodes.Count(node => node.State == NodeState.Failed), Nodes.Count(node => node.State == NodeState.Stale), Nodes.Count);

    /// <summary>"5 of 9 · 1 running · 3 need you", "9 nodes" before anything runs, or "No nodes yet", then "Unsaved" while it applies.</summary>
    public string Summary => string.Join(" · ", new[] { Counts.Summary(), HasUnsavedChanges ? "Unsaved" : null }.OfType<string>());

    /// <summary>How far the work went, at the end of the name's line. The line under the name holds the states.</summary>
    public string RowProgress => Counts.Total == 0 ? "Empty" : $"{Counts.Completed}/{Counts.Total}";

    /// <summary>
    /// What a workflow row says under its name: the most urgent group's State Mark and every group of the run bar in the bar's own words and
    /// groups, such as "2 need you · 1 failed". The line wraps as far as it needs and breaks only between groups, so no count is cut (ADR 0074).
    /// </summary>
    public IReadOnlyList<StateChip> RowChips => BarChips is [var first, ..]
        ? [first with { Label = string.Join("\u00a0· ", BarChips.Select(chip => chip.Label.Replace(' ', '\u00a0'))), Tip = string.Join("\n", BarChips.Select(chip => chip.Tip)) }]
        : [];

    /// <summary>The row has its status line while any group shows in the run bar. The Nodes section lists the states one by one (ADR 0071).</summary>
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

