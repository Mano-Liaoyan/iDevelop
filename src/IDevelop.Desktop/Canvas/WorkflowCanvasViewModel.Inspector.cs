using IDevelop.Desktop.Inspector;

namespace IDevelop.Desktop.Canvas;

/// <summary>What the inspector reads from the canvas while nothing is selected, and its way to select a node.</summary>
public sealed partial class WorkflowCanvasViewModel
{
    public WorkflowOverviewViewModel Overview => field ??= new WorkflowOverviewViewModel(this);

    /// <summary>Selects the node alone, so the inspector shows it.</summary>
    internal void Inspect(TaskNodeViewModel node) => Select(node);
}
