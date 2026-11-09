using IDevelop.Desktop.Blueprints;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Mvvm;
using IDevelop.Desktop.Theme;

namespace IDevelop.Desktop.Inspector;

/// <summary>How many nodes of one kind the workflow holds.</summary>
public sealed record KindCount(NodeKind Kind, string Label, int Count);

/// <summary>
/// The Workflow page while nothing is selected: the workflow's name, its line of progress, its nodes by kind, and the libraries.
/// The Run Strip holds the workflow's state counts, so the page does not repeat them.
/// </summary>
public sealed class WorkflowOverviewViewModel : ObservableObject
{
    private readonly WorkflowCanvasViewModel _canvas;

    internal WorkflowOverviewViewModel(WorkflowCanvasViewModel canvas)
    {
        _canvas = canvas;
        canvas.Nodes.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(StateLine));
            OnPropertyChanged(nameof(Kinds));
            OnPropertyChanged(nameof(HasKinds));
        };
        canvas.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkflowCanvasViewModel.Name))
            {
                OnPropertyChanged(nameof(Name));
            }
            else if (e.PropertyName == nameof(WorkflowCanvasViewModel.Counts))
            {
                OnPropertyChanged(nameof(StateLine));
            }
        };
    }

    public string Name => _canvas.Name;

    /// <summary>"2 of 9 done" once a node ran, "9 nodes" before. The Run Strip names the states, so the page does not repeat them.</summary>
    public string StateLine => StateCounts.KeepCountsWhole(_canvas.Counts.HasRun ? $"{_canvas.Counts.Progress} done" : _canvas.Counts.Summary());

    /// <summary>The workflow's nodes by kind, in the order of the kinds.</summary>
    public IReadOnlyList<KindCount> Kinds => [.. _canvas.Nodes.GroupBy(node => node.Kind).OrderBy(group => group.Key)
        .Select(group => new KindCount(group.Key, NodeKinds.Info(group.Key).Label, group.Count()))];

    public bool HasKinds => _canvas.Nodes.Count > 0;

    /// <summary>The libraries, which the Workflow page lists.</summary>
    public BlueprintsViewModel Blueprints => _canvas.Blueprints;
}
