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
/// The left area, top to bottom: Find, the Activity list, the tree of projects, workflows, and nodes, and a footer with the
/// agents' status and the theme. Up, Down, Home, and End move through its rows, Right opens a row, and Left closes it and
/// then moves to its parent.
/// </summary>
public partial class SidebarView : UserControl
{
    // The room a revealed row keeps from the tree's bottom edge.
    private const double TreeBottomRoom = 32;

    // Below this panel height Activity starts folded, so the tree keeps the room.
    private const double ActivityFoldHeight = 700;

    private IInputElement? _beforeFind;
    private MainWindowViewModel? _revealing;

    public SidebarView()
    {
        InitializeComponent();
        FindBox.AddHandler(KeyDownEvent, OnFindKeyDown, RoutingStrategies.Tunnel);
        // A click anywhere else leaves Find, as it leaves any floating list. A result takes no focus, so a click on one stays here.
        FindBox.LostFocus += (_, _) => ViewModel.Find.Clear();
        AddHandler(KeyDownEvent, OnRowKeyDown, RoutingStrategies.Tunnel);
        // Activity never takes more than half of the panel. A short panel, or a tree longer than half of it, folds Activity to its
        // heading, so the tree keeps the room while the person moves through a long workflow. Both measures are free of the fold.
        Rows.SizeChanged += (_, e) =>
        {
            ActivityScroller.MaxHeight = e.NewSize.Height / 2;
            FoldActivity();
        };
        TreeRows.LayoutUpdated += (_, _) => FoldActivity();
        DataContextChanged += (_, _) => WatchReveals();
    }

    private void FoldActivity()
    {
        if (DataContext is MainWindowViewModel view && Rows.Bounds.Height > 0)
        {
            view.Activity.IsCompact = Rows.Bounds.Height < ActivityFoldHeight || TreeRows.DesiredSize.Height > Rows.Bounds.Height / 2;
        }
    }

    // The tree follows the window: what the person opens anywhere, it opens to and shows.
    private void WatchReveals()
    {
        _revealing?.TreeReveal -= OnTreeReveal;
        _revealing = DataContext as MainWindowViewModel;
        _revealing?.TreeReveal += OnTreeReveal;
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

    // A row in the tree clears the pinned rows, and a row of the Activity list only scrolls its own list.
    private void InView(Control row)
    {
        if (!TreeRows.IsVisualAncestorOf(row))
        {
            row.BringIntoView();
            return;
        }

        var head = row is ListBoxItem ? 2 * PinnedRowHeight + 1 : PinnedRowHeight + 1;
        ScrollInto(TopOf(row), TopOf(row) + row.Bounds.Height, head);
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

    // A workflow shows from its row to the end of its list, and a node shows its own row.
    private void Reveal(WorkflowCanvasViewModel canvas, TaskNodeViewModel? node)
    {
        Window?.UpdateLayout();
        if (node is not null && TreeRows.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault(row => row.DataContext == node) is { IsEffectivelyVisible: true } item)
        {
            InView(item);
        }
        else if (WorkflowRows().FirstOrDefault(row => row.DataContext == canvas) is { } row)
        {
            ScrollInto(TopOf(row), ListBottom(row), PinnedRowHeight + 1);
        }
    }

    private IReadOnlyList<Control> KeyRows() => [.. Rows.GetVisualDescendants().OfType<Control>().Where(control => control.IsEffectivelyVisible && control switch
    {
        ListBoxItem item => item.FindAncestorOfType<ListBox>()?.Classes.Contains("tasks") == true,
        ToggleButton toggle => toggle.Classes.Contains("projectRow"),
        Button button => button.Classes.Contains("workflow") || button.Classes.Contains("activity"),
        _ => false,
    })];

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
            case WorkflowCanvasViewModel { IsExpanded: false } canvas when row is Button:
                canvas.IsExpanded = true;
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
            case WorkflowCanvasViewModel { IsExpanded: true } canvas when row is Button:
                canvas.IsExpanded = false;
                return true;
            default:
                return false;
        }
    }

    private static Control? ParentRow(IReadOnlyList<Control> rows, int at) => rows[at] switch
    {
        ListBoxItem { DataContext: TaskNodeViewModel node } => rows.Take(at).LastOrDefault(row => row is Button && row.DataContext == node.Canvas),
        Button { DataContext: WorkflowCanvasViewModel canvas } => rows.Take(at).LastOrDefault(row => row.DataContext == canvas.Project),
        _ => rows[at],
    };

    private void OnTreeScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        PinRows();
        RestOnWholeRow(e.OffsetDelta.Y);
        CoverCutRows();
    }

    private IEnumerable<ToggleButton> ProjectRows() => TreeRows.GetVisualDescendants().OfType<ToggleButton>().Where(row => row.Classes.Contains("projectRow") && row.IsEffectivelyVisible);

    private IEnumerable<Button> WorkflowRows() => TreeRows.GetVisualDescendants().OfType<Button>().Where(row => row.Classes.Contains("workflow") && row.IsEffectivelyVisible);

    private double TopOf(Visual row) => row.TranslatePoint(default, TreeRows)?.Y ?? 0;

    // The project whose rows meet the top edge stays pinned there, and so does the open workflow whose nodes meet it.
    private void PinRows()
    {
        var top = Tree.Offset.Y;
        var project = top < 1 ? null : ProjectRows().LastOrDefault(row => TopOf(row) < top + 1);
        var workflow = project is null ? null : WorkflowRows().LastOrDefault(row => row.DataContext is WorkflowCanvasViewModel { IsExpanded: true } && TopOf(row) + row.Bounds.Height <= top + PinnedRowHeight + 1
            && ListBottom(row) > top + 2 * PinnedRowHeight && TopOf(row) >= TopOf(project));
        StickyProject.DataContext = project?.DataContext;
        StickyProject.IsVisible = project is not null;
        StickyWorkflow.DataContext = workflow?.DataContext;
        StickyWorkflow.IsVisible = workflow is not null;
        TreeHead.IsVisible = project is not null;
    }

    private const double PinnedRowHeight = 28;

    // The bottom of the workflow's list of nodes.
    private double ListBottom(Button row) =>
        row.FindAncestorOfType<StackPanel>() is { } block ? TopOf(block) + block.Bounds.Height : 0;

    // The height of the pinned rows that show now, with the hairline under them.
    private double PinnedHeight => (StickyProject.IsVisible ? PinnedRowHeight : 0) + (StickyWorkflow.IsVisible ? PinnedRowHeight : 0) + (TreeHead.IsVisible ? 1 : 0);

    // Every row in the tree in view order, measured from the tree's top edge.
    private List<(double Top, double Bottom)> ShownRows() =>
        [.. TreeRows.GetVisualDescendants().OfType<Control>().Where(IsTreeRow)
            .Select(row => (Top: TopOf(row) - Tree.Offset.Y, Bottom: TopOf(row) - Tree.Offset.Y + row.Bounds.Height)).OrderBy(row => row.Top)];

    // The tree rests on a row's top under the pinned rows. It goes on in the direction it moved, so a small step never goes back.
    private void RestOnWholeRow(double moved)
    {
        var (rows, head) = (ShownRows(), PinnedHeight);
        if (moved == 0 || !rows.Any(row => row.Top < head - 0.5 && row.Bottom > head + 0.5))
        {
            return;
        }

        var row = moved > 0 ? rows.FirstOrDefault(row => row.Top >= head - 0.5, (Top: head, Bottom: head)) : rows.Last(row => row.Top < head - 0.5);
        Tree.Offset = Tree.Offset.WithY(Tree.Offset.Y + row.Top - head);
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

    private static bool IsTreeRow(Control control) => control.IsEffectivelyVisible && control switch
    {
        ListBoxItem item => item.FindAncestorOfType<ListBox>()?.Classes.Contains("tasks") == true,
        ToggleButton toggle => toggle.Classes.Contains("projectRow"),
        Button button => button.Classes.Contains("workflow"),
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

    private void OnStickyWorkflow(object? sender, RoutedEventArgs e)
    {
        if (WorkflowRows().FirstOrDefault(row => row.DataContext == StickyWorkflow.DataContext) is { } row)
        {
            Tree.Offset = Tree.Offset.WithY(TopOf(row) - PinnedRowHeight);
        }
    }

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
    private void OnSidebarSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { IsKeyboardFocusWithin: true, SelectedItem: TaskNodeViewModel task })
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
