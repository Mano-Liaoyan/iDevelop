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
/// The Index: its header with Projects and Inbox, one view that scrolls, and its footer. Projects has four levels: project, workflow,
/// stage, and agent. Up, Down, Home, and End move through the rows, Right opens a row, Left closes it and then moves to its parent,
/// and Ctrl+Tab changes the view.
/// </summary>
public partial class SidebarView : UserControl
{
    private MainWindowViewModel? _revealing;

    public SidebarView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnViewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnAgentRowEnter, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnRowKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => WatchReveals();
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

    private void OnFind(object? sender, RoutedEventArgs e) => Window?.OpenFind();

    /// <summary>Puts the focus on the current row: the selected agent, the current workflow, or the view's first row.</summary>
    internal void FocusFirst()
    {
        var rows = KeyRows();
        var row = rows.FirstOrDefault(row => row is Button { DataContext: TaskNodeViewModel { IsSelected: true } })
            ?? rows.FirstOrDefault(row => row is Button { DataContext: WorkflowCanvasViewModel { IsSelected: true } })
            ?? rows.FirstOrDefault();
        (row ?? this.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => button.IsEffectivelyVisible))?.Focus(NavigationMethod.Directional);
    }

    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.Control)
        {
            ViewModel.IsInboxView = !ViewModel.IsInboxView;
            e.Handled = true;
            Dispatcher.UIThread.Post(FocusFirst, DispatcherPriority.Background);
        }
    }

    // A workflow's row or an agent's row shows, once the rows that its stage opened exist.
    private void Reveal(WorkflowCanvasViewModel canvas, TaskNodeViewModel? node)
    {
        Window?.UpdateLayout();
        object target = (object?)node ?? canvas;
        TreeRows.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(row => row.DataContext == target && (row.Classes.Contains("agent") || row.Classes.Contains("workflow")))
            ?.BringIntoView();
    }

    private IReadOnlyList<Control> KeyRows() => [.. Rows.GetVisualDescendants().OfType<Control>().Where(control => control.IsEffectivelyVisible && IsKeyRow(control))];

    private static bool IsKeyRow(Control control) => control switch
    {
        ToggleButton toggle => toggle.Classes.Contains("projectRow"),
        Button button => button.Classes.Contains("workflow") || button.Classes.Contains("stage") || button.Classes.Contains("agent") || button.Classes.Contains("activity"),
        _ => false,
    };

    // Enter on an agent's row moves one distance closer, to its Chat page, and Shift+Enter opens its Room.
    private void OnAgentRowEnter(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.Source is TextBox || e.Source is not Control source || source.FindAncestorOfType<MenuItem>(includeSelf: true) is not null)
        {
            return;
        }

        var node = source.DataContext as TaskNodeViewModel;
        var target = node ?? (source.DataContext as ActivityRow)?.Node;
        if (e.KeyModifiers == KeyModifiers.Shift && target is not null)
        {
            target.OpenRoom();
            e.Handled = true;
        }
        else if (e.KeyModifiers == KeyModifiers.None && node is { HasAgent: true })
        {
            node.OpenConversationCommand.Execute(null);
            Window?.BringIntoViewIfHidden(node);
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
        next.BringIntoView();
        // Focus alone selects nothing, and moving through the agents of the shown workflow chooses each one.
        if (next is Button { DataContext: TaskNodeViewModel node } && node.Canvas == ViewModel.Canvas)
        {
            ViewModel.ShowNode(node);
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
            case WorkflowCanvasViewModel { IsExpanded: false } workflow:
                workflow.IsExpanded = true;
                return true;
            case WorkflowStage { IsFolded: true } stage:
                stage.ToggleFold();
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
            case WorkflowCanvasViewModel { IsExpanded: true } workflow:
                workflow.IsExpanded = false;
                return true;
            case WorkflowStage { IsFolded: false } stage:
                stage.ToggleFold();
                return true;
            default:
                return false;
        }
    }

    private static Control? ParentRow(IReadOnlyList<Control> rows, int at) => rows[at].DataContext switch
    {
        TaskNodeViewModel => rows.Take(at).LastOrDefault(row => row.DataContext is WorkflowStage),
        WorkflowStage => rows.Take(at).LastOrDefault(row => row.DataContext is WorkflowCanvasViewModel),
        WorkflowCanvasViewModel workflow => rows.Take(at).LastOrDefault(row => row.DataContext == workflow.Project),
        _ => rows[at],
    };

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

    private void OnStageClick(object? sender, RoutedEventArgs e) => ((sender as Control)?.DataContext as WorkflowStage)?.ToggleFold();

    private void OnAgentClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is TaskNodeViewModel node)
        {
            ViewModel.ShowNode(node);
        }
    }

    private void OnAgentDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is TaskNodeViewModel { HasAgent: true } node)
        {
            node.OpenConversationCommand.Execute(null);
            e.Handled = true;
        }
    }

    // The node's menu acts on the selection, so a right-click shows and selects the row's node first.
    private void OnAgentMenu(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is Control { DataContext: TaskNodeViewModel node } row)
        {
            ViewModel.ShowNode(node);
            Window?.UpdateLayout();
            Window?.OpenNodeMenu(row);
            e.Handled = true;
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
            ViewModel.IsInboxView = false;
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
}
