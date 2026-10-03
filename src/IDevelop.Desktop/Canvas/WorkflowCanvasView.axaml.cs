using Avalonia;
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

    /// <summary>Centers the canvas on a task chosen in the sidebar when its card is not wholly in view.</summary>
    internal void BringIntoViewIfHidden(TaskNodeViewModel node)
    {
        var card = new Rect(node.Location, new Size(WorkflowCanvasViewModel.TaskCardWidth, WorkflowCanvasViewModel.TaskCardHeight));
        if (!new Rect(Editor.ViewportLocation, Editor.ViewportSize).Contains(card))
        {
            // Nodify's animated pan moves the editor alone, so the view model, which places new tasks, would keep
            // the old location.
            Editor.BringIntoView(card.Center, animated: false);
        }
    }

    private static void OpenConnectionMenu(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Right && e.Source is BaseConnection { ContextMenu: { } menu } connection)
        {
            menu.Open(connection);
            e.Handled = true;
        }
    }
}
