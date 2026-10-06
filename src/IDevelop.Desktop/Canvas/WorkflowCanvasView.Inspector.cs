using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using IDevelop.Desktop.Inspector;

namespace IDevelop.Desktop.Canvas;

/// <summary>What the canvas view does in the inspector beside it.</summary>
public partial class WorkflowCanvasView
{
    // The node menu runs its command while it is still open, so the list opens on a background pass, after the menu has
    // closed.
    void ICanvasView.FocusAgent() => Dispatcher.UIThread.Post(
        () => TopLevel.GetTopLevel(this)?.GetVisualDescendants().OfType<InspectorView>().FirstOrDefault()?.FocusClientPicker(),
        DispatcherPriority.Background);
}
