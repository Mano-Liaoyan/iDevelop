using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia;
using IDevelop.Clients;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Conversation;
using IDevelop.Desktop.Mvvm;
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
        ViewModel.OpenFolder = folder => Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder));
        ViewModel.LayOut = UpdateLayout;
        ViewModel.NodeShown += BringIntoViewIfHidden;
        ViewModel.ComposerRequested += FocusComposer;
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.DockedConversation) && (ViewModel.DockedConversation is not null) != _docked)
            {
                _docked = !_docked;
                SizeDock();
            }
            else if (e.PropertyName is nameof(MainWindowViewModel.IsSidebarOpen) or nameof(MainWindowViewModel.IsInspectorOpen))
            {
                ShowPanels();
            }
        };
        _sidebarMax = Columns.ColumnDefinitions[0].MaxWidth;
        _inspectorMax = Columns.ColumnDefinitions[4].MaxWidth;
        _sidebarMin = Columns.ColumnDefinitions[0].MinWidth;
        _inspectorMin = Columns.ColumnDefinitions[4].MinWidth;
        Columns.SizeChanged += (_, e) =>
        {
            ViewModel.IsNarrow = e.NewSize.Width < PanelWidths.NarrowWindow;
            LimitPanels();
        };
        Columns.ColumnDefinitions[0].PropertyChanged += OnPanelColumnChanged;
        Columns.ColumnDefinitions[4].PropertyChanged += OnPanelColumnChanged;
        CanvasArea.SizeChanged += (_, e) => TopBar.Classes.Set("compact", CanvasChrome.IsCompact(e.NewSize));
        MainArea.SizeChanged += (_, _) => LimitDock();
        DockedHost.AddHandler(ConversationView.RequiredHeightChangedEvent, (_, _) => LimitDock());
    }

    // A tab sits over the canvas's bottom band, clear of the splitter's middle, where a person drags it.
    private const double TabLift = 72;

    /// <summary>The left panel's tab sits at the window's edge while the panel is closed.</summary>
    public static readonly IValueConverter LeftEdge = new FuncValueConverter<bool, Thickness>(open => open ? new Thickness(-6, 0, -6, TabLift) : new Thickness(6, 0, -6, TabLift));

    public static readonly IValueConverter RightEdge = new FuncValueConverter<bool, Thickness>(open => open ? new Thickness(-6, 0, -6, TabLift) : new Thickness(-6, 0, 6, TabLift));

    // The largest widths the sidebar's and the inspector's columns declare for themselves, and the widths and least widths they
    // go back to when they open.
    private readonly double _sidebarMax;
    private readonly double _inspectorMax;
    private readonly double _sidebarMin;
    private readonly double _inspectorMin;
    private double _sidebarWidth;
    private double _inspectorWidth;

    // A closed panel's column takes no width.
    private void ShowPanels()
    {
        Fold(Columns.ColumnDefinitions[0], ViewModel.IsSidebarOpen, ref _sidebarWidth, _sidebarMin, _sidebarMax);
        Fold(Columns.ColumnDefinitions[4], ViewModel.IsInspectorOpen, ref _inspectorWidth, _inspectorMin, _inspectorMax);
        LimitPanels();
    }

    private static void Fold(ColumnDefinition column, bool open, ref double width, double min, double max)
    {
        if (open && column.MaxWidth == 0)
        {
            column.MaxWidth = max;
            column.MinWidth = min;
            column.Width = new GridLength(width);
        }
        else if (!open && column.MaxWidth != 0)
        {
            width = column.Width.Value;
            column.MinWidth = 0;
            column.MaxWidth = 0;
            column.Width = new GridLength(0);
        }
    }

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

        var (sidebarMax, inspectorMax) = Columns.Bounds.Width < PanelWidths.NarrowWindow ? (sidebar.MinWidth, inspector.MinWidth) : PanelWidths.Limits(room,
            new(sidebar.Width.Value, sidebar.MinWidth, ViewModel.IsSidebarOpen ? _sidebarMax : 0), new(inspector.Width.Value, inspector.MinWidth, ViewModel.IsInspectorOpen ? _inspectorMax : 0));
        sidebar.MaxWidth = sidebarMax;
        inspector.MaxWidth = inspectorMax;
    }

    // The dock opens at its default height, keeps the height the person drags it to while it stays open, and gives the
    // space back when it closes. It is never shorter than the conversation needs for its header, its composer, and a few
    // lines of transcript, and while it can be, it leaves the canvas above it CanvasMinHeight. Its splitter keeps to both.
    private const double DockHeight = 400;

    // A taller window gives the dock more, so the transcript reads as more than a few lines.
    private const double TallDockHeight = 460;
    private const double TallWindow = 760;

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
            dock.Height = new GridLength(MainArea.Bounds.Height >= TallWindow ? TallDockHeight : DockHeight);
            LimitDock();
            // Once the dock has its height, the canvas above it moves its cards out from under its floating controls, if
            // the cards in view before would end up under them. Resizing later never moves the person's view.
            var shown = CanvasView?.CardsInView() ?? [];
            Dispatcher.UIThread.Post(() => CanvasView?.FitIfCovered(shown), DispatcherPriority.Background);
        }
    }

    private void LimitDock()
    {
        if (ViewModel.DockedConversation is null || MainArea.Bounds.Height <= 0)
        {
            return;
        }

        var dock = MainArea.RowDefinitions[2];
        if (MainArea.Bounds.Height < TallWindow && dock.Height.Value == TallDockHeight)
        {
            dock.Height = new GridLength(DockHeight);
        }

        // The dock's border adds its top line to what the conversation needs.
        var needed = DockedHost.Presenter?.Child is ConversationView { RequiredHeight: > 0 } view ? view.RequiredHeight + 1 : 160;
        dock.MinHeight = Math.Max(160, needed);
        dock.MaxHeight = Math.Max(dock.MinHeight, MainArea.Bounds.Height - MainArea.RowDefinitions[1].ActualHeight - CanvasMinHeight);
    }


    public MainWindowViewModel ViewModel { get; }

    /// <summary>Asks for a project folder and returns its path, or null when the user cancels.</summary>
    internal Func<Task<string?>> PickFolder { get; set; }

    /// <summary>Puts text on the clipboard.</summary>
    private Func<string, Task> Copy { get; }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        // The work up to the first question runs now, so it can still let the window close or keep it open.
        UiWork.Start(() => Leave(e), ShowFailure);
    }

    private async Task Leave(WindowClosingEventArgs e)
    {
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
        var ask = ViewModel.RunningNodes.Any() || ViewModel.Projects.Any(project => project.UnsavedDocuments.Any());
        if (ask && !await ConfirmLeaving([.. ViewModel.Projects], "leave"))
        {
            _waitingForUser = false;
            return;
        }

        // Nothing in the window takes a click or a key while the running nodes stop.
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
        else if (!e.Handled)
        {
            e.Handled = RunKey(e);
        }
    }

    // The window's key map. A modal sheet covers the window, so no key here acts under it.
    private bool RunKey(KeyEventArgs e)
    {
        if (ViewModel.Canvas?.Sheet is not null)
        {
            return false;
        }

        if (e is { Key: Key.F6 } && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift)
        {
            MoveRegion(e.KeyModifiers == KeyModifiers.Shift ? -1 : 1);
            return true;
        }

        if (CanvasKeys.ToggleInspector.Matches(e) || CanvasKeys.ToggleSidebar.Matches(e))
        {
            (e.KeyModifiers.HasFlag(KeyModifiers.Alt) ? ViewModel.ToggleInspectorCommand : ViewModel.ToggleSidebarCommand).Execute(null);
            return true;
        }

        if (CanvasKeys.Find.Matches(e))
        {
            Sidebar.FocusFind();
            return true;
        }

        // The longer gesture comes first, because a gesture with Shift also holds the shorter one's key.
        if (CanvasKeys.NextFailed.Matches(e) || CanvasKeys.NextAttention.Matches(e))
        {
            if (ViewModel.ShowNext(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? NodeState.Failed : NodeState.InputRequired) is not null)
            {
                FocusNode();
            }

            return true;
        }

        if (CanvasKeys.RunWorkflow.Matches(e))
        {
            if (ViewModel.Canvas?.RunWorkflowCommand is { } run && run.CanExecute(null))
            {
                run.Execute(null);
            }

            return true;
        }

        if (CanvasKeys.ModelPicker.Matches(e))
        {
            return FocusModelPicker();
        }

        if (CanvasKeys.FilterInspector.Matches(e) && InspectorPanel.IsKeyboardFocusWithin)
        {
            return InspectorFilter()?.Focus() == true;
        }

        return false;
    }

    // Enter on the canvas opens the node's conversation. An open conversation has followed the selection, so its composer
    // takes the keys instead.
    private void FocusNode()
    {
        if (ViewModel.Conversation is not null)
        {
            FocusComposer();
        }
        else
        {
            CanvasFocusTarget?.Focus();
        }
    }

    private ConversationView? ShownConversation => this.GetVisualDescendants().OfType<ConversationView>().FirstOrDefault(view => view.IsEffectivelyVisible);

    private void FocusComposer() => Dispatcher.UIThread.Post(() => ShownConversation?.FocusComposer(), DispatcherPriority.Background);

    private TextBox? InspectorFilter() => InspectorPanel.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(box =>
        box.IsEffectivelyVisible && Avalonia.Automation.AutomationProperties.GetAutomationId(box) == "InspectorFilter");

    // The filter shows while something is selected, and otherwise the panel's first control takes the focus.
    private void FocusInspector()
    {
        var target = InspectorFilter() ?? InspectorPanel.GetVisualDescendants().OfType<Control>().FirstOrDefault(control =>
            control is Button or TextBox && control is { Focusable: true, IsEffectivelyVisible: true, IsEffectivelyEnabled: true });
        target?.Focus(NavigationMethod.Directional);
    }

    private bool FocusModelPicker()
    {
        ViewModel.ShowInspector();
        UpdateLayout();
        var picker = InspectorPanel.GetVisualDescendants().OfType<ComboBox>().FirstOrDefault(box =>
            box.IsEffectivelyVisible && Avalonia.Automation.AutomationProperties.GetAutomationId(box) == "TaskModel");
        picker?.BringIntoView();
        return picker?.Focus(NavigationMethod.Tab) == true;
    }

    // F6 moves through the regions a person can see: the left area, the canvas, the conversation, and the inspector.
    private void MoveRegion(int step)
    {
        var conversation = ShownConversation;
        IEnumerable<(Control Region, Action Enter)> Regions()
        {
            if (ViewModel.IsSidebarOpen)
            {
                yield return (Sidebar, Sidebar.FocusFirst);
            }

            if (ViewModel.ShowsCanvas && CanvasView is { } canvas)
            {
                yield return (canvas, () => canvas.Editor.Focus(NavigationMethod.Directional));
            }

            if (conversation is not null)
            {
                yield return (conversation, conversation.FocusComposer);
            }

            if (ViewModel.IsInspectorOpen)
            {
                yield return (InspectorPanel, FocusInspector);
            }
        }

        var regions = Regions().ToList();
        var focused = FocusManager?.GetFocusedElement() as Visual;
        var at = regions.FindIndex(region => focused is not null && (region.Region == focused || region.Region.IsVisualAncestorOf(focused)));
        regions[((at < 0 ? (step > 0 ? -1 : 0) : at) + step + regions.Count) % regions.Count].Enter();
    }

    private void OnTitleBandPressed(object? sender, PointerPressedEventArgs e) => TitleBandPressed(e);

    /// <summary>A press on a band that stands in for the title bar moves the window, and a double press maximizes it.</summary>
    internal void TitleBandPressed(PointerPressedEventArgs e)
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

    private WorkflowCanvasView? CanvasView => CanvasHost.Presenter?.Child as WorkflowCanvasView;

    private void OnGenerate(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.Canvas?.ShowGenerate() is { } planner)
        {
            CanvasView?.BringIntoViewIfHidden(planner);
        }
    }

    private void OnAddNode(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            CanvasView?.OpenAddInView(button.TranslatePoint(new Point(0, button.Bounds.Height + 4), this) ?? default);
        }
    }

    /// <summary>Brings a node's card into view on the canvas, if the canvas does not show it whole.</summary>
    internal void BringIntoViewIfHidden(TaskNodeViewModel node) => CanvasView?.BringIntoViewIfHidden(node);

    /// <summary>The control that takes the canvas's keys, or the inspector's filter while a conversation covers the canvas.</summary>
    internal IInputElement? CanvasFocusTarget => ViewModel.ShowsCanvas ? CanvasView?.Editor : InspectorFilter();

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

    // Both questions come before either answer acts, and the project's runs stop only once it closes.
    internal void CloseProject(ProjectViewModel project) => UiWork.Start(() => CloseAsked(project), ShowFailure);

    private async Task CloseAsked(ProjectViewModel project)
    {
        if (_waitingForUser)
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

    // A question, the folder picker, or a project that is still closing holds a second open or close until it ends.
    private void OnOpenFolder(object? sender, RoutedEventArgs e) => OpenFolder();

    internal void OpenFolder() => UiWork.Start(AskForFolder, ShowFailure);

    private async Task AskForFolder()
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

    // A failed question, close, or open leaves the window usable, and the status line says what went wrong.
    private void ShowFailure(string text)
    {
        _waitingForUser = false;
        ((Control)Content!).IsEnabled = true;
        ViewModel.Status = text;
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
    /// Asks whether to stop the projects' running nodes, then whether to save each project's unsaved workflows. Every
    /// question comes before any answer acts, so a Cancel at the last leaves every node running and saves nothing. The
    /// chosen saves then run, and a failed one keeps the projects open.
    /// </summary>
    /// <param name="projects">The projects to leave, whose running nodes and unsaved workflows the questions name.</param>
    /// <param name="leave">What the questions ask to do, such as "leave" or "close seed".</param>
    private async Task<bool> ConfirmLeaving(IReadOnlyList<ProjectViewModel> projects, string leave)
    {
        var running = projects.SelectMany(project => project.RunningNodes).ToList();
        var question = running switch
        {
            [] => null,
            [var node] => $"\"{node.Title}\" is running. Stop it and {leave}?",
            _ => $"{running.Count} nodes are running. Stop them and {leave}?",
        };
        if (question is not null && await new RunningNodesDialog(question).ShowDialog<RunningNodesChoice?>(this) != RunningNodesChoice.StopAndLeave)
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

    private async Task<string?> PickFolderWithStorageProvider()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Open project folder" });
        return folders is [var folder] ? folder.TryGetLocalPath() : null;
    }
}
