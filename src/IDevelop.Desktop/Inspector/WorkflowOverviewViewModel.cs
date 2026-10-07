using System.Collections.Immutable;
using IDevelop.Desktop.Blueprints;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Mvvm;

namespace IDevelop.Desktop.Inspector;

/// <summary>How many of the workflow's nodes are of one kind.</summary>
public sealed record KindCount(NodeKind Kind, string Label, int Count);

/// <summary>The workflow inspector's header and overview while nothing is selected: the workflow, its task count, and its kinds.</summary>
public sealed class WorkflowOverviewViewModel : ObservableObject
{
    private readonly WorkflowCanvasViewModel _canvas;
    private ImmutableArray<KindCount> _kinds = [];

    internal WorkflowOverviewViewModel(WorkflowCanvasViewModel canvas)
    {
        _canvas = canvas;
        canvas.Nodes.CollectionChanged += (_, _) => Count();
        canvas.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkflowCanvasViewModel.Name))
            {
                OnPropertyChanged(nameof(Name));
            }
        };
        Count();
    }

    public string Name => _canvas.Name;

    public string TaskCount => _canvas.Nodes.Count == 1 ? "1 task" : $"{_canvas.Nodes.Count} tasks";

    /// <summary>Each kind the workflow holds, in the registry's order.</summary>
    public IReadOnlyList<KindCount> Kinds => _kinds;

    public bool HasKinds => !_kinds.IsEmpty;

    /// <summary>The libraries, which the workflow inspector lists under the overview.</summary>
    public BlueprintsViewModel Blueprints => _canvas.Blueprints;

    private void Count()
    {
        ImmutableArray<KindCount> kinds =
            [.. _canvas.Nodes.GroupBy(node => node.Kind).OrderBy(group => group.Key).Select(group => new KindCount(group.Key, NodeKinds.Info(group.Key).Label, group.Count()))];
        if (!kinds.SequenceEqual(_kinds))
        {
            _kinds = kinds;
            OnPropertyChanged(nameof(Kinds));
            OnPropertyChanged(nameof(HasKinds));
        }

        OnPropertyChanged(nameof(TaskCount));
    }
}
