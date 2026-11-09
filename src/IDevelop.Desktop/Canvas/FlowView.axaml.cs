using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JetBrains.Annotations;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// Flow: the Map's read-only view, agents in rows by graph depth inside a <see cref="ScrollViewer"/>. Every chip's place comes from
/// <see cref="FlowLayout"/>, so nothing can overlap. It has no dragging and no connecting.
/// </summary>
public partial class FlowView : UserControl
{
    [UsedImplicitly]
    public FlowView()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Bubble);
        AddHandler(PointerReleasedEvent, OnReleased, RoutingStrategies.Bubble);
        AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        PointerExited += (_, _) => Model?.Hover(null);
        DoubleTapped += OnDoubleTapped;
        SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width > 0)
            {
                Model?.Measure(e.NewSize.Width);
                Surface.MinHeight = e.NewSize.Height;
                OnShown();
            }
        };
    }

    private FlowViewModel? Model => DataContext as FlowViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Bounds.Width > 0)
        {
            Model?.Measure(Bounds.Width);
        }
    }

    /// <summary>Flow opens scrolled to the first stage that is not all Done. Each workflow's Flow does it once.</summary>
    internal void OnShown()
    {
        if (Model is { Canvas: { FlowOpened: false, IsFlow: true } canvas } && Bounds.Width > 0)
        {
            canvas.FlowOpened = true;
            Dispatcher.UIThread.Post(ScrollToRunFront, DispatcherPriority.Background);
        }
    }

    private void ScrollToRunFront()
    {
        if (Model is not { } flow || flow.Canvas.Stages.Count == 0 || flow.BandOf(flow.Canvas.Stages[flow.RunFront()]) is not { } band)
        {
            return;
        }

        Scroller.Offset = new Vector(0, Math.Max(0, band.Y - FlowLayout.SideMargin));
    }

    /// <summary>F: the density that shows the most stages, then the run front.</summary>
    internal void Fit()
    {
        Model?.Fit(Scroller.Viewport.Height);
        Dispatcher.UIThread.Post(ScrollToRunFront, DispatcherPriority.Background);
    }

    /// <summary>Opens the node's stage and scrolls its card into view, leaving a card that shows whole where it is.</summary>
    internal void Reveal(TaskNodeViewModel node)
    {
        if (Model is not { } flow)
        {
            return;
        }

        flow.StageOf(node)?.Open();
        Dispatcher.UIThread.Post(() =>
        {
            if (flow.CardOf(node) is { } card)
            {
                const double Margin = 8;
                var (view, offset) = (Scroller.Viewport.Height, Scroller.Offset.Y);
                var (top, bottom) = (card.Y - FlowLayout.HeaderHeight - Margin, card.Y + card.Height + Margin);
                var y = top < offset ? top : bottom > offset + view ? bottom - view : offset;
                Scroller.Offset = new Vector(0, Math.Max(0, y));
            }
        }, DispatcherPriority.Background);
    }

    private static Control? CardControl(object? source) =>
        (source as Control)?.GetSelfAndVisualAncestors().OfType<Control>().FirstOrDefault(control => control.Classes.Contains("flowCard"));

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Model is not { Canvas: var canvas })
        {
            return;
        }

        Focus();
        if (CardControl(e.Source) is { DataContext: FlowCardViewModel { Node: { IsRenaming: false } node } })
        {
            if (!node.IsSelected)
            {
                canvas.Select(node);
            }

            e.Handled = !e.GetCurrentPoint(this).Properties.IsRightButtonPressed;
        }
        else if ((e.Source as Control)?.FindAncestorOfType<Button>(includeSelf: true) is null)
        {
            canvas.ClearSelection();
        }
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Right && CardControl(e.Source) is { } card && this.FindAncestorOfType<WorkflowCanvasView>() is { } view)
        {
            view.OpenFlowMenu(card);
            e.Handled = true;
        }
    }

    private void OnMoved(object? sender, PointerEventArgs e) =>
        Model?.Hover((CardControl(e.Source)?.DataContext as FlowCardViewModel)?.Node);

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (CardControl(e.Source) is { DataContext: FlowCardViewModel { Node: { HasAgent: true, IsRenaming: false } node } })
        {
            node.OpenConversationCommand.Execute(null);
            e.Handled = true;
        }
    }
}
