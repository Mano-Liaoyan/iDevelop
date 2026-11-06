using IDevelop.Desktop.Sidebar;
using IDevelop.Desktop.Theme;

namespace IDevelop.Desktop.Canvas;

/// <summary>What the left area's workflow row says about the workflow's run. All of it is read from the nodes' states.</summary>
public sealed partial class WorkflowCanvasViewModel
{
    /// <summary>How many nodes are in each state.</summary>
    public StateCounts Counts => new(
        Nodes.Count(node => node.State == NodeState.Completed), Nodes.Count(node => node.State == NodeState.Running), WaitingCount,
        Nodes.Count(node => node.State == NodeState.Failed), Nodes.Count(node => node.State == NodeState.Stale), Nodes.Count);

    /// <summary>"5 of 9 · 1 running · 3 waiting", "9 nodes" before anything runs, or "No nodes yet", then "Unsaved" while it applies.</summary>
    public string Summary => string.Join(" · ", new[] { Counts.Summary(), HasUnsavedChanges ? "Unsaved" : null }.OfType<string>());

    /// <summary>How far the work went, at the row's end. The chips under the name hold the other states.</summary>
    public string RowProgress => Counts.Total == 0 ? "Empty" : $"{Counts.Completed}/{Counts.Total}";

    /// <summary>What the first running node does now, in one line, or null while none runs. The row shows it under the name.</summary>
    public string? RowActivity => Outline.FirstOrDefault(node => node.State == NodeState.Running) is { } node ? ActivityRow.OneLine(node.Latest) : null;

    /// <summary>The list shows a node of this workflow as selected, so the workflow's row keeps a quiet fill.</summary>
    public bool ShowsNodeSelection => IsExpanded && SelectedNode is not null;

    private void OnSummaryChanged()
    {
        OnPropertyChanged(nameof(Counts));
        OnPropertyChanged(nameof(RowProgress));
        OnPropertyChanged(nameof(RowActivity));
        OnPropertyChanged(nameof(Summary));
    }
}

