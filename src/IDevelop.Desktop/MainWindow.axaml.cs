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
using IDevelop.Desktop.Inspector;
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
        ConversationLinkRouter.SetRouter(this, new ConversationLinkRouter(uri => Launcher.LaunchUriAsync(uri), text => Copy(text)));
        ViewModel.OpenFolder = folder => Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder));
        ViewModel.OpenFile = file => Launcher.LaunchFileInfoAsync(new FileInfo(file));
        ViewModel.LayOut = UpdateLayout;
        ViewModel.NodeShown += BringIntoViewIfHidden;
        ViewModel.ComposerRequested += FocusComposer;
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainWindowViewModel.IsSidebarShown) or nameof(MainWindowViewModel.IsInspectorShown) or nameof(MainWindowViewModel.IsIndexFlyoutOpen))
            {
                ShowPanels();
            }
        };
        ViewModel.AgentPanel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AgentPanelViewModel.IsRoom))
            {
                PlaceRoom();
            }
        };
        InspectorFrame.SizeChanged += (_, e) => ViewModel.AgentPanel.Width = e.NewSize.Width;
        ViewModel.NextRequested += GoNext;
        ViewModel.FrameRunningRequested += () => CanvasView?.FrameRunning();
        // A click outside the flyout or Esc closes it. Anything that is not the flyout's own keeps working as it would.
        AddHandler(PointerPressedEvent, (_, e) => CloseFlyoutOnClickOutside(e.Source), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        Shell.SizeChanged += (_, e) => ApplyTier(e.NewSize.Width);
        Columns.ColumnDefinitions[0].PropertyChanged += OnPanelColumnChanged;
        Columns.ColumnDefinitions[4].PropertyChanged += OnPanelColumnChanged;
    }

    // The widths the Index and the Agent panel have now: their tier's, or what the person dragged them to since the tier began.
    private double _indexWidth = PanelWidths.SizesOf(WidthTier.Default).Index.Width;
    private double _panelWidth = PanelWidths.SizesOf(WidthTier.Default).Panel.Width;
    private WidthTier? _appliedTier;
    private bool _applyingWidths;

    // A panel that opens or closes keeps the selected card in view. A fitted view fits again by itself, and a view the person
    // moved otherwise stays.
    private void KeepSelectionInView() => Dispatcher.UIThread.Post(() =>
    {
        if (ViewModel.Canvas?.SelectedNode is { } node)
        {
            CanvasView?.BringIntoViewIfHidden(node);
        }
    }, DispatcherPriority.Background);

    // The sheets' shown state as the columns last laid it out.
    private (bool Sidebar, bool Inspector) _panelsShown = (true, true);

    // A tier gives the sheets their widths on start and when the window enters it. A dragged width stays until then.
    private void ApplyTier(double width)
    {
        var tier = PanelWidths.TierOf(width);
        ViewModel.IsMinimal = width < PanelWidths.MinimalFrom;
        if (tier != _appliedTier)
        {
            _appliedTier = tier;
            ViewModel.Tier = tier;
            (_indexWidth, _panelWidth) = (PanelWidths.SizesOf(tier).Index.Width, PanelWidths.SizesOf(tier).Panel.Width);
        }

        ShowPanels();
    }

    // The Index, or the Rail in its place, and the Agent panel take their tier's widths. The Rail stands flush at the window's left edge.
    private void ShowPanels()
    {
        var shown = (ViewModel.IsSidebarShown, ViewModel.IsInspectorShown);
        if (shown != _panelsShown)
        {
            _panelsShown = shown;
            KeepSelectionInView();
        }

        var (index, panel) = PanelWidths.SizesOf(ViewModel.Tier);
        var (indexColumn, panelGap, panelColumn) = (Columns.ColumnDefinitions[0], Columns.ColumnDefinitions[3], Columns.ColumnDefinitions[4]);
        _applyingWidths = true;
        if (ViewModel.IsSidebarShown)
        {
            indexColumn.MinWidth = 0;
            indexColumn.MaxWidth = index.Max;
            indexColumn.MinWidth = index.Min;
            indexColumn.Width = new GridLength(_indexWidth);
        }
        else
        {
            indexColumn.MinWidth = 0;
            indexColumn.MaxWidth = PanelWidths.RailWidth;
            indexColumn.Width = new GridLength(PanelWidths.RailWidth);
        }

        panelGap.Width = new GridLength(ViewModel.IsInspectorShown ? PanelWidths.SheetGap : 0);
        if (ViewModel.IsInspectorShown)
        {
            panelColumn.MinWidth = 0;
            panelColumn.MaxWidth = panel.Max;
            panelColumn.MinWidth = panel.Min;
            panelColumn.Width = new GridLength(_panelWidth);
        }
        else
        {
            panelColumn.MinWidth = 0;
            panelColumn.MaxWidth = 0;
            panelColumn.Width = new GridLength(0);
        }

        _applyingWidths = false;
        Columns.Margin = new Thickness(ViewModel.ShowsRail ? 0 : PanelWidths.SheetGap, 0, PanelWidths.SheetGap, PanelWidths.SheetGap);
        PlaceFlyout();
        LimitPanels();
    }

    // The Room takes the Map's column and the panel's. On the way back the selected card comes into view again.
    private void PlaceRoom()
    {
        var room = ViewModel.AgentPanel.IsRoom;
        Grid.SetColumn(InspectorFrame, room ? 2 : 4);
        Grid.SetColumnSpan(InspectorFrame, room ? 3 : 1);
        if (!room)
        {
            KeepSelectionInView();
        }
    }

    // The flyout spans the body beside the Rail and covers the Map, which it does not rearrange. Closed, the sheet returns to its column.
    private void PlaceFlyout()
    {
        var flyout = ViewModel.IsIndexFlyoutOpen && ViewModel.ShowsRail;
        Grid.SetColumnSpan(IndexSheet, flyout ? 5 : 1);
        IndexSheet.HorizontalAlignment = flyout ? Avalonia.Layout.HorizontalAlignment.Left : Avalonia.Layout.HorizontalAlignment.Stretch;
        IndexSheet.Width = flyout ? PanelWidths.FlyoutWidth : double.NaN;
        IndexSheet.Margin = flyout ? new Thickness(PanelWidths.RailWidth + PanelWidths.SheetGap, 0, 0, 0) : default;
    }

    private void OnPanelColumnChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != ColumnDefinition.WidthProperty || _applyingWidths)
        {
            return;
        }

        if (sender == Columns.ColumnDefinitions[0] && ViewModel.IsSidebarShown)
        {
            _indexWidth = Columns.ColumnDefinitions[0].Width.Value;
        }
        else if (sender == Columns.ColumnDefinitions[4] && ViewModel.IsInspectorShown)
        {
            _panelWidth = Columns.ColumnDefinitions[4].Width.Value;
        }

        LimitPanels();
    }

    // Each sheet keeps its width while the Map keeps its least width. Its column's largest width caps it, and the splitters keep to that.
    private void LimitPanels()
    {
        var (sidebar, inspector) = (Columns.ColumnDefinitions[0], Columns.ColumnDefinitions[4]);
        var body = Shell.Bounds.Width - Columns.Margin.Left - Columns.Margin.Right;
        var room = body - Columns.ColumnDefinitions[1].Width.Value - Columns.ColumnDefinitions[3].Width.Value - PanelWidths.CanvasMinWidth;
        if (room <= 0)
        {
            return;
        }

        var (index, panel) = PanelWidths.SizesOf(ViewModel.Tier);
        var indexSide = ViewModel.IsSidebarShown ? new PanelWidths.Side(sidebar.Width.Value, index.Min, index.Max) : new PanelWidths.Side(sidebar.Width.Value, sidebar.Width.Value, sidebar.Width.Value);
        var panelSide = ViewModel.IsInspectorShown ? new PanelWidths.Side(inspector.Width.Value, panel.Min, panel.Max) : new PanelWidths.Side(0, 0, 0);
        var (sidebarMax, inspectorMax) = PanelWidths.Limits(room, indexSide, panelSide);
        sidebar.MaxWidth = sidebarMax;
        inspector.MaxWidth = inspectorMax;
    }

    private void CloseFlyoutOnClickOutside(object? source)
    {
        if (ViewModel.IsIndexFlyoutOpen && source is Visual visual && !IndexSheet.IsVisualAncestorOf(visual) && visual != IndexSheet && !Rail.IsVisualAncestorOf(visual))
        {
            ViewModel.IsIndexFlyoutOpen = false;
        }
    }

    // Esc closes the flyout before any key handler below it acts on the key.
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e is { Key: Key.Escape, KeyModifiers: KeyModifiers.None } && ViewModel.IsIndexFlyoutOpen)
        {
            ViewModel.IsIndexFlyoutOpen = false;
            e.Handled = true;
        }
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

        // A key that nothing took on its way here, such as Esc with the focus in the Index, still leaves the Room.
        if (e is { Key: Key.Escape, KeyModifiers: KeyModifiers.None } && ViewModel.AgentPanel.IsRoom)
        {
            StepBack();
            return true;
        }

        if (CanvasKeys.ToggleInspector.Matches(e) || CanvasKeys.ToggleSidebar.Matches(e))
        {
            (e.KeyModifiers.HasFlag(KeyModifiers.Alt) ? ViewModel.ToggleInspectorCommand : ViewModel.ToggleSidebarCommand).Execute(null);
            return true;
        }

        if (CanvasKeys.Find.Matches(e) || CanvasKeys.FindAlso.Matches(e))
        {
            OpenFind();
            return true;
        }

        // The longer gesture comes first, because a gesture with Shift also holds the shorter one's key.
        if (CanvasKeys.NextFailed.Matches(e) || CanvasKeys.NextAttention.Matches(e))
        {
            GoNext(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? NodeState.Failed : NodeState.InputRequired);
            return true;
        }

        if (e is { Key: Key.F2, KeyModifiers: KeyModifiers.None } && ViewModel.Canvas is { SelectedNodes.Count: 0 } canvas && FocusManager?.GetFocusedElement() is not TextBox)
        {
            ViewModel.ShowSidebar();
            canvas.BeginRenameWorkflow();
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

        if (CanvasKeys.Page(e) is { } page && ViewModel.Canvas?.Inspected is TaskNodeViewModel node && AgentPanelViewModel.Has(node, page))
        {
            ViewModel.ShowInspector();
            ViewModel.AgentPanel.Show(page);
            if (ViewModel.AgentPanel is { IsChat: true, IsPinned: true })
            {
                FocusComposer();
            }

            return true;
        }

        return false;
    }

    // The next node's Chat page takes the keys, so a person can reply at once.
    private void GoNext(NodeState state) => ViewModel.ShowNext(state);

    /// <summary>Esc moves one distance back: from the Room to the panel of the same node, and from the panel to its card on the Map.</summary>
    internal void StepBack()
    {
        if (ViewModel.AgentPanel.IsRoom)
        {
            ViewModel.LeaveRoom();
            Dispatcher.UIThread.Post(FocusInspector, DispatcherPriority.Background);
        }
        else
        {
            CanvasView?.Editor.Focus(NavigationMethod.Directional);
        }
    }

    private ConversationView? ShownConversation => this.GetVisualDescendants().OfType<ConversationView>().FirstOrDefault(view => view.IsEffectivelyVisible);

    private void FocusComposer() => Dispatcher.UIThread.Post(() => ShownConversation?.FocusComposer(), DispatcherPriority.Background);

    // The panel's first control takes the focus: the first tab, or on a page without tabs its first button.
    private Control? FirstPanelControl() => InspectorPanel.GetVisualDescendants().OfType<Control>().FirstOrDefault(control =>
        control is Button or TextBox && control is { Focusable: true, IsEffectivelyVisible: true, IsEffectivelyEnabled: true });

    // The Chat page takes the keys in its composer, and any other page in its first control.
    private void FocusInspector()
    {
        if (ViewModel.AgentPanel.IsChat && ShownConversation is { } conversation)
        {
            conversation.FocusComposer();
        }
        else
        {
            FirstPanelControl()?.Focus(NavigationMethod.Directional);
        }
    }

    private bool FocusModelPicker()
    {
        ViewModel.ShowInspector();
        ViewModel.AgentPanel.Show(Inspector.AgentPage.Agent);
        UpdateLayout();
        var picker = InspectorPanel.GetVisualDescendants().OfType<ComboBox>().FirstOrDefault(box =>
            box.IsEffectivelyVisible && Avalonia.Automation.AutomationProperties.GetAutomationId(box) == "TaskModel");
        picker?.BringIntoView();
        return picker?.Focus(NavigationMethod.Tab) == true;
    }

    // F6 moves through the regions a person can see: the Index, the Map, and the Agent panel.
    private void MoveRegion(int step)
    {
        IEnumerable<(Control Region, Action Enter)> Regions()
        {
            if (ViewModel.IsSidebarShown)
            {
                yield return (Sidebar, Sidebar.FocusFirst);
            }
            else
            {
                yield return (Rail, () => Rail.GetVisualDescendants().OfType<Button>().FirstOrDefault()?.Focus(NavigationMethod.Directional));
            }

            if (CanvasView is { } canvas && !ViewModel.AgentPanel.IsRoom)
            {
                yield return (canvas, () => canvas.Editor.Focus(NavigationMethod.Directional));
            }

            if (ViewModel.IsInspectorShown)
            {
                yield return (InspectorPanel, FocusInspector);
            }
        }

        var regions = Regions().ToList();
        var focused = FocusManager?.GetFocusedElement() as Visual;
        var at = regions.FindIndex(region => focused is not null && (region.Region == focused || region.Region.IsVisualAncestorOf(focused)));
        regions[((at < 0 ? (step > 0 ? -1 : 0) : at) + step + regions.Count) % regions.Count].Enter();
    }

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

    /// <summary>Opens the Generate sheet from the Command Bar.</summary>
    internal void Generate()
    {
        if (ViewModel.Canvas?.ShowGenerate() is { } planner)
        {
            CanvasView?.BringIntoViewIfHidden(planner);
        }
    }

    /// <summary>Opens the Add popover under the Command Bar's button.</summary>
    internal void OpenAddAt(Button button) => CanvasView?.OpenAddInView(button.TranslatePoint(new Point(0, button.Bounds.Height + 4), this) ?? default);

    /// <summary>Opens the node's menu at its Index row.</summary>
    internal void OpenNodeMenu(Control row) => CanvasView?.OpenNodeMenu(row);

    /// <summary>Opens the Find palette and puts the focus in its field. A Rail's flyout closes first.</summary>
    internal void OpenFind()
    {
        ViewModel.IsIndexFlyoutOpen = false;
        Palette.Focus(FocusManager?.GetFocusedElement());
    }

    /// <summary>Brings a node's card into view on the canvas, if the canvas does not show it whole.</summary>
    internal void BringIntoViewIfHidden(TaskNodeViewModel node) => CanvasView?.BringIntoViewIfHidden(node);

    /// <summary>The control that takes the canvas's keys.</summary>
    internal IInputElement? CanvasFocusTarget => CanvasView?.Editor;

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
