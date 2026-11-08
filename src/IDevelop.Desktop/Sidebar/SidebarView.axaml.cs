using Avalonia.Automation;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using IDevelop.Desktop.Canvas;

namespace IDevelop.Desktop.Sidebar;

/// <summary>
/// The left area, top to bottom: Find, the Activity list, the Projects section with the projects and their workflows, the Nodes
/// section with the shown workflow's nodes, and a footer with the agents' status and the theme (ADR 0071). Up, Down, Home, and
/// End move through its rows, Right opens a row, and Left closes it and then moves to its parent.
/// </summary>
public partial class SidebarView : UserControl
{
    // The room a revealed row keeps from the tree's bottom edge.
    private const double TreeBottomRoom = 32;

    // Below this panel height Activity starts folded, so the tree keeps the room.
    private const double ActivityFoldHeight = 700;

    // A tree that takes more of the panel than this folds Activity to its heading, so Projects and Nodes keep their rows.
    private const double ActivityFoldShare = 0.45;

    // The least height of the Nodes section, so a long list of projects never hides the shown workflow's nodes.
    private const double NodesLeastHeight = 120;

    // A workflow with no node keeps the Nodes heading and its one line, and Projects takes the rest (ADR 0072).
    private const double EmptyNodesHeight = 72;

    private IInputElement? _beforeFind;
    private MainWindowViewModel? _revealing;

    private WorkflowCanvasViewModel? _splitting;

    public SidebarView()
    {
        InitializeComponent();
        FindBox.AddHandler(KeyDownEvent, OnFindKeyDown, RoutingStrategies.Tunnel);
        // A click anywhere else leaves Find, as it leaves any floating list. A result takes no focus, so a click on one stays here.
        FindBox.LostFocus += (_, _) => ViewModel.Find.Clear();
        AddHandler(KeyDownEvent, OnRowKeyDown, RoutingStrategies.Tunnel);
        // A group header folds or opens its group, and the list never selects it.
        AddHandler(PointerPressedEvent, OnGroupPressed, RoutingStrategies.Tunnel);
        // Activity never takes more than half of the panel. A short panel, or a tree longer than ActivityFoldShare of it, folds Activity to its
        // heading, so the tree keeps the room while the person moves through a long workflow. Both measures are free of the fold: the rows' own heights leave out the tree's end room, which changes with the fold.
        Rows.SizeChanged += (_, e) =>
        {
            ActivityScroller.MaxHeight = e.NewSize.Height / 2;
            FoldActivity();
        };
        TreeRows.LayoutUpdated += (_, _) =>
        {
            FoldActivity();
            SplitSections();
        };
        Sections.SizeChanged += (_, _) => SplitSections();
        NodeRows.AddHandler(ScrollViewer.ScrollChangedEvent, (_, e) => OnNodesScrollChanged(e));
        // A rest moves the rows after the scroll's event, so the pin and the covers read the rows where they end up.
        NodeRows.LayoutUpdated += (_, _) =>
        {
            if (NodesScroller is { } scroller)
            {
                PinGroup(scroller);
            }

            CoverCutNodeRows();
        };
        DataContextChanged += (_, _) => WatchReveals();
    }

    // Projects takes its rows' height up to half of the sections' room, then scrolls, and Nodes takes the rest. With no workflow
    // shown, Projects takes all the room (ADR 0071).
    private void SplitSections()
    {
        var room = Sections.Bounds.Height - ProjectsHeading.Bounds.Height;
        Tree.MaxHeight = WholeRows(Math.Max(0, _revealing?.Canvas switch
        {
            null => room,
            { IsEmpty: true } => room - EmptyNodesHeight,
            _ => Math.Min(room / 2, room - NodesLeastHeight),
        }));
    }

    // A row that misses the limit by this little still shows whole, so the last few pixels of the section's room are not wasted.
    private const double WholeRowSlack = 6;

    // A section that scrolls ends on a whole row, so the row after it never shows cut or as a blank band. Nodes takes what is left over.
    private double WholeRows(double limit) => TreeRows.DesiredSize.Height <= limit ? limit
        : TreeRows.GetVisualDescendants().OfType<Control>().Where(IsTreeRow)
            .Select(row => TopOf(row) + row.Bounds.Height).Where(bottom => bottom <= limit + WholeRowSlack).DefaultIfEmpty(limit).Max();

    private void FoldActivity()
    {
        if (DataContext is MainWindowViewModel view && Rows.Bounds.Height > 0)
        {
            view.Activity.IsCompact = Rows.Bounds.Height < ActivityFoldHeight || TreeRows.Children.Sum(child => child.DesiredSize.Height) > Rows.Bounds.Height * ActivityFoldShare;
        }
    }

    // The tree follows the window: what the person opens anywhere, it opens to and shows.
    private void WatchReveals()
    {
        _revealing?.TreeReveal -= OnTreeReveal;
        _revealing?.PropertyChanged -= OnViewChanged;
        _revealing = DataContext as MainWindowViewModel;
        _revealing?.TreeReveal += OnTreeReveal;
        _revealing?.PropertyChanged += OnViewChanged;
        WatchCanvas();
    }

    private void OnViewChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.Canvas))
        {
            WatchCanvas();
        }
    }

    private void WatchCanvas()
    {
        _splitting?.PropertyChanged -= OnCanvasChanged;
        _splitting = _revealing?.Canvas;
        _splitting?.PropertyChanged += OnCanvasChanged;
        SplitSections();
    }

    private void OnCanvasChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkflowCanvasViewModel.IsEmpty))
        {
            SplitSections();
        }
    }

    private void OnTreeReveal(WorkflowCanvasViewModel canvas, TaskNodeViewModel? node) =>
        Dispatcher.UIThread.Post(() => Reveal(canvas, node), DispatcherPriority.Background);

    private MainWindow? Window => TopLevel.GetTopLevel(this) as MainWindow;

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    /// <summary>Puts the focus in Find, and remembers where Esc returns it.</summary>
    internal void FocusFind()
    {
        _beforeFind = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        ViewModel.ShowSidebar();
        Window?.UpdateLayout();
        FindBox.Focus();
        FindBox.SelectAll();
    }

    /// <summary>Puts the focus on the region's first control: Find, or the first row while Find hides.</summary>
    internal void FocusFirst()
    {
        if (FindBox.IsEffectivelyVisible)
        {
            FindBox.Focus();
        }
        else
        {
            this.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => button.IsEffectivelyVisible)?.Focus(NavigationMethod.Directional);
        }
    }

    private void OnFindKeyDown(object? sender, KeyEventArgs e)
    {
        var find = ViewModel.Find;
        switch (e.Key)
        {
            case Key.Down when find.IsActive:
                find.Move(1);
                FindResults.ScrollIntoView(find.Index);
                e.Handled = true;
                break;
            case Key.Up when find.IsActive:
                find.Move(-1);
                FindResults.ScrollIntoView(find.Index);
                e.Handled = true;
                break;
            case Key.Enter:
                e.Handled = Choose();
                break;
            case Key.Escape:
                find.Clear();
                (_beforeFind ?? Window?.CanvasFocusTarget)?.Focus();
                _beforeFind = null;
                e.Handled = true;
                break;
        }
    }

    private void OnResultTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not null)
        {
            Choose();
        }
    }

    // The chosen node's card takes the keys next, so Enter goes on to its conversation. The canvas of another workflow only takes
    // the keys after the next layout.
    private bool Choose()
    {
        if (ViewModel.Find.Choose() is null)
        {
            return false;
        }

        Window?.CanvasFocusTarget?.Focus(NavigationMethod.Tab);
        _beforeFind = null;
        Dispatcher.UIThread.Post(() => Window?.CanvasFocusTarget?.Focus(NavigationMethod.Tab), DispatcherPriority.Background);
        return true;
    }

    // A row of Projects clears the pinned row, and a row of Nodes or of the Activity list only scrolls its own list.
    private void InView(Control row)
    {
        if (!TreeRows.IsVisualAncestorOf(row))
        {
            row.BringIntoView();
            return;
        }

        ScrollInto(TopOf(row), TopOf(row) + row.Bounds.Height, PinnedRowHeight + 1);
    }

    // The tree moves only when the block is not in view, so a click on a row in view never moves that row. A block taller than
    // the view starts at the top edge.
    private void ScrollInto(double top, double bottom, double head)
    {
        var (from, height) = (Tree.Offset.Y, Tree.Viewport.Height);
        if (top < from + head || bottom - top + head >= height)
        {
            Tree.Offset = Tree.Offset.WithY(top - head);
        }
        else if (bottom > from + height)
        {
            Tree.Offset = Tree.Offset.WithY(bottom + TreeBottomRoom - height);
        }
    }

    // Projects shows the workflow's row, and Nodes shows the node's row.
    private void Reveal(WorkflowCanvasViewModel canvas, TaskNodeViewModel? node)
    {
        Window?.UpdateLayout();
        if (WorkflowRows().FirstOrDefault(row => row.DataContext == canvas) is { } row)
        {
            ScrollInto(TopOf(row), TopOf(row) + row.Bounds.Height, PinnedRowHeight + 1);
        }

        if (node is not null && NodeRows.DataContext == canvas && NodeRows.ItemsView.Contains(node))
        {
            NodeRows.ScrollIntoView(node);
        }
    }

    private IReadOnlyList<Control> KeyRows() => [.. Rows.GetVisualDescendants().OfType<Control>().Where(control => control.IsEffectivelyVisible && control switch
    {
        ListBoxItem item => item.FindAncestorOfType<ListBox>()?.Classes.Contains("tasks") == true,
        ToggleButton toggle => toggle.Classes.Contains("projectRow"),
        Button button => button.Classes.Contains("workflow") || button.Classes.Contains("activity"),
        _ => false,
    })];

    // A group header is a row that the arrow keys reach, but Tab goes on to the nodes, and the header names its group.
    private void OnTreeContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        var item = sender is ListBox list ? list.ItemsView[e.Index] : null;
        var header = item is NodeGroup;
        e.Container.Classes.Set("groupRow", header);
        KeyboardNavigation.SetIsTabStop(e.Container, !header);
        AutomationProperties.SetAutomationId(e.Container, header ? "NodeGroup" : null);
        if (header)
        {
            e.Container[!AutomationProperties.NameProperty] = new Avalonia.Data.Binding(nameof(NodeGroup.ToggleLabel));
        }
        else
        {
            e.Container.ClearValue(AutomationProperties.NameProperty);
        }

        // The canvas's selected node gets its row back when the person opens its group, and the row shows the selection.
        if (sender is ListBox { DataContext: WorkflowCanvasViewModel { SelectedNode: { } selected } } rows && rows.ItemsView[e.Index] == selected)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (rows.DataContext is WorkflowCanvasViewModel { SelectedNode: { } node } && node == selected && rows.SelectedItem != node && rows.ItemsView.Contains(node))
                {
                    rows.SetCurrentValue(SelectingItemsControl.SelectedItemProperty, node);
                }
            });
        }
    }

    // A group header folds or opens its group.
    private static NodeGroup? GroupOf(object? row) => row as NodeGroup;

    private static void OnGroupPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { } item
            && GroupOf(item.DataContext) is { } group
            && e.GetCurrentPoint(item).Properties.IsLeftButtonPressed)
        {
            group.Toggle();
            e.Handled = true;
        }
    }

    private void OnRowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || e.Source is TextBox || (e.Source as Visual)?.FindAncestorOfType<MenuItem>(includeSelf: true) is not null)
        {
            return;
        }

        var rows = KeyRows();
        var at = rows.ToList().FindIndex(row => row == e.Source || (e.Source is Visual source && row.IsVisualAncestorOf(source)));
        if (at < 0)
        {
            return;
        }

        var row = rows[at];
        if (e.Key is Key.Enter or Key.Space && GroupOf(row.DataContext) is { } group)
        {
            group.Toggle();
            e.Handled = true;
            return;
        }

        Control? next = e.Key switch
        {
            Key.Down => rows[Math.Min(at + 1, rows.Count - 1)],
            Key.Up => rows[Math.Max(at - 1, 0)],
            Key.Home => rows[0],
            Key.End => rows[^1],
            Key.Right => Open(row) ? row : rows[Math.Min(at + 1, rows.Count - 1)],
            Key.Left => Close(row) ? row : ParentRow(rows, at),
            _ => null,
        };
        if (next is null)
        {
            return;
        }

        e.Handled = true;
        next.Focus(NavigationMethod.Directional);
        InView(next);
        // Focus alone selects nothing, and moving through a node list chooses each node, as the list's own keys do.
        if (next is ListBoxItem { DataContext: TaskNodeViewModel node } item && item.FindAncestorOfType<ListBox>() is { } list)
        {
            list.SelectedItem = node;
        }
    }

    // Each returns whether the key changed the row rather than moved the focus.
    private static bool Open(Control row)
    {
        switch (row.DataContext)
        {
            case ProjectViewModel { IsExpanded: false } project:
                project.IsExpanded = true;
                return true;
            case NodeGroup { IsFolded: true } group:
                group.Toggle();
                return true;
            default:
                return false;
        }
    }

    private static bool Close(Control row)
    {
        switch (row.DataContext)
        {
            case ProjectViewModel { IsExpanded: true } project:
                project.IsExpanded = false;
                return true;
            case NodeGroup { IsFolded: false } group:
                group.Toggle();
                return true;
            default:
                return false;
        }
    }

    private static Control? ParentRow(IReadOnlyList<Control> rows, int at) => rows[at] switch
    {
        ListBoxItem { DataContext: TaskNodeViewModel node } => rows.Take(at).LastOrDefault(row => row is Button && row.DataContext == node.Canvas),
        ListBoxItem { DataContext: NodeGroup } item => item.FindAncestorOfType<ListBox>()?.DataContext is WorkflowCanvasViewModel canvas
            ? rows.Take(at).LastOrDefault(row => row is Button && row.DataContext == canvas) : rows[at],
        Button { DataContext: WorkflowCanvasViewModel canvas } => rows.Take(at).LastOrDefault(row => row.DataContext == canvas.Project),
        _ => rows[at],
    };

    private void OnTreeScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        PinRows();
        RoomAfterLastRow();
        var rows = ShownRows();
        RestOnWholeRow(Tree, rows, rows, PinnedHeight, e.OffsetDelta.Y);
        CoverCutRows();
    }

    private IEnumerable<ToggleButton> ProjectRows() => TreeRows.GetVisualDescendants().OfType<ToggleButton>().Where(row => row.Classes.Contains("projectRow") && row.IsEffectivelyVisible);

    private IEnumerable<Button> WorkflowRows() => TreeRows.GetVisualDescendants().OfType<Button>().Where(row => row.Classes.Contains("workflow") && row.IsEffectivelyVisible);

    private double TopOf(Visual row) => row.TranslatePoint(default, TreeRows)?.Y ?? 0;

    // The project whose rows meet the top edge stays pinned there.
    private void PinRows()
    {
        var top = Tree.Offset.Y;
        var project = top < 1 ? null : ProjectRows().LastOrDefault(row => TopOf(row) < top + 1);
        StickyProject.DataContext = project?.DataContext;
        StickyProject.IsVisible = project is not null;
        TreeHead.IsVisible = project is not null;
    }

    private const double PinnedRowHeight = 26;

    // The height of the pinned row that shows now.
    private double PinnedHeight => StickyProject.IsVisible ? PinnedRowHeight : 0;

    // Every row in the tree in view order, measured from the tree's top edge.
    private List<(double Top, double Bottom)> ShownRows() =>
        [.. TreeRows.GetVisualDescendants().OfType<Control>().Where(IsTreeRow)
            .Select(row => (Top: TopOf(row) - Tree.Offset.Y, Bottom: TopOf(row) - Tree.Offset.Y + row.Bounds.Height)).OrderBy(row => row.Top)];

    // The space under the last row without the room that RoomAfterLastRow adds.
    private const double TreeFootInset = 8;

    // The end of a list's range rests on a row's top too. A little room after the last row makes up what no whole row fills,
    // so a row that an edge would cut never hides as an empty band there. The room that the list has now does not count.
    private static double EndRoom(ScrollViewer scroller, double room, IEnumerable<double> tops, double head)
    {
        var end = scroller.Extent.Height - room - scroller.Viewport.Height;
        var rest = end <= 0 ? end : tops.Select(top => top - head).Where(top => top >= end - 0.5).DefaultIfEmpty(end).Min();
        return Math.Max(0, rest - end);
    }

    private void RoomAfterLastRow()
    {
        var room = TreeRows.Margin.Bottom - TreeFootInset;
        var wanted = EndRoom(Tree, room, TreeRows.GetVisualDescendants().OfType<Control>().Where(IsTreeRow).Select(row => TopOf(row)), PinnedRowHeight);
        if (Math.Abs(wanted - room) > 0.5)
        {
            TreeRows.Margin = new Thickness(0, 0, TreeRows.Margin.Right, TreeFootInset + wanted);
        }
    }

    // A list rests on one of its rests under its head. It goes on in the direction it moved, so a small step never goes back. A row
    // under the head that is no rest moves on at once, also after a change of height, which moves nothing.
    private static void RestOnWholeRow(ScrollViewer scroller, IReadOnlyList<(double Top, double Bottom)> rows, IReadOnlyList<(double Top, double Bottom)> rests, double head, double moved)
    {
        var cut = rows.Any(row => row.Top < head - 0.5 && row.Bottom > head + 0.5);
        var noRest = !cut && head > 0 && rows.FirstOrDefault(row => row.Bottom > head + 0.5) is { Bottom: > 0 } first && !rests.Contains(first);
        if (!noRest && (moved == 0 || !cut))
        {
            return;
        }

        var rest = moved >= 0 ? rests.FirstOrDefault(row => row.Top >= head - 0.5, (Top: head, Bottom: head)) : rests.LastOrDefault(row => row.Top < head - 0.5, (Top: head, Bottom: head));
        scroller.Offset = scroller.Offset.WithY(scroller.Offset.Y + rest.Top - head);
    }

    private ScrollViewer? NodesScroller => NodeRows.Scroll as ScrollViewer;

    private List<(double Top, double Bottom, bool Header)> NodeRowSpans() =>
        [.. NodeRows.GetVisualDescendants().OfType<ListBoxItem>().Where(row => row.IsEffectivelyVisible)
            .Select(row => (Top: row.TranslatePoint(default, NodeRows)?.Y ?? 0, row.Bounds.Height, Header: row.DataContext is NodeGroup))
            .Select(row => (row.Top, Bottom: row.Top + row.Height, row.Header)).OrderBy(row => row.Top)];

    private IReadOnlyList<(double Top, double Bottom)> ShownNodeRows() => [.. NodeRowSpans().Select(row => (row.Top, row.Bottom))];

    // The rows that the list rests on under the pinned group. A header after a node row is no rest: every row of the group above it
    // has gone, so that group's pin would stand over nothing. The list goes on to the header's first row, and the header takes the pin.
    private IReadOnlyList<(double Top, double Bottom)> NodeRests()
    {
        var spans = NodeRowSpans();
        return [.. spans.Where((row, at) => !row.Header || at == 0 || spans[at - 1].Header).Select(row => (row.Top, row.Bottom))];
    }

    private double PinnedGroupHeight => NodesHead.IsVisible ? PinnedRowHeight : 0;

    // The group whose rows meet the top edge stays pinned there. A header that has reached the pin's place takes it over.
    private void PinGroup(ScrollViewer scroller)
    {
        var group = scroller.Offset.Y < 1 ? null
            : NodeRows.GetVisualDescendants().OfType<ListBoxItem>().Where(row => row.IsEffectivelyVisible && row.DataContext is NodeGroup)
                .LastOrDefault(row => (row.TranslatePoint(default, NodeRows)?.Y ?? 0) < PinnedRowHeight - 0.5);
        if (StickyGroup.DataContext != group?.DataContext || NodesHead.IsVisible != group is not null)
        {
            StickyGroup.DataContext = group?.DataContext;
            NodesHead.IsVisible = group is not null;
        }
    }

    // A click on the pinned group folds it, as a click on its header does.
    private void OnStickyGroup(object? sender, RoutedEventArgs e) => (StickyGroup.DataContext as NodeGroup)?.Toggle();

    // The list brought the selected row to its top edge. The pinned group must not hide that row.
    private void KeepSelectedRowUnderPin(ScrollViewer scroller)
    {
        if (PinnedGroupHeight > 0 && NodeRows.SelectedItem is { } selected && NodeRows.ContainerFromItem(selected) is { } row
            && row.TranslatePoint(default, NodeRows)?.Y is { } top && top > -0.5 && top < PinnedGroupHeight - 0.5)
        {
            scroller.Offset = scroller.Offset.WithY(scroller.Offset.Y + top - PinnedGroupHeight);
        }
    }

    private void OnNodesScrollChanged(ScrollChangedEventArgs e)
    {
        if (NodesScroller is { } scroller)
        {
            PinGroup(scroller);
            var room = NodeRows.Padding.Bottom - TreeFootInset;
            var wanted = EndRoom(scroller, room, NodeRests().Select(row => row.Top + scroller.Offset.Y), PinnedRowHeight);
            if (Math.Abs(wanted - room) > 0.5)
            {
                NodeRows.Padding = new Thickness(0, 0, NodeRows.Padding.Right, TreeFootInset + wanted);
            }

            RestOnWholeRow(scroller, ShownNodeRows(), NodeRests(), PinnedGroupHeight, e.OffsetDelta.Y);
            if (e.OffsetDelta.Y < 0)
            {
                KeepSelectedRowUnderPin(scroller);
            }
        }

        CoverCutNodeRows();
    }

    // At the end of its range the tree cannot rest on a row, and a row that an edge would cut hides under a cover until it shows whole.
    private void CoverCutRows()
    {
        var (rows, head, height) = (ShownRows(), PinnedHeight, Tree.Viewport.Height);
        var foot = rows.Where(row => row.Top < height - 0.5 && row.Bottom > height + 0.5).Select(row => height - row.Top).DefaultIfEmpty(0).Max();
        TreeFootCover.Height = foot;
        TreeFootCover.Margin = new Thickness(0, 0, 8, 0);
        TreeFootCover.IsVisible = foot > 0;
        var top = rows.Where(row => row.Top < head - 0.5 && row.Bottom > head + 0.5).Select(row => row.Bottom - head).DefaultIfEmpty(0).Max();
        TreeHeadCover.Height = top;
        TreeHeadCover.Margin = new Thickness(0, head, 8, 0);
        TreeHeadCover.IsVisible = top > 0;
    }

    // The Nodes section's row that its top or bottom edge would cut hides under a cover, as the Projects section's does.
    private void CoverCutNodeRows()
    {
        var height = NodeRows.Bounds.Height;
        var rows = NodeRows.GetVisualDescendants().OfType<ListBoxItem>().Where(row => row.IsEffectivelyVisible)
            .Select(row => (Top: row.TranslatePoint(default, NodeRows)?.Y ?? 0, row.Bounds.Height)).ToList();
        var foot = rows.Where(row => row.Top < height - 0.5 && row.Top + row.Height > height + 0.5)
            .Select(row => height - row.Top).DefaultIfEmpty(0).Max();
        var head = PinnedGroupHeight;
        var top = rows.Where(row => row.Top < head - 0.5 && row.Top + row.Height > head + 0.5)
            .Select(row => row.Top + row.Height - head).DefaultIfEmpty(0).Max();
        if (NodesHeadCover.Height != top || NodesHeadCover.IsVisible != top > 0 || NodesHeadCover.Margin.Top != head)
        {
            NodesHeadCover.Height = top;
            NodesHeadCover.Margin = new Thickness(0, head, 8, 0);
            NodesHeadCover.IsVisible = top > 0;
        }

        if (NodesFootCover.Height != foot || NodesFootCover.IsVisible != foot > 0)
        {
            NodesFootCover.Height = foot;
            NodesFootCover.IsVisible = foot > 0;
        }
    }

    private static bool IsTreeRow(Control control) => control.IsEffectivelyVisible && control switch
    {
        ListBoxItem item => item.FindAncestorOfType<ListBox>()?.Classes.Contains("tasks") == true,
        ToggleButton toggle => toggle.Classes.Contains("projectRow"),
        Button button => button.Classes.Contains("workflow"),
        Grid grid => grid.Classes.Contains("unopenedRow"),
        _ => false,
    };

    private void ScrollToRow(Control? row)
    {
        if (row is not null)
        {
            Tree.Offset = Tree.Offset.WithY(TopOf(row));
        }
    }

    private void OnStickyProject(object? sender, RoutedEventArgs e) =>
        ScrollToRow(ProjectRows().FirstOrDefault(row => row.DataContext == StickyProject.DataContext));

    private void OnTitleBandPressed(object? sender, PointerPressedEventArgs e) => Window?.TitleBandPressed(e);

    // A click, a key, and UI Automation each check a segment their own way, so the checked segment is the choice.
    private void OnThemeChecked(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { IsChecked: true, Tag: ThemeVariant variant })
        {
            ((App)Application.Current!).Choose(variant);
        }
    }

    private void OnWorkflowClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is WorkflowCanvasViewModel canvas)
        {
            ViewModel.Select(canvas);
        }
    }

    private void OnWorkflowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e is { Key: Key.F2, KeyModifiers: KeyModifiers.None } && (sender as Control)?.DataContext is WorkflowCanvasViewModel canvas)
        {
            RenameWorkflow(canvas);
            e.Handled = true;
        }
    }

    private void OnRenameWorkflow(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is WorkflowCanvasViewModel canvas)
        {
            RenameWorkflow(canvas);
        }
    }

    // The renamed workflow is shown, so Undo takes the rename back.
    private void RenameWorkflow(WorkflowCanvasViewModel canvas)
    {
        ViewModel.Select(canvas);
        canvas.BeginRenameWorkflow();
    }

    private void OnNewWorkflow(object? sender, RoutedEventArgs e) => NewWorkflow((sender as Control)?.DataContext as ProjectViewModel);

    private void OnNewWorkflowHere(object? sender, RoutedEventArgs e) => NewWorkflow(ViewModel.Canvas?.Project);

    private void NewWorkflow(ProjectViewModel? project)
    {
        if (project is not null && ViewModel.NewWorkflow(project) is { } canvas)
        {
            Window?.UpdateLayout();
            this.GetVisualDescendants().OfType<Button>().FirstOrDefault(row => row.Classes.Contains("workflow") && row.DataContext == canvas)?.BringIntoView();
        }
    }

    private void OnForgetProject(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is UnopenedProject project)
        {
            ViewModel.Forget(project);
        }
    }

    private void OnCloseProject(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ProjectViewModel project)
        {
            Window?.CloseProject(project);
        }
    }

    private void OnOpenFolder(object? sender, RoutedEventArgs e) => Window?.OpenFolder();

    private void OnOpenRecent(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is RecentProject project)
        {
            ViewModel.Open(project.Folder);
        }
    }

    // The list also follows its canvas's selection, so only a change made while the list has focus is a choice in it.
    // The list chooses a node for the canvas. The canvas can select a node that has no row, in a large folded group, so the
    // list's own loss of its selection does not clear the canvas's selection.
    private void OnSidebarSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox { SelectedItem: TaskNodeViewModel task, DataContext: WorkflowCanvasViewModel canvas } list)
        {
            return;
        }

        canvas.SelectedNode = task;
        if (list.IsKeyboardFocusWithin)
        {
            Window?.BringIntoViewIfHidden(task);
        }
    }

    // Choosing the node that is already selected changes no selection, so a tap on a row and Space or Enter on the
    // focused row also count as choices.
    private void OnSidebarTapped(object? sender, TappedEventArgs e) => BringRowIntoView(e.Source);

    private void OnSidebarKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter)
        {
            BringRowIntoView(e.Source);
        }
    }

    // A tap between rows also reaches the list, and it chooses no node.
    private void BringRowIntoView(object? source)
    {
        if ((source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: TaskNodeViewModel task })
        {
            Window?.BringIntoViewIfHidden(task);
        }
    }
}
