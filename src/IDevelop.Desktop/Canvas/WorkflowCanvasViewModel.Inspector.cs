using IDevelop.Desktop.Inspector;

namespace IDevelop.Desktop.Canvas;

/// <summary>What the inspector reads from the canvas while nothing is selected, and its way to select a node.</summary>
public sealed partial class WorkflowCanvasViewModel
{
    private WorkflowOverviewViewModel? _overview;

    public WorkflowOverviewViewModel Overview => _overview ??= new WorkflowOverviewViewModel(this);

    /// <summary>Selects the node alone, so the inspector shows it.</summary>
    internal void Inspect(TaskNodeViewModel node) => Select(node);
}
