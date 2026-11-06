using IDevelop.Desktop.Theme;

namespace IDevelop.Desktop.Canvas;

/// <summary>What the left area's workflow row says about the workflow's run. All of it is read from the nodes' states.</summary>
public sealed partial class WorkflowCanvasViewModel
{
    /// <summary>How many nodes are in each state.</summary>
    public StateCounts Counts => new(
        Nodes.Count(node => node.State == NodeState.Completed), Nodes.Count(node => node.State == NodeState.Running), WaitingCount,
        Nodes.Count(node => node.State == NodeState.Failed), Nodes.Count(node => node.State == NodeState.Stale), Nodes.Count);

    /// <summary>A node of the workflow has run, so its row shows a progress ring.</summary>
    public bool HasRun => Counts.HasRun;

    /// <summary>"5 of 9 · 1 running · 3 waiting", "9 nodes" before anything runs, or "No nodes yet", then "Unsaved" while it applies.</summary>
    public string Summary => string.Join(" · ", new[] { Counts.Summary(), HasUnsavedChanges ? "Unsaved" : null }.OfType<string>());

    /// <summary>The row's second line: how far the work went, with the state counts as glyphs after it.</summary>
    public string RowProgress => Counts.Total == 0 ? "Empty" : $"{Counts.Completed}/{Counts.Total}";

    /// <summary>
    /// What a row says at its end: what runs, what waits, and what failed, each with its count. A stale node shows only while
    /// none of them does. Without any of these the row shows its progress.
    /// </summary>
    public IReadOnlyList<RowFact> RowFacts
    {
        get
        {
            var counts = Counts;
            RowFact?[] facts =
            [
                counts.Running > 0 ? new(NodeState.Running, counts.Running) : null,
                counts.Waiting > 0 ? new(NodeState.InputRequired, counts.Waiting) : null,
                counts.Failed > 0 ? new(NodeState.Failed, counts.Failed) : null,
            ];
            var shown = facts.OfType<RowFact>().ToList();
            return shown.Count == 0 && counts.Stale > 0 ? [new(NodeState.Stale, counts.Stale)] : shown;
        }
    }

    public bool HasRowFacts => RowFacts.Count > 0;

    /// <summary>The list shows a node of this workflow as selected, so the workflow's row keeps only its edge bar.</summary>
    public bool ShowsNodeSelection => IsExpanded && SelectedNode is not null;

    private void OnSummaryChanged()
    {
        OnPropertyChanged(nameof(Counts));
        OnPropertyChanged(nameof(HasRun));
        OnPropertyChanged(nameof(RowProgress));
        OnPropertyChanged(nameof(RowFacts));
        OnPropertyChanged(nameof(HasRowFacts));
        OnPropertyChanged(nameof(Summary));
    }
}

/// <summary>A state that a workflow's row names, and how many of its nodes are in it.</summary>
public sealed record RowFact(NodeState State, int Count)
{
    public bool IsRunning => State == NodeState.Running;
}
