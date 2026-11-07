using IDevelop.Desktop.Blueprints;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Mvvm;

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
        canvas.Nodes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(TaskCount));
        canvas.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkflowCanvasViewModel.Name))
            {
                OnPropertyChanged(nameof(Name));
            }
        };
    }

    public string Name => _canvas.Name;

    public string TaskCount => _canvas.Nodes.Count == 1 ? "1 node" : $"{_canvas.Nodes.Count} nodes";

    /// <summary>The libraries, which the workflow inspector lists.</summary>
    public BlueprintsViewModel Blueprints => _canvas.Blueprints;
}
