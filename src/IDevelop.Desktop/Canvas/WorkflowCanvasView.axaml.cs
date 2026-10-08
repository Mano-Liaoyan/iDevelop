using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using IDevelop.Workflows;
using Nodify;

namespace IDevelop.Desktop.Canvas;

public partial class WorkflowCanvasView : UserControl, ICanvasView
{
    // The run bar sits in the bottom row between the zoom controls and the minimap, as PlanWeave's does, when it fits
    // there. On a narrower canvas it sits above them.
    private static readonly Thickness BetweenCorners = new(54, 12, 224, 12);
    private static readonly Thickness AboveCorners = new(12, 12, 12, 174);
    private const double RunBarWidth = 380;

    /// <summary>
    /// The margin a fitted or first view keeps around its cards. The breadcrumb, the waiting pill, and Generate float
    /// over the canvas's top 48 px, so the top margin starts below them.
    /// </summary>
    private static readonly Thickness ViewInset = new(24, 72, 24, 24);

    private WorkflowCanvasViewModel? _viewModel;
    private Point? _windowAnchor;
    private bool _showingAdd;
    private bool _addOnRelease;

    static WorkflowCanvasView()
    {
        // NodifyAvalonia 6.6.0 refreshes a minimap item's layout only inside a DecoratorContainer,
        // so a moved task's item would keep its old place until the viewport changes.
        MinimapItem.LocationProperty.Changed.AddClassHandler<MinimapItem>((item, _) => (item.GetVisualParent() as Layoutable)?.InvalidateMeasure());
        // A right-button drag pans. A release less than 4 px from its press is a click, which opens the Add popover.
        NodifyEditor.HandleRightClickAfterPanningThreshold = 4;
    }

    /// <param name="canvas">
    /// The workflow this view will show. A new editor bound to a selection leaves it in disarray, so the view binds to an
    /// empty one and then selects again what the workflow had selected.
    /// </param>
    public WorkflowCanvasView(WorkflowCanvasViewModel? canvas) : this()
    {
        canvas?.HoldSelection();
    }

    public WorkflowCanvasView()
    {
        InitializeComponent();
        Editor.ContextRequested += OnCanvasContextRequested;
        // NodifyAvalonia 6.6.0 cards and connections handle pointer release without calling the base handler, so
        // Avalonia never raises ContextRequested for them and their menus open here.
        Editor.AddHandler(PointerReleasedEvent, OnRightReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        Editor.AddHandler(Connector.DisconnectEvent, OnPortDisconnect);
        Editor.DoubleTapped += OnDoubleTapped;
        Editor.KeyDown += OnEditorKeyDown;
        Editor.KeyUp += OnEditorKeyUp;
        AddPopover.Closed += OnAddClosed;
        // NodifyAvalonia 6.6.0 zooms the minimap by the wheel delta's length, which drops its direction and is
        // about 0.2% per notch, so the minimap's wheel zooms here before Nodify sees it.
        Minimap.AddHandler(PointerWheelChangedEvent, OnMinimapWheel, RoutingStrategies.Tunnel);
        SizeChanged += (_, e) => RunBars.Margin =
            e.NewSize.Width - BetweenCorners.Left - BetweenCorners.Right >= RunBarWidth ? BetweenCorners : AboveCorners;
    }

    /// <inheritdoc />
    public CanvasPoint Pointer => new(Editor.MouseLocation.X, Editor.MouseLocation.Y);

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

    /// <summary>Opens the Add popover for a node in view, with its top left at a point of the window.</summary>
    internal void OpenAddInView(Point anchorInWindow)
    {
        _windowAnchor = anchorInWindow;
        _viewModel?.OpenAdd(new AddTarget.InView());
    }

    /// <summary>Zooms and pans so every card fits inside the view's inset, within the editor's zoom limits.</summary>
    public void FitToView()
    {
        var extent = Editor.ItemsExtent;
        var room = new Rect(Editor.Bounds.Size).Deflate(ViewInset);
        if (extent.Width <= 0 || extent.Height <= 0 || room.Width <= 0 || room.Height <= 0)
        {
            return;
        }

        Editor.ViewportZoom = Math.Min(room.Width / extent.Width, room.Height / extent.Height);
        Editor.ViewportLocation = extent.Center - (Vector)room.Center / Editor.ViewportZoom;
    }

    public void ZoomToActual() => Editor.ZoomAtPosition(1 / Editor.ViewportZoom, new Rect(Editor.ViewportLocation, Editor.ViewportSize).Center);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is { } old)
        {
            old.PropertyChanged -= OnViewModelChanged;
            old.View = null;
        }

        _viewModel = DataContext as WorkflowCanvasViewModel;
        if (_viewModel is { } canvas)
        {
            canvas.View = this;
            canvas.PropertyChanged += OnViewModelChanged;
            StartBelowChrome(canvas);
            Editor.LayoutUpdated += RestoreSelection;
        }

        ShowAdd();
    }

    // The selection comes back once the editor has laid out its cards, as a selection made on the canvas would.
    private void RestoreSelection(object? sender, EventArgs e)
    {
        Editor.LayoutUpdated -= RestoreSelection;
        _viewModel?.RestoreSelection();
    }

    // The window builds a new view for each workflow it shows, so a view that leaves the window lets its canvas go.
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_viewModel is { } canvas && ReferenceEquals(canvas.View, this))
        {
            canvas.View = null;
        }
    }

    // A workflow first shows at the canvas origin. Where its top cards would sit under the floating breadcrumb, it shows
    // higher, so they start at the view's top inset as they do after Fit. Later it shows where it was left.
    private static void StartBelowChrome(WorkflowCanvasViewModel canvas)
    {
        if (canvas.IsPositioned)
        {
            return;
        }

        canvas.IsPositioned = true;
        if (canvas.Nodes.Count == 0)
        {
            return;
        }

        var top = canvas.Nodes.Min(node => node.Location.Y) - ViewInset.Top / canvas.ViewportZoom;
        if (top < 0)
        {
            canvas.ViewportLocation = new Point(0, top);
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkflowCanvasViewModel.AddNode))
        {
            ShowAdd();
        }
    }

    private void ShowAdd()
    {
        _showingAdd = true;
        try
        {
            if (_viewModel?.AddNode is not { } add)
            {
                if (AddPopover.IsOpen)
                {
                    AddPopover.IsOpen = false;
                    Editor.Focus();
                }

                return;
            }

            if (TopLevel.GetTopLevel(this) is not { } window)
            {
                return;
            }

            // The sidebar's popover may reach over the whole window. Every other one stays on the canvas.
            var (anchor, bounds) = add.Target is AddTarget.InView && _windowAnchor is { } windowAnchor
                ? (windowAnchor, new Rect(window.Bounds.Size))
                : (this.TranslatePoint(ToView(CanvasPointOf(add.Target)), window) ?? default, new Rect(this.TranslatePoint(default, window) ?? default, Bounds.Size));
            _windowAnchor = null;
            AddPopover.Show(add, this, anchor, bounds);
        }
        finally
        {
            _showingAdd = false;
        }
    }

    // A click outside closes the popover itself.
    private void OnAddClosed(object? sender, EventArgs e)
    {
        if (!_showingAdd)
        {
            _viewModel?.CloseAdd();
        }
    }

    private CanvasPoint CanvasPointOf(AddTarget target) => target switch
    {
        AddTarget.AtPoint at => at.Point,
        AddTarget.FromOutput from => from.Drop,
        AddTarget.ToInput to => to.Drop,
        AddTarget.Between between => between.Point,
        _ => new(Editor.ViewportLocation.X + 60, Editor.ViewportLocation.Y + 60),
    };

    private Point ToView(CanvasPoint point) =>
        Editor.TranslatePoint(new Point((point.X - Editor.ViewportLocation.X) * Editor.ViewportZoom, (point.Y - Editor.ViewportLocation.Y) * Editor.ViewportZoom), this)
        ?? default;

    /// <summary>The pointer's canvas point while it is over the canvas, else the view's center.</summary>
    private CanvasPoint PointerOrCenter()
    {
        var center = new Rect(Editor.ViewportLocation, Editor.ViewportSize).Center;
        return Editor.IsPointerOver ? Pointer : new CanvasPoint(center.X, center.Y);
    }

    private static ItemContainer? Card(object? source) => (source as Visual)?.FindAncestorOfType<ItemContainer>(includeSelf: true);

    private static bool OnConnection(object? source) => (source as Visual)?.FindAncestorOfType<BaseConnection>(includeSelf: true) is not null;

    // Nodify's commands route from the focused element, which a button beside the editor never reaches.
    private void OnZoomIn(object? sender, RoutedEventArgs e) => EditorCommands.ZoomIn.Execute(null, Editor);

    private void OnZoomOut(object? sender, RoutedEventArgs e) => EditorCommands.ZoomOut.Execute(null, Editor);

    private void OnFitToScreen(object? sender, RoutedEventArgs e) => FitToView();

    // One notch is a delta of 1, and the zoom buttons step by 2^(1/3), so a notch zooms one button step.
    private void OnMinimapWheel(object? sender, PointerWheelEventArgs e)
    {
        Editor.ZoomAtPosition(Math.Pow(2, e.Delta.Y / 3), new Rect(Editor.ViewportLocation, Editor.ViewportSize).Center);
        e.Handled = true;
    }

    private void OnCanvasContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (Card(e.Source) is not null || _viewModel is not { } canvas)
        {
            return;
        }

        e.Handled = true;
        if (canvas.IsEmptyAt(Pointer))
        {
            canvas.OpenAdd(new AddTarget.AtPoint(Pointer));
        }
    }

    private void OnRightReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right || _viewModel is not { } canvas)
        {
            return;
        }

        if (e.Source is BaseConnection { DataContext: ConnectionViewModel connection } line)
        {
            // The connection a right-click reaches is selected, as a card is, so its wider line shows what the menu acts on.
            canvas.SelectConnection(connection);
            Open("ConnectionMenu", canvas.ConnectionMenu(connection, Pointer), line);
            e.Handled = true;
        }
        else if (Card(e.Source) is { DataContext: TaskNodeViewModel } card)
        {
            // The card selects itself on this release before the event reaches the editor, so the menu acts on it.
            Open("NodeMenu", canvas.NodeMenu(), card);
            e.Handled = true;
        }
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel is { } canvas && Card(e.Source) is null && !OnConnection(e.Source) && canvas.IsEmptyAt(Pointer))
        {
            canvas.OpenAdd(new AddTarget.AtPoint(Pointer));
            e.Handled = true;
        }
    }

    // Alt+click on a port deletes the connections on that side of its node. Nodify's connector raises this event for
    // Alt+click. Its pointer gestures fire; only the editor's key gestures do not.
    private void OnPortDisconnect(object? sender, ConnectorEventArgs e)
    {
        if (_viewModel is { } canvas && e.Connector is PortViewModel port)
        {
            canvas.Disconnect(port);
            e.Handled = true;
        }
    }

    // Nodify's own key gestures never fire, so the canvas's shortcuts are read here. A box's keys stay its own.
    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is { } canvas && e.Source is not TextBox)
        {
            e.Handled = Shortcut(canvas, e.Key, e.KeyModifiers);
        }
    }

    // N opens the popover on its release, after its letter went to the canvas, so the search box never receives it.
    private void OnEditorKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.N && _addOnRelease && _viewModel is { } canvas)
        {
            _addOnRelease = false;
            canvas.OpenAdd(new AddTarget.AtPoint(PointerOrCenter()));
            e.Handled = true;
        }
    }

    private bool Shortcut(WorkflowCanvasViewModel canvas, Key key, KeyModifiers modifiers)
    {
        var plain = modifiers == KeyModifiers.None;
        var command = modifiers == CanvasKeys.Command;
        var single = canvas.SelectedNodes is [var one] ? one : null;
        switch (key)
        {
            case Key.N when plain:
                _addOnRelease = true;
                return true;
            case Key.Delete or Key.Back when plain:
                canvas.DeleteSelectionCommand.Execute(null);
                return true;
            case Key.D when command:
                canvas.Duplicate();
                return true;
            case Key.A when command:
                canvas.SelectAll();
                return true;
            case Key.Escape when plain:
                if (canvas.AddNode is not null)
                {
                    canvas.CloseAdd();
                }
                else
                {
                    canvas.ClearSelection();
                }

                return true;
            case Key.F2 when plain && single is not null:
                single.BeginRename();
                return true;
            case Key.Enter when command && single is not null:
                if (single.RunCommand.CanExecute(null))
                {
                    single.RunCommand.Execute(null);
                }

                return true;
            case Key.F when plain:
                FitToView();
                return true;
            case Key.OemPlus or Key.Add when (modifiers & ~KeyModifiers.Shift) == KeyModifiers.None:
                EditorCommands.ZoomIn.Execute(null, Editor);
                return true;
            case Key.OemMinus or Key.Subtract when plain:
                EditorCommands.ZoomOut.Execute(null, Editor);
                return true;
            case Key.D0 or Key.NumPad0 when plain:
                ZoomToActual();
                return true;
            default:
                return false;
        }
    }

    private void Open(string menu, object target, Control at)
    {
        var contextMenu = (ContextMenu)this.FindResource(menu)!;
        contextMenu.DataContext = target;
        contextMenu.Open(at);
    }
}
