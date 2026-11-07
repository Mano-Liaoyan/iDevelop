using IDevelop.Desktop.Theme;

namespace IDevelop.Desktop.Canvas;

/// <summary>What the left area's workflow row says about the workflow's run. All of it is read from the nodes' states.</summary>
public sealed partial class WorkflowCanvasViewModel
{
    private const int RowChipLimit = 3;

    /// <summary>How many nodes are in each state.</summary>
    public StateCounts Counts => new(
        Nodes.Count(node => node.State == NodeState.Completed), Nodes.Count(node => node.State == NodeState.Running), WaitingCount,
        Nodes.Count(node => node.WaitReason == WaitReason.Interrupted), Nodes.Count(node => node.State == NodeState.Failed), Nodes.Count(node => node.State == NodeState.Stale), Nodes.Count);

    /// <summary>"5 of 9 · 1 running · 3 waiting", "9 nodes" before anything runs, or "No nodes yet", then "Unsaved" while it applies.</summary>
    public string Summary => string.Join(" · ", new[] { Counts.Summary(), HasUnsavedChanges ? "Unsaved" : null }.OfType<string>());

    /// <summary>How far the work went, at the row's end. The chips under the name hold the other states.</summary>
    public string RowProgress => Counts.Total == 0 ? "Empty" : $"{Counts.Completed}/{Counts.Total}";

    /// <summary>
    /// What a folded row says under the name in one line: the run bar's groups, most urgent first, so the row and the bar give
    /// one number for one group. Past <see cref="RowChipLimit"/> groups, one "+2" stands for the rest, and the row's tooltip names them.
    /// </summary>
    public IReadOnlyList<StateChip> RowChips
    {
        get
        {
            var groups = BarGroups;
            if (groups.Count <= RowChipLimit)
            {
                return groups;
            }

            var rest = groups.Skip(RowChipLimit - 1).ToList();
            return [.. groups.Take(RowChipLimit - 1), StateChip.Rest(rest)];
        }
    }

    /// <summary>A folded row says its groups. The node rows of an open workflow show the same states one by one.</summary>
    public bool ShowsRowChips => !IsExpanded && BarGroups.Count > 0;

    /// <summary>The workflow row's tooltip: the whole name, which the row may cut, and the whole sum.</summary>
    public string RowTip => $"{Name}\n{Summary}";

    /// <summary>The list shows a node of this workflow as selected, so the workflow's row keeps a quiet fill.</summary>
    public bool ShowsNodeSelection => IsExpanded && SelectedNode is not null;

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

