using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Conversation;
using IDevelop.Execution;
using IDevelop.Projects;

namespace IDevelop.Desktop;

public partial class MainWindow : Window
{
    private bool _waitingForUser;
    private bool _closeConfirmed;

    /// <summary>For the XAML loader and the designer. Nothing refreshes its directory, which searches no folder, so every
    /// client stays Checking.</summary>
    public MainWindow() : this(new ClientDirectory(CommandResolver.Create([], [])))
    {
    }

    /// <param name="clients">The app's one directory. The app starts its first refresh.</param>
    public MainWindow(ClientDirectory clients)
    {
        Copy = text => Clipboard?.SetTextAsync(text) ?? Task.FromException(new InvalidOperationException("This window has no clipboard."));
        var app = Application.Current as App;
        ViewModel = new MainWindowViewModel(clients, text => Copy(text), app?.PersonalBlueprints, app?.SessionFile);
        InitializeComponent();
        DataContext = ViewModel;
        PickFolder = PickFolderWithStorageProvider;
        // A press or the focus in a workflow's task list shows that workflow first, so the list's choice selects a card
        // on the canvas that shows it.
        AddHandler(PointerPressedEvent, (_, e) => ShowWorkflowOf(e.Source), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(GotFocusEvent, (_, e) => ShowWorkflowOf(e.Source), RoutingStrategies.Bubble, handledEventsToo: true);
        ConversationLinkRouter.SetRouter(this, new ConversationLinkRouter(uri => Launcher.LaunchUriAsync(uri), text => Copy(text)));
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.DockedConversation) && (ViewModel.DockedConversation is not null) != _docked)
            {
                _docked = !_docked;
                SizeDock();
            }
        };
        _sidebarMax = Columns.ColumnDefinitions[0].MaxWidth;
        _inspectorMax = Columns.ColumnDefinitions[4].MaxWidth;
        Columns.SizeChanged += (_, _) => LimitPanels();
        Columns.ColumnDefinitions[0].PropertyChanged += OnPanelColumnChanged;
        Columns.ColumnDefinitions[4].PropertyChanged += OnPanelColumnChanged;
        CanvasArea.SizeChanged += (_, e) => OnCanvasAreaSized(e);
        MainArea.SizeChanged += (_, _) => LimitDock();
        DockedHost.AddHandler(ConversationView.RequiredHeightChangedEvent, (_, _) => LimitDock());
    }

    // The largest widths the sidebar's and the inspector's columns declare for themselves.
    private readonly double _sidebarMax;
    private readonly double _inspectorMax;

    private void OnPanelColumnChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ColumnDefinition.WidthProperty)
        {
            LimitPanels();
        }
    }

    // Each panel keeps the width it was given, which its column's largest width caps while the window is too narrow for it.
    private void LimitPanels()
    {
        var (sidebar, inspector) = (Columns.ColumnDefinitions[0], Columns.ColumnDefinitions[4]);
        var room = Columns.Bounds.Width - Columns.ColumnDefinitions[1].Width.Value - Columns.ColumnDefinitions[3].Width.Value - PanelWidths.CanvasMinWidth;
        if (room <= 0)
        {
            return;
        }

        var (sidebarMax, inspectorMax) = PanelWidths.Limits(room,
            new(sidebar.Width.Value, sidebar.MinWidth, _sidebarMax), new(inspector.Width.Value, inspector.MinWidth, _inspectorMax));
        sidebar.MaxWidth = sidebarMax;
        inspector.MaxWidth = inspectorMax;
    }

    // The dock opens at its default height, keeps the height the person drags it to while it stays open, and gives the
    // space back when it closes. It is never shorter than the conversation needs for its header, its composer, and a few
    // lines of transcript, and while it can be, it leaves the canvas above it CanvasMinHeight. Its splitter keeps to both.
    private const double DockHeight = 400;

    /// <summary>The canvas's least height above the docked conversation, while the conversation keeps what it needs.</summary>
    internal const double CanvasMinHeight = 280;

    private bool _docked;

    private void SizeDock()
    {
        var dock = MainArea.RowDefinitions[2];
        if (ViewModel.DockedConversation is null)
        {
            dock.MinHeight = 0;
            dock.MaxHeight = double.PositiveInfinity;
            dock.Height = GridLength.Auto;
            MainArea.RowDefinitions[0].Height = GridLength.Star;
        }
        else
        {
            dock.Height = new GridLength(DockHeight);
            LimitDock();
        }
    }

    private void LimitDock()
    {
        if (ViewModel.DockedConversation is null || MainArea.Bounds.Height <= 0)
        {
            return;
        }

        var dock = MainArea.RowDefinitions[2];
        // The dock's border adds its top line to what the conversation needs.
        var needed = DockedHost.Presenter?.Child is ConversationView { RequiredHeight: > 0 } view ? view.RequiredHeight + 1 : 160;
        dock.MinHeight = Math.Max(160, needed);
        dock.MaxHeight = Math.Max(dock.MinHeight, MainArea.Bounds.Height - MainArea.RowDefinitions[1].ActualHeight - CanvasMinHeight);
    }

    // When the canvas gets smaller under its floating controls while the conversation is docked, as when it docks, it
    // brings the cards that it cut or covered back into view, clear of the controls.
    private void OnCanvasAreaSized(SizeChangedEventArgs e)
    {
        TopBar.Classes.Set("compact", CanvasChrome.IsCompact(e.NewSize));
        if (ViewModel.DockedConversation is not null && (e.NewSize.Height < e.PreviousSize.Height || e.NewSize.Width < e.PreviousSize.Width))
        {
            Dispatcher.UIThread.Post(() => CanvasView?.FitIfHidden(), DispatcherPriority.Background);
        }
    }

    public MainWindowViewModel ViewModel { get; }

    /// <summary>Asks for a project folder and returns its path, or null when the user cancels.</summary>
    internal Func<Task<string?>> PickFolder { get; set; }

    /// <summary>Puts text on the clipboard.</summary>
    internal Func<string, Task> Copy { get; set; }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed)
        {
            return;
        }

        e.Cancel = true;
        if (_waitingForUser)
        {
            return;
        }

        _waitingForUser = true;
        CommitRenames();
        var ask = !ViewModel.ActiveRuns.IsEmpty || ViewModel.ActiveWorkflowRuns.Any() || ViewModel.Projects.Any(project => project.UnsavedDocuments.Any());
        if (ask && !await ConfirmLeaving([.. ViewModel.Projects], "leave"))
        {
            _waitingForUser = false;
            return;
        }

        // A stopping runner refuses every start, so nothing in the window takes a click or a key while the runs stop.
        ((Control)Content!).IsEnabled = false;
        var leaving = ViewModel.Leave();
        // With nothing to ask and nothing to stop, the window closes now.
        if (!ask && leaving.IsCompleted)
        {
            e.Cancel = false;
            return;
        }

        await leaving;
        _closeConfirmed = true;
        Close();
    }

    // Window key bindings run before the focused control sees a key, so they would take Ctrl+Z from a text box. A key
    // reaches this handler only after the focused control left it unhandled, so a text box keeps its own undo. The keys
    // are the platform's, the same a text box uses.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var keys = Application.Current?.PlatformSettings?.HotkeyConfiguration;
        var command = keys is null ? null
            : keys.Undo.Any(gesture => gesture.Matches(e)) ? ViewModel.UndoCommand
            : keys.Redo.Any(gesture => gesture.Matches(e)) ? ViewModel.RedoCommand
            : null;
        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
            e.Handled = true;
        }
    }

    private void OnTitleBandPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
        else
        {
            BeginMoveDrag(e);
        }
    }

    // A click, a key, and UI Automation each check a segment their own way, so the checked segment is the choice.
    private void OnThemeChecked(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { IsChecked: true, Tag: ThemeVariant variant })
        {
            ((App)Application.Current!).Choose(variant);
        }
    }

    private WorkflowCanvasView? CanvasView => CanvasHost.Presenter?.Child as WorkflowCanvasView;

    private void OnGenerate(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.Canvas?.ShowGenerate() is { } planner)
        {
            CanvasView?.BringIntoViewIfHidden(planner);
        }
    }

    private void OnAddNode(object? sender, RoutedEventArgs e) =>
        CanvasView?.OpenAddInView(AddNode.TranslatePoint(new Point(0, AddNode.Bounds.Height + 4), this) ?? default);

    // The list also follows its canvas's selection, so only a change made while the list has focus is a choice in it.
    private void OnSidebarSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { IsKeyboardFocusWithin: true, SelectedItem: TaskNodeViewModel task })
        {
            CanvasView?.BringIntoViewIfHidden(task);
        }
    }

    private void ShowWorkflowOf(object? source)
    {
        if ((source as Visual)?.FindAncestorOfType<ListBox>(includeSelf: true) is { DataContext: WorkflowCanvasViewModel canvas } list
            && list.Classes.Contains("tasks") && canvas != ViewModel.Canvas)
        {
            ViewModel.Select(canvas);
            // The new canvas view lays out now, so a card it brings into view is measured against its real size.
            UpdateLayout();
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
        if (e.Key == Key.F2 && e.KeyModifiers == KeyModifiers.None && (sender as Control)?.DataContext is WorkflowCanvasViewModel canvas)
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
            UpdateLayout();
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

    // Both questions come before either answer acts, and the project's runs stop only once it closes.
    private async void OnCloseProject(object? sender, RoutedEventArgs e)
    {
        if (_waitingForUser || (sender as Control)?.DataContext is not ProjectViewModel project)
        {
            return;
        }

        _waitingForUser = true;
        CommitRenames();
        if (await ConfirmLeaving([project], $"close {project.Name}"))
        {
            await ViewModel.Close(project);
        }

        _waitingForUser = false;
    }

    // Choosing the task that is already selected changes no selection, so a tap on a row and Space or Enter on the
    // focused row also count as choices.
    private void OnSidebarTapped(object? sender, TappedEventArgs e) => BringRowIntoView(e.Source);

    private void OnSidebarKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter)
        {
            BringRowIntoView(e.Source);
        }
    }

    // A tap between rows also reaches the list, and it chooses no task.
    private void BringRowIntoView(object? source)
    {
        if ((source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: TaskNodeViewModel task })
        {
            CanvasView?.BringIntoViewIfHidden(task);
        }
    }

    private void OnTreeScrollChanged(object? sender, ScrollChangedEventArgs e) =>
        TreeRows.Margin = new Thickness(0, 0, Tree.ScrollBarMaximum.Y > 0 ? 16 : 8, 0);

    // A question, the folder picker, or a project that is still closing holds a second open or close until it ends.
    private async void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        if (_waitingForUser)
        {
            return;
        }

        _waitingForUser = true;
        if (await PickFolder() is { } folder)
        {
            ViewModel.Open(folder);
        }

        _waitingForUser = false;
    }

    // Closing moves no focus, so a name typed into a rename box becomes an edit here, before the unsaved question.
    private void CommitRenames()
    {
        foreach (var box in this.GetVisualDescendants().OfType<WorkflowNameBox>())
        {
            box.Commit();
        }
    }

    /// <summary>
    /// Asks whether to stop the projects' running tasks and active workflow runs, then whether to save each project's
    /// unsaved workflows. Every
    /// question comes before any answer acts, so a Cancel at the last leaves every run going and saves nothing. The
    /// chosen saves then run, and a failed one keeps the projects open.
    /// </summary>
    /// <param name="leave">What the questions ask to do, such as "leave" or "close seed".</param>
    private async Task<bool> ConfirmLeaving(IReadOnlyList<ProjectViewModel> projects, string leave)
    {
        var tasks = projects.SelectMany(project => project.Runs.Active).ToList();
        var workflows = projects.SelectMany(project => project.ActiveWorkflowRuns).ToList();
        var question = (tasks, workflows) switch
        {
            ([], []) => null,
            ([var run], []) => $"\"{run.TaskTitle}\" is running. Stop it and {leave}?",
            (_, []) => $"{tasks.Count} tasks are running. Stop them and {leave}?",
            ([], [var workflow]) => $"A run of the \"{workflow.Name}\" workflow is active. Stop it and {leave}?",
            ([], _) => $"Runs of {workflows.Count} workflows are active. Stop them and {leave}?",
            _ => $"{Count(tasks.Count, "task")} and {Count(workflows.Count, "workflow run")} are running. Stop them and {leave}?",
        };
        if (question is not null
            && await new RunningTaskDialog(question).ShowDialog<RunningTaskChoice?>(this) != RunningTaskChoice.StopAndLeave)
        {
            return false;
        }

        List<WorkflowDocument> saves = [];
        foreach (var project in projects.Where(project => project.UnsavedDocuments.Any()))
        {
            switch (await new UnsavedChangesDialog(project.Name).ShowDialog<UnsavedChangesChoice?>(this))
            {
                case UnsavedChangesChoice.Save:
                    saves.AddRange(project.UnsavedDocuments);
                    break;
                case UnsavedChangesChoice.Discard:
                    break;
                default:
                    return false;
            }
        }

        return saves.Count == 0 || ViewModel.TrySave(saves);
    }

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private async Task<string?> PickFolderWithStorageProvider()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Open project folder" });
        return folders is [var folder] ? folder.TryGetLocalPath() : null;
    }
}
