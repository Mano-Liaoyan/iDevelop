using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using IDevelop.Workflows;
using JetBrains.Annotations;
using Nodify;

namespace IDevelop.Desktop.Canvas;

public partial class WorkflowCanvasView : UserControl, ICanvasView
{
    /// <summary>The canvas's top bar ends 40 px down, its 8 px inset and its 32 px controls, when the window has not laid it out.</summary>
    private const double TopBarBottom = 40;

    /// <summary>The bottom row ends this far from the canvas's edge: its 12 px inset and the 42 px run bar, which may show at any time.</summary>
    private const double BottomRowTop = 54;

    /// <summary>How far a fitted view keeps its cards from the canvas's edges and from what floats over it.</summary>
    private const double CardMargin = 24;

    /// <summary>How far apart the canvas's dots lie, in canvas units.</summary>
    private const double DotPitch = 24;

    /// <summary>The zoom at which a found card's title and neighbours read: a card shows its tile and two lines.</summary>
    private const double FoundZoom = 0.6;

    private WorkflowCanvasViewModel? _viewModel;
    private Point? _windowAnchor;
    private bool _showingAdd;
    private bool _addOnRelease;
    private IBrush? _dots;
    private ZoomTier _shownTier;

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

    [UsedImplicitly]
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
        _dots = Editor.Background;
        Editor.GotFocus += (_, e) => ShowKeyboardFocus(e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional);
        Editor.LostFocus += (_, _) => ShowKeyboardFocus(false);
        // A compact canvas shows its run bar's buttons as glyphs. A view that Fit made fits again at the new size, so a
        // resized panel or window never cuts the graph. A view that the person moved stays.
        SizeChanged += (_, e) =>
        {
            Classes.Set("compact", CanvasChrome.IsCompact(e.NewSize));
            Classes.Set("narrowBar", CanvasChrome.IsNarrowBar(e.NewSize));
            Classes.Set("shortBar", CanvasChrome.IsShortBar(e.NewSize));
            ShowChrome();
            if (IsFitted)
            {
                Dispatcher.UIThread.Post(FitToView, DispatcherPriority.Background);
            }
        };
        Editor.PropertyChanged += (_, e) =>
        {
            if (e.Property == NodifyEditor.ViewportLocationProperty || e.Property == NodifyEditor.ViewportZoomProperty || e.Property == NodifyEditor.ViewportSizeProperty)
            {
                ShowChrome();
                PlaceGhosts();
            }
        };
        ZoomOpen.IsCheckedChanged += (_, _) => ShowChrome();
        MinimapToggle.IsCheckedChanged += (_, _) => ShowChrome();
    }

    // The canvas's edge says the keys act here, and the selected card takes the same ring as every focused control.
    private void ShowKeyboardFocus(bool shown)
    {
        CanvasFocus.IsVisible = shown;
        Classes.Set("keyboardFocus", shown);
    }

    /// <summary>The tooltip of the run bar's Next, with its shortcut.</summary>
    public static string NextHelp => $"Next\nShow the next node that waits for you or failed\n{CanvasKeys.Hint(CanvasKeys.NextAttention)}: next that waits. {CanvasKeys.Hint(CanvasKeys.NextFailed)}: next that failed.";

    private bool IsFitted => _viewModel?.FittedView is { } fit && fit.Location == Editor.ViewportLocation && fit.Zoom == Editor.ViewportZoom;

    // The minimap and the zoom buttons show only while they are of use: while a card is outside the view, in a graph too large to
    // read at a fit, or on the person's choice.
    private void ShowChrome()
    {
        var hidden = Cards().Any(card => !new Rect(Editor.ViewportLocation, Editor.ViewportSize).Contains(card));
        var large = _viewModel?.Nodes.Count >= CanvasChrome.LargeGraph;
        MinimapPanel.IsVisible = CanvasChrome.ShowsMinimap(Bounds.Size) && (hidden || large || MinimapToggle.IsChecked == true);
        ZoomMore.IsVisible = hidden || ZoomOpen.IsChecked == true;
        ExpandAllSlot.IsVisible = CanvasChrome.ShowsMinimap(Bounds.Size);
    }

    // NodifyAvalonia 6.6.0 moves its decorators by the viewport's location before the zoom, where cards move after it, so at
    // a zoom other than 100% a ghost would leave the cards it belongs to. Each ghost moves back by the difference, in the
    // zoomed units of its own layer.
    private void PlaceGhosts()
    {
        var shift = (Vector)Editor.ViewportLocation * (1 - Editor.ViewportZoom) / Editor.ViewportZoom;
        foreach (var ghost in Editor.GetVisualDescendants().OfType<DecoratorContainer>())
        {
            ghost.RenderTransform = new TranslateTransform(shift.X, shift.Y);
        }
    }

    // A tier takes one class on the view, so only a threshold changes the cards' layout. Under a zoom of 0.5 the dots would turn
    // into a texture, so they spread to a four times wider grid, and the smallest cards have only the canvas's color.
    private void ShowTier()
    {
        if (_viewModel is not { Tier: var tier } || tier == _shownTier && Classes.Contains(ZoomTiers.StyleClass(tier) ?? "tier-full"))
        {
            return;
        }

        foreach (var known in Enum.GetValues<ZoomTier>())
        {
            Classes.Set(ZoomTiers.StyleClass(known) ?? "tier-full", known == tier);
        }

        _shownTier = tier;
        if (_dots is DrawingBrush dots)
        {
            var pitch = tier is ZoomTier.Full or ZoomTier.Compact ? DotPitch : DotPitch * 4;
            dots.DestinationRect = new RelativeRect(0, 0, pitch, pitch, RelativeUnit.Absolute);
        }

        Editor.Background = tier == ZoomTier.Block ? (IBrush?)this.FindResource("CanvasPlainBrush") : _dots;
    }

    /// <inheritdoc />
    public CanvasPoint Pointer => new(Editor.MouseLocation.X, Editor.MouseLocation.Y);

    /// <summary>
    /// Brings a card into the part of the view that no floating control covers, when it is not wholly there, so a card chosen
    /// in the left area or by a key is never under the top bar or the run bar.
    /// </summary>
    internal void BringIntoViewIfHidden(TaskNodeViewModel node)
    {
        var card = node.DisplayRect;
        var zoom = Editor.ViewportZoom;
        var room = new Rect(Editor.Bounds.Size).Deflate(FitInsets().First());
        var clear = new Rect(Editor.ViewportLocation + (Vector)room.Position / zoom, room.Size / zoom);
        if (!clear.Contains(card) || UnderMinimap(card))
        {
            // Nodify's animated pan moves the editor alone, so the view model, which places new tasks, would keep
            // the old location.
            Editor.ViewportLocation = card.Center - (Vector)room.Center / zoom;
        }
    }

    // Whether the card sits under the minimap, which floats over the bottom row's corner.
    private bool UnderMinimap(Rect card) => MinimapPanel.IsVisible && MinimapPanel.TranslatePoint(default, Editor) is { } at
        && new Rect(Editor.ViewportLocation + (Vector)at / Editor.ViewportZoom, MinimapPanel.Bounds.Size / Editor.ViewportZoom).Intersects(card);

    void ICanvasView.Reveal(TaskNodeViewModel node) => BringIntoViewIfHidden(node);

    /// <summary>Centres a card the person looked for and, at a zoom too low to read it, zooms in, so its title and its neighbours read.</summary>
    void ICanvasView.RevealFound(TaskNodeViewModel node)
    {
        if (Editor.ViewportZoom >= FoundZoom)
        {
            BringIntoViewIfHidden(node);
            return;
        }

        Editor.ViewportZoom = FoundZoom;
        var room = new Rect(Editor.Bounds.Size).Deflate(FitInsets().First());
        Editor.ViewportLocation = CanvasChrome.ViewOn(Extent(), room, FoundZoom, node.DisplayRect.Center);
    }

    /// <summary>Opens the Add popover for a node in view, with its top left at a point of the window.</summary>
    internal void OpenAddInView(Point anchorInWindow)
    {
        _windowAnchor = anchorInWindow;
        _viewModel?.OpenAdd(new AddTarget.InView());
    }

    /// <summary>
    /// Zooms and pans so every card, and every ghost card of an open proposal, fits clear of the controls that float along
    /// the canvas's top and bottom edges, never above 100%. A graph too large to read at that fit stops at
    /// <see cref="CanvasChrome.ReadableZoom"/> around the node that matters now. See <see cref="FitInsets"/>.
    /// </summary>
    public void FitToView()
    {
        if (Cards().Any())
        {
            Fit(Extent(), FocusPoint());
        }
    }

    private Rect Extent() => Cards().Aggregate((union, card) => union.Union(card));

    // The selected node, else a running node, else a node that needs the person, else the graph's start.
    private Point FocusPoint()
    {
        var nodes = _viewModel?.Nodes ?? [];
        var node = _viewModel?.SelectedNode ?? nodes.FirstOrDefault(node => node.State == NodeState.Running) ?? nodes.FirstOrDefault(node => node.HasAttention);
        return node?.DisplayRect.Center ?? Extent().TopLeft;
    }

    private void Fit(Rect extent, Point focus)
    {
        // A fit posted before the window showed another workflow finds this view gone, and its size no longer says anything.
        if (extent.Width <= 0 || extent.Height <= 0 || Editor.Bounds.Width <= 0 || !this.IsAttachedToVisualTree())
        {
            return;
        }

        // The margins give way when the cards would need less than the editor's least zoom to fit inside them, and the
        // controls' edges only when they leave no room at all.
        var insets = FitInsets().ToArray();
        for (var i = 0; i < insets.Length; i++)
        {
            var room = new Rect(Editor.Bounds.Size).Deflate(insets[i]);
            if (i < insets.Length - 1 && (room.Width < MinFitRoom || room.Height < MinFitRoom))
            {
                continue;
            }

            Editor.ViewportZoom = CanvasChrome.FitZoom(extent, room);
            Editor.ViewportLocation = CanvasChrome.ViewOn(extent, room, Editor.ViewportZoom, focus);
            if (_viewModel is { } canvas)
            {
                canvas.FittedView = (Editor.ViewportLocation, Editor.ViewportZoom);
            }

            return;
        }
    }

    /// <summary>
    /// The margins a fit tries in turn. First <see cref="CardMargin"/> clear of everything that floats along the canvas's
    /// top edge, which the window lays out over the canvas, and of the bottom row. Where that leaves too little room, as with a
    /// long status above the docked conversation, right against those controls, and with no room left at all, the whole
    /// canvas. The minimap does not count: it hides once every card fits, and a larger graph runs on under it.
    /// </summary>
    private IEnumerable<Thickness> FitInsets()
    {
        var top = TopLevel.GetTopLevel(this) is MainWindow { TopBar: { IsVisible: true } bar } && bar.TranslatePoint(new Point(0, bar.Bounds.Height), this) is { } below
            ? below.Y
            : TopBarBottom;
        var bars = RunBars.TranslatePoint(default, this) is { } run && RunBars.Bounds.Height > 0 ? Bounds.Height - run.Y : 0;
        var bottom = Math.Max(BottomRowTop, bars);
        yield return new Thickness(CardMargin, top + CardMargin, CardMargin, bottom + CardMargin);
        yield return new Thickness(CardMargin, top, CardMargin, bottom);
        yield return default;
    }

    // The least room a fit keeps its cards in before it gives up a margin.
    private const double MinFitRoom = 16;

    /// <summary>The boxes, in canvas units, of the cards and of an open proposal's ghost cards that show in the view now.</summary>
    internal Rect[] CardsInView()
    {
        var view = new Rect(Editor.ViewportLocation, Editor.ViewportSize);
        return [.. Cards().Where(view.Intersects)];
    }

    /// <summary>
    /// Fits the view when one of <paramref name="shown"/>, the cards that showed before the canvas got smaller, now sits under
    /// a control that floats over the canvas, such as the run bar. A card cut at the canvas's edge leaves the view as it is.
    /// </summary>
    internal void FitIfCovered(IReadOnlyCollection<Rect> shown)
    {
        if (shown.Count == 0 || TopLevel.GetTopLevel(this) is not MainWindow window || !IsEffectivelyVisible || Editor.ViewportZoom <= 0)
        {
            return;
        }

        // Each control's box in canvas units.
        Rect InCanvas(Visual control) => control.TranslatePoint(default, Editor) is { } at
            ? new Rect(Editor.ViewportLocation + (Vector)at / Editor.ViewportZoom, control.Bounds.Size / Editor.ViewportZoom)
            : default;
        var controls = window.TopBar.Children.Concat(BottomBar.Children).Concat(RunBars.Children)
            .Where(control => control.IsEffectivelyVisible && control != RunBars && control.Bounds.Width > 0)
            .Select(InCanvas).ToArray();
        if (shown.Any(card => controls.Any(control => control.Intersects(card))))
        {
            FitToView();
        }
    }

    /// <summary>The cards and an open proposal's ghost cards, in canvas units.</summary>
    private IEnumerable<Rect> Cards()
    {
        var size = new Size(WorkflowCanvasViewModel.TaskCardWidth, WorkflowCanvasViewModel.TaskCardHeight);
        return _viewModel is not { } canvas ? []
            // A card shows where the expanded cards' layout moves it, and as large as it shows (ADR 0024).
            : canvas.Nodes.Select(node => node.DisplayRect).Concat(canvas.Ghosts.OfType<GhostCardViewModel>().Select(ghost => new Rect(ghost.Location, size)));
    }

    // Frames the nodes as Fit frames the whole graph.
    private void Frame(IReadOnlyCollection<TaskNodeViewModel> nodes)
    {
        if (nodes.Count > 0)
        {
            var extent = nodes.Select(node => node.DisplayRect).Aggregate((union, rect) => union.Union(rect));
            Fit(extent, extent.Center);
        }
    }

    public void ZoomToActual() => Editor.ZoomAtPosition(1 / Editor.ViewportZoom, new Rect(Editor.ViewportLocation, Editor.ViewportSize).Center);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is { } old)
        {
            old.PropertyChanged -= OnViewModelChanged;
            old.Nodes.CollectionChanged -= OnNodesChanged;
            old.Ghosts.CollectionChanged -= OnGhostsChanged;
            old.View = null;
        }

        _viewModel = DataContext as WorkflowCanvasViewModel;
        if (_viewModel is { } canvas)
        {
            canvas.View = this;
            canvas.PropertyChanged += OnViewModelChanged;
            canvas.Nodes.CollectionChanged += OnNodesChanged;
            canvas.Ghosts.CollectionChanged += OnGhostsChanged;
            Editor.LayoutUpdated += RestoreSelection;
            Editor.LayoutUpdated += PositionOnce;
            ShowTier();
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

    // Opening a workflow fits it. Later it shows where it was left.
    private void PositionOnce(object? sender, EventArgs e)
    {
        if (_viewModel is not { } canvas || canvas.IsPositioned)
        {
            Editor.LayoutUpdated -= PositionOnce;
            ShowChrome();
            return;
        }

        if (Editor.Bounds.Width > 0)
        {
            Editor.LayoutUpdated -= PositionOnce;
            canvas.IsPositioned = true;
            FitToView();
            ShowChrome();
        }
    }

    private void OnNodesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => ShowChrome();

    private void OnGhostsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(PlaceGhosts, DispatcherPriority.Loaded);

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkflowCanvasViewModel.AddNode))
        {
            ShowAdd();
        }
        else if (e.PropertyName == nameof(WorkflowCanvasViewModel.Tier))
        {
            ShowTier();
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

    private void OnZoomActual(object? sender, RoutedEventArgs e) => ZoomToActual();

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
        // A key typed in a text box, or on an expanded card's answer box and pickers, stays with it (ADR 0024).
        if (_viewModel is { } canvas && e.Source is not TextBox && !(e.Source is Visual source && source.FindAncestorOfType<CardDetail>(includeSelf: true) is not null))
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
            case Key.Tab when plain || modifiers == KeyModifiers.Shift:
                return canvas.SelectInOutline(plain ? 1 : -1);
            case Key.Right when plain:
                return canvas.SelectConnected(CardStep.Successor);
            case Key.Left when plain:
                return canvas.SelectConnected(CardStep.Predecessor);
            case Key.Up when plain:
                return canvas.SelectConnected(CardStep.PreviousSibling);
            case Key.Down when plain:
                return canvas.SelectConnected(CardStep.NextSibling);
            case Key.Space when plain && single is not null:
                single.IsExpanded = !single.IsExpanded;
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
            // The window's Enter: an agent's conversation, with the focus in its composer. An approval's request is in the inspector.
            case Key.Enter when plain && single is { HasAgent: true }:
                single.OpenConversationCommand.Execute(null);
                return true;
            case Key.OemPeriod when plain && canvas.SelectedNodes.Count > 0:
                Frame(canvas.SelectedNodes);
                return true;
            case Key.OemPeriod when modifiers == KeyModifiers.Shift:
                Frame([.. canvas.Nodes.Where(node => node.State == NodeState.Running)]);
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
