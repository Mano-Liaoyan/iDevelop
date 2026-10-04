using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Nodify;

namespace IDevelop.Desktop.Canvas;

public partial class WorkflowCanvasView : UserControl
{
    static WorkflowCanvasView()
    {
        // NodifyAvalonia 6.6.0 refreshes a minimap item's layout only inside a DecoratorContainer,
        // so a moved task's item would keep its old place until the viewport changes.
        MinimapItem.LocationProperty.Changed.AddClassHandler<MinimapItem>((item, _) => (item.GetVisualParent() as Layoutable)?.InvalidateMeasure());
    }

    public WorkflowCanvasView()
    {
        InitializeComponent();
        // NodifyAvalonia 6.6.0 connections handle pointer release without calling the base
        // handler, so Avalonia never raises ContextRequested for them and their menu never opens.
        Editor.AddHandler(PointerReleasedEvent, OpenConnectionMenu, RoutingStrategies.Bubble, handledEventsToo: true);
        // NodifyAvalonia 6.6.0 zooms the minimap by the wheel delta's length, which drops its direction and is
        // about 0.2% per notch, so the minimap's wheel zooms here before Nodify sees it.
        Minimap.AddHandler(PointerWheelChangedEvent, OnMinimapWheel, RoutingStrategies.Tunnel);
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

    // Nodify's commands route from the focused element, which a button beside the editor never reaches.
    private void OnZoomIn(object? sender, RoutedEventArgs e) => EditorCommands.ZoomIn.Execute(null, Editor);

    private void OnZoomOut(object? sender, RoutedEventArgs e) => EditorCommands.ZoomOut.Execute(null, Editor);

    private void OnFitToScreen(object? sender, RoutedEventArgs e) => EditorCommands.FitToScreen.Execute(null, Editor);

    // One notch is a delta of 1, and the zoom buttons step by 2^(1/3), so a notch zooms one button step.
    private void OnMinimapWheel(object? sender, PointerWheelEventArgs e)
    {
        Editor.ZoomAtPosition(Math.Pow(2, e.Delta.Y / 3), new Rect(Editor.ViewportLocation, Editor.ViewportSize).Center);
        e.Handled = true;
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
