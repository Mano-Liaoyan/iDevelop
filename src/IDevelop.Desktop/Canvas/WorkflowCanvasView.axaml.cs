using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Nodify;

namespace IDevelop.Desktop.Canvas;

public partial class WorkflowCanvasView : UserControl
{
    public WorkflowCanvasView()
    {
        InitializeComponent();
        // NodifyAvalonia 6.6.0 connections handle pointer release without calling the base
        // handler, so Avalonia never raises ContextRequested for them and their menu never opens.
        Editor.AddHandler(PointerReleasedEvent, OpenConnectionMenu, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private static void OpenConnectionMenu(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Right && e.Source is Connection { ContextMenu: { } menu } connection)
        {
            menu.Open(connection);
            e.Handled = true;
        }
    }
}
