using Avalonia.Controls;
using IDevelop.Desktop.Canvas;

namespace IDevelop.Desktop;

/// <summary>Renames a workflow on its sidebar row. After Enter or Escape, the row takes the focus.</summary>
public sealed class WorkflowNameBox : RenameBox
{
    private WorkflowCanvasViewModel? Workflow => DataContext as WorkflowCanvasViewModel;

    protected override string? ItemText => Workflow?.Name;

    protected override Control? FocusTarget => (Parent as Panel)?.Children.OfType<Button>().FirstOrDefault();

    protected override void End(string? text) => Workflow?.EndRenameWorkflow(text);
}
