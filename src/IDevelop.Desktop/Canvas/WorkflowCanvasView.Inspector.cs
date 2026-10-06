using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using IDevelop.Desktop.Inspector;

namespace IDevelop.Desktop.Canvas;

/// <summary>What the canvas view does in the inspector beside it.</summary>
public partial class WorkflowCanvasView
{
    // The node menu runs its command before it closes. A background pass opens the list once the menu has closed, so
    // nothing the menu does as it closes can reach the list.
    void ICanvasView.FocusAgent() => Dispatcher.UIThread.Post(
        () => TopLevel.GetTopLevel(this)?.GetVisualDescendants().OfType<InspectorView>().FirstOrDefault()?.FocusClientPicker(),
        DispatcherPriority.Background);
}
