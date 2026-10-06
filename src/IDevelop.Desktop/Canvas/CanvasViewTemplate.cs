using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// Builds a new view for each workflow the window shows. A XAML data template would hand the same editor from one
/// workflow to the next, and the editor would carry its selection and viewport across while its bindings move.
/// </summary>
public sealed class CanvasViewTemplate : IDataTemplate
{
    public Control Build(object? param) => new WorkflowCanvasView(param as WorkflowCanvasViewModel);

    public bool Match(object? data) => data is WorkflowCanvasViewModel;
}
