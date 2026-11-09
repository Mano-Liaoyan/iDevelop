using IDevelop.Desktop.Theme;

namespace IDevelop.Desktop.Canvas;

/// <summary>What the left area's workflow row says about the workflow's run. All of it is read from the nodes' states.</summary>
public sealed partial class WorkflowCanvasViewModel
{
    /// <summary>How many nodes are in each state.</summary>
    public StateCounts Counts => StateCounts.Of(Nodes);

    /// <summary>The workflow's one State Mark, or null when no mark shows: Needs you, Failed, Running, Stale, then Done when all are done.</summary>
    public NodeState? Rollup => Counts.Rollup;

    /// <summary>The reason inside a Needs you rollup mark: the one reason every waiting agent shares, or a question for several.</summary>
    public WaitReason RollupWhy => NodeStates.CommonReason(Nodes);

    public bool HasRollup => Rollup is not null;

    /// <summary>"5 of 9 · 1 running · 3 need you", "9 nodes" before anything runs, or "No nodes yet", then "Unsaved" while it applies.</summary>
    public string Summary => string.Join(" · ", new[] { Counts.Summary(), HasUnsavedChanges ? "Unsaved" : null }.OfType<string>());

    /// <summary>How far the work went, at the end of the name's line. The line under the name holds the states.</summary>
    public string RowProgress => Counts.Total == 0 ? "Empty" : $"{Counts.Completed}/{Counts.Total}";

    /// <summary>The workflow row's tooltip: the whole name, which the row may cut, and the whole sum.</summary>
    public string RowTip => $"{Name}\n{Summary}";

    private void OnSummaryChanged()
    {
        OnPropertyChanged(nameof(Counts));
        OnPropertyChanged(nameof(RowProgress));
        OnPropertyChanged(nameof(Rollup));
        OnPropertyChanged(nameof(RollupWhy));
        OnPropertyChanged(nameof(HasRollup));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(RowTip));
    }
}

