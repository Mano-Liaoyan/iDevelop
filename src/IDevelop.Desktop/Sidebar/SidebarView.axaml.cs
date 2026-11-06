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
    // At or below this window height Activity shows fewer rows, and it never takes more than half of the panel.
    private const double SmallHeight = 700;

    // What the pinned project row and the bottom fade cover at the tree's edges.
    private const double TreeEdgeRoom = 32;

    private IInputElement? _beforeFind;

    public SidebarView()
    {
        InitializeComponent();
        FindBox.AddHandler(KeyDownEvent, OnFindKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnRowKeyDown, RoutingStrategies.Tunnel);
        Rows.SizeChanged += (_, e) =>
        {
            ActivityScroller.MaxHeight = e.NewSize.Height / 2;
            if (DataContext is MainWindowViewModel view)
            {
                view.Activity.Limit = e.NewSize.Height <= SmallHeight ? ActivityViewModel.SmallLimit : ActivityViewModel.NormalLimit;
            }
        };
    }

    private MainWindow? Window => TopLevel.GetTopLevel(this) as MainWindow;

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    /// <summary>Puts the focus in Find, and remembers where Esc returns it.</summary>
    internal void FocusFind()
    {
        _beforeFind = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
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

    // The chosen node's card takes the keys next, so Enter goes on to its conversation. The tree shows the row again after the
    // next layout, and the canvas of another workflow only takes the keys then too.
    private bool Choose()
    {
        if (ViewModel.Find.Choose() is not { } result)
        {
            return false;
        }

        Window?.CanvasFocusTarget?.Focus();
        _beforeFind = null;
        Dispatcher.UIThread.Post(() =>
        {
            Reveal(result);
            Window?.CanvasFocusTarget?.Focus();
        }, DispatcherPriority.Background);
        return true;
    }

    // The room over a row clears the pinned project, and the room under it clears the bottom edge's fade.
    private static void InView(Control row) => row.BringIntoView(new Rect(row.Bounds.Size).Inflate(new Thickness(0, TreeEdgeRoom)));

    private void Reveal(FindResult result)
    {
        object target = (object?)result.Node ?? result.Canvas;
        result.Canvas.Project.IsExpanded = true;
        if (Rows.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(row => row.DataContext == target && (row is ListBoxItem || (row is Button && row.Classes.Contains("workflow")))) is { } row)
        {
            InView(row);
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
        TreeRows.Margin = new Thickness(0, 0, Tree.ScrollBarMaximum.Y > 0 ? 16 : 8, 8);
        // The project whose rows meet the top edge stays pinned there.
        var top = Tree.Offset.Y;
        var pinned = top < 1 ? null : TreeRows.GetVisualDescendants().OfType<ToggleButton>()
            .Where(row => row.Classes.Contains("projectRow") && row.DataContext is ProjectViewModel)
            .LastOrDefault(row => row.TranslatePoint(default, TreeRows)?.Y < top + 1);
        StickyProject.DataContext = pinned?.DataContext;
        StickyProject.IsVisible = pinned is not null;
    }

    private void OnStickyProject(object? sender, RoutedEventArgs e)
    {
        var row = TreeRows.GetVisualDescendants().OfType<ToggleButton>()
            .FirstOrDefault(row => row.Classes.Contains("projectRow") && row.DataContext == StickyProject.DataContext);
        if (row?.TranslatePoint(default, TreeRows) is { } at)
        {
            Tree.Offset = Tree.Offset.WithY(at.Y);
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

    private void OnNewWorkflow(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ProjectViewModel project && ViewModel.NewWorkflow(project) is { } canvas)
        {
            Window?.UpdateLayout();
            this.GetVisualDescendants().OfType<Button>().FirstOrDefault(row => row.Classes.Contains("workflow") && row.DataContext == canvas)?.BringIntoView();
        }
    }

    // The header's New workflow adds to the project on show.
    private void OnNewWorkflowHere(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.Canvas?.Project is { } project)
        {
            ViewModel.NewWorkflow(project);
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
