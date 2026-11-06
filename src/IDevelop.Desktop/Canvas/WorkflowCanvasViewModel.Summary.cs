using IDevelop.Desktop.Theme;

namespace IDevelop.Desktop.Canvas;

/// <summary>What the left area's workflow row says about the workflow's run. All of it is read from the nodes' states.</summary>
public sealed partial class WorkflowCanvasViewModel
{
    /// <summary>How many nodes are in each state.</summary>
    public StateCounts Counts => new(
        Nodes.Count(node => node.State == NodeState.Completed), Nodes.Count(node => node.State == NodeState.Running), WaitingCount,
        Nodes.Count(node => node.State == NodeState.Failed), Nodes.Count(node => node.State == NodeState.Stale), Nodes.Count);

    /// <summary>A node of the workflow has run, so its row shows a progress bar.</summary>
    public bool HasRun => Counts.HasRun;

    /// <summary>"5 of 9 · 1 running · 3 waiting", "9 nodes" before anything runs, or "No nodes yet", then "Unsaved" while it applies.</summary>
    public string Summary => string.Join(" · ", new[] { Counts.Summary(), HasUnsavedChanges ? "Unsaved" : null }.OfType<string>());

    /// <summary>The row's second line: how far the work went, with the state counts as glyphs after it.</summary>
    public string RowProgress => Counts.Total == 0 ? "Empty" : $"{Counts.Completed}/{Counts.Total}";

    /// <summary>The state a row names: what failed, else what waits, else what runs, else what is stale, or null while nothing does.</summary>
    public NodeState? RowAlert
    {
        get
        {
            var counts = Counts;
            return counts.Failed > 0 ? NodeState.Failed
                : counts.Waiting > 0 ? NodeState.InputRequired
                : counts.Running > 0 ? NodeState.Running
                : counts.Stale > 0 ? NodeState.Stale
                : null;
        }
    }

    /// <summary>How many nodes are in <see cref="RowAlert"/>.</summary>
    public int RowAlertCount => RowAlert switch
    {
        NodeState.Failed => Counts.Failed,
        NodeState.InputRequired => Counts.Waiting,
        NodeState.Running => Counts.Running,
        NodeState.Stale => Counts.Stale,
        _ => 0,
    };

    /// <summary>The list shows a node of this workflow as selected, so the workflow's row keeps only its edge bar.</summary>
    public bool ShowsNodeSelection => IsExpanded && SelectedNode is not null;

    private void OnSummaryChanged()
    {
        OnPropertyChanged(nameof(Counts));
        OnPropertyChanged(nameof(HasRun));
        OnPropertyChanged(nameof(RowProgress));
        OnPropertyChanged(nameof(RowAlert));
        OnPropertyChanged(nameof(RowAlertCount));
        OnPropertyChanged(nameof(Summary));
    }
}
