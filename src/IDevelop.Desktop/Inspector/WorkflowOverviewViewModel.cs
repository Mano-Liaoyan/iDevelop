using IDevelop.Desktop.Blueprints;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Mvvm;
using IDevelop.Desktop.Theme;

namespace IDevelop.Desktop.Inspector;

/// <summary>
/// The workflow inspector's header and library while nothing is selected: the workflow and its node count. The run bar holds
/// the workflow's state counts, so the inspector does not repeat them.
/// </summary>
public sealed class WorkflowOverviewViewModel : ObservableObject
{
    private readonly WorkflowCanvasViewModel _canvas;

    internal WorkflowOverviewViewModel(WorkflowCanvasViewModel canvas)
    {
        _canvas = canvas;
        canvas.Nodes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(StateLine));
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

    /// <summary>"2 of 9 · 1 running · 2 waiting" once a node ran, "9 nodes" before, as the workflow's tooltip says it.</summary>
    public string StateLine => StateCounts.KeepCountsWhole(_canvas.Counts.Summary());

    /// <summary>The libraries, which the workflow inspector lists.</summary>
    public BlueprintsViewModel Blueprints => _canvas.Blueprints;
}
