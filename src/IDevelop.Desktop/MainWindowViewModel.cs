using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Logging;
using Avalonia.Threading;
using IDevelop.Clients;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Conversation;
using IDevelop.Desktop.Inspector;
using IDevelop.Desktop.Execution;
using IDevelop.Desktop.Mvvm;
using IDevelop.Desktop.Sidebar;
using IDevelop.Engine;
using IDevelop.Projects;

namespace IDevelop.Desktop;

/// <summary>
/// The window's open projects and which workflow it shows. Choosing a project or a workflow only changes what the
/// window shows. A project's running nodes stop only when the project or the window closes, after the person agreed.
/// </summary>
public sealed class MainWindowViewModel : ObservableObject
{
    private readonly ClientDirectory _clients;
    private readonly Func<string, Task> _copy;
    private readonly string? _personalBlueprints;
    private readonly string? _sessionFile;
    // The most folders the empty left area offers.
    private const int MaxRecent = 5;

    private bool? _inspectorChoice;

    // The node whose swapped-in inspector the person closed, so it stays closed until another node is selected.
    private TaskNodeViewModel? _swapClosedFor;

    private readonly RelayCommand _save;
    private readonly RelayCommand _undo;
    private readonly RelayCommand _redo;
    private readonly RelayCommand _refreshAgents;
    private bool _refreshingAgents;
    private bool _restoring;

    /// <summary>Opens a folder in the system's file manager, and returns whether it opened. The window sets it.</summary>
    internal Func<string, Task<bool>> OpenFolder { get; set; } = _ => Task.FromResult(false);

    /// <summary>Opens a file with the program that the system chooses.</summary>
    internal Func<string, Task<bool>> OpenFile { get; set; } = _ => Task.FromResult(false);

    /// <param name="clients">The coding-agent clients on this machine and what each offers.</param>
    /// <param name="copy">Puts text on the clipboard.</param>
    /// <param name="personalBlueprints">The personal blueprint library's folder, or null for none.</param>
    /// <param name="sessionFile">The file that remembers the open projects between runs, or null to remember nothing.</param>
    public MainWindowViewModel(ClientDirectory clients, Func<string, Task> copy, string? personalBlueprints = null, string? sessionFile = null)
    {
        _clients = clients;
        _copy = copy;
        _personalBlueprints = personalBlueprints;
        _sessionFile = sessionFile;
        _save = new RelayCommand(TrySave, () => EditableCanvas is not null);
        _undo = new RelayCommand(() => EditableCanvas?.Document.Undo(), () => EditableCanvas?.Document.CanUndo ?? false);
        _redo = new RelayCommand(() => EditableCanvas?.Document.Redo(), () => EditableCanvas?.Document.CanRedo ?? false);
        _refreshAgents = new RelayCommand(() => UiWork.Start(RefreshAgents, text => Status = text), () => !_refreshingAgents);
        AgentPanel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AgentPanelViewModel.ShowsChat))
            {
                SyncConversation();
            }
            else if (e.PropertyName == nameof(AgentPanelViewModel.IsRoom))
            {
                RaiseInspector();
            }
        };
        ToggleRoomCommand = new RelayCommand(ToggleRoom);
        Activity = new ActivityViewModel(() => Projects, Attend);
        Find = new FindViewModel(() => Projects, Choose);
        Projects.CollectionChanged += (_, _) => RaiseProjects();
        Recent.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRecent));
        ToggleSidebarCommand = new RelayCommand(ToggleSidebar);
        ToggleInspectorCommand = new RelayCommand(ToggleInspector);
        Unopened.CollectionChanged += (_, _) => RaiseProjects();
        clients.Changed += (_, _) => Dispatcher.UIThread.Post(OnClientsChanged);
    }

    public ObservableCollection<ProjectViewModel> Projects { get; } = [];

    /// <summary>The environment each engine's Git runs with, so tests can point Git at a configuration of their own.</summary>
    internal IReadOnlyDictionary<string, string> GitEnvironment { get; set; } = new Dictionary<string, string>();

    /// <summary>
    /// Remembered folders that this start could not open. Each stays remembered, so a later start opens it once it is back,
    /// until the person forgets it.
    /// </summary>
    public ObservableCollection<UnopenedProject> Unopened { get; } = [];

    /// <summary>Whether the left area has a project to list, open or not. Without one it shows the recent folders.</summary>
    public bool HasProjects => Projects.Count > 0 || Unopened.Count > 0;

    /// <summary>The folders the person closed, newest first, which the empty left area offers again.</summary>
    public ObservableCollection<RecentProject> Recent { get; } = [];

    public bool HasRecent => Recent.Count > 0;

    /// <summary>The left area is open. Find opens it.</summary>
    public bool IsSidebarOpen
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RaiseInspector();
            }
        }
    } = true;

    /// <summary>The Index is wide. Otherwise the Rail stands in its place.</summary>
    public bool IsSidebarShown => IsSidebarOpen && !(IsCompact && IsInspectorShown);

    /// <summary>The Rail stands in the Index's place.</summary>
    public bool ShowsRail => !IsSidebarShown;

    /// <summary>The window's width tier. The window applies it on start and on every resize.</summary>
    public WidthTier Tier
    {
        get;
        internal set
        {
            if (SetProperty(ref field, value))
            {
                // Each tier starts from its own layout, so a choice made for another width does not carry over.
                _inspectorChoice = null;
                _swapClosedFor = null;
                IsSidebarOpen = true;
                IsIndexFlyoutOpen = false;
                AgentPanel.IsCompactWindow = value == WidthTier.Compact;
                OnPropertyChanged(nameof(IsCompact));
                RaiseInspector();
            }
        }
    } = WidthTier.Default;

    /// <summary>The window is in the Compact tier: at most one of the Index and the Agent panel is wide.</summary>
    public bool IsCompact => Tier == WidthTier.Compact;

    /// <summary>The window is so narrow that the Command Bar's progress bar takes its shorter width.</summary>
    public bool IsMinimal
    {
        get;
        internal set => SetProperty(ref field, value);
    }

    /// <summary>The Rail's Index flyout is open over the Map. It closes on a choice, on Esc, or on a click outside it.</summary>
    public bool IsIndexFlyoutOpen
    {
        get;
        internal set => SetProperty(ref field, value);
    }

    /// <summary>The Agent panel is open: as the person last chose, or else in every tier but Compact, where it opens on demand. With no project it has nothing to show.</summary>
    public bool IsInspectorOpen => HasProjects && (_inspectorChoice ?? !IsCompact);

    /// <summary>
    /// In the Compact tier a selected node opens the Agent panel on demand, and the Index becomes the Rail. The Index toggle or
    /// Ctrl+Alt+B closes it for that node.
    /// </summary>
    public bool IsInspectorSwapped => HasProjects && IsCompact && _inspectorChoice is null
        && Canvas?.SelectedNode is { } node && node != _swapClosedFor;

    /// <summary>The Agent panel shows in its column.</summary>
    public bool IsInspectorShown => IsInspectorOpen || IsInspectorSwapped;

    /// <summary>A drag can resize the Index: it is wide, and the tier gives it a range.</summary>
    public bool CanResizeIndex => IsSidebarShown && !IsCompact;

    /// <summary>A drag can resize the Agent panel: it shows, and the tier gives it a range.</summary>
    public bool CanResizePanel => IsInspectorShown && !IsCompact && !AgentPanel.IsRoom;

    public string SidebarToggleLabel => IsSidebarShown ? "Show the Rail" : "Show the Index";

    public ICommand ToggleSidebarCommand { get; }

    public ICommand ToggleInspectorCommand { get; }

    /// <summary>The panel header's Room button: into the Room with the same page, or back to the panel.</summary>
    public ICommand ToggleRoomCommand { get; }

    /// <summary>Which page the Agent panel shows.</summary>
    public AgentPanelViewModel AgentPanel { get; } = new();

    /// <summary>The left area's list of nodes that wait, failed, or run, in every open project.</summary>
    public ActivityViewModel Activity { get; }

    /// <summary>The left area's Find box.</summary>
    public FindViewModel Find { get; }

    /// <summary>Lays the window out, so a canvas view that a switch brought in holds its selection before a node is selected
    /// in it. The window sets it.</summary>
    internal Action LayOut { get; set; } = () => { };

    /// <summary>A node was shown from outside its canvas, so the window brings its card into view.</summary>
    internal event Action<TaskNodeViewModel>? NodeShown;

    /// <summary>A workflow was shown or a node was chosen, so the left tree opens to its row and shows it.</summary>
    internal event Action<WorkflowCanvasViewModel, TaskNodeViewModel?>? TreeReveal;

    /// <summary>A Run Strip pill asked for the next node in a state, as Ctrl+J and Ctrl+Shift+J do.</summary>
    internal event Action<Canvas.NodeState>? NextRequested;

    /// <summary>A Run Strip pill asked to frame the running nodes, as Shift+Period does.</summary>
    internal event Action? FrameRunningRequested;

    /// <summary>The person asked for the conversation of a node that waits for their words, so the window puts the focus in its composer.</summary>
    internal event Action? ComposerRequested;

    /// <summary>The workflow the window shows, or null when no project is open.</summary>
    public WorkflowCanvasViewModel? Canvas
    {
        get;
        private set
        {
            var shown = field;
            if (SetProperty(ref field, value))
            {
                shown?.PropertyChanged -= OnCanvasChanged;
                value?.PropertyChanged += OnCanvasChanged;
                RaiseInspector();
            }
        }
    }

    private void OnCanvasChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkflowCanvasViewModel.SelectedNode))
        {
            if (Canvas?.SelectedNode is not null)
            {
                CloseIndexFlyout();
            }

            if (Canvas?.SelectedNode != _swapClosedFor)
            {
                _swapClosedFor = null;
            }

            RaiseInspector();
        }
    }

    /// <summary>The conversation that the Chat page shows while the Agent panel shows that page, or null.</summary>
    public ConversationViewModel? Conversation
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                AgentPanel.Conversation = value;
            }
        }
    }

    // The Generate sheet is modal, so a key or a button under its scrim neither saves nor takes an edit back.
    private WorkflowCanvasViewModel? EditableCanvas => Canvas is { Sheet: null } canvas ? canvas : null;

    public string? ProjectName => Canvas?.Project.Name;

    public string? WorkflowName => Canvas?.Name;

    public string Title => ProjectName is null ? "iDevelop" : $"{ProjectName}{(HasUnsavedChanges ? "*" : "")} - iDevelop";

    /// <summary>Whether the shown workflow has edits that are not saved.</summary>
    public bool HasUnsavedChanges => Canvas?.Document.HasUnsavedChanges ?? false;

    public string? Status
    {
        get;
        internal set => SetProperty(ref field, value);
    }

    public ICommand SaveCommand => _save;

    /// <summary>Takes back the shown workflow's latest edit. A run of typing in one box is one edit.</summary>
    public ICommand UndoCommand => _undo;

    public ICommand RedoCommand => _redo;

    public IReadOnlyList<AgentRow> Agents => [.. ClientCatalog.All.Select(id => new AgentRow(id, _clients.Current[id]))];

    /// <summary>"4 ready", "1 needs setup", or "Checking…".</summary>
    public string AgentsSummary => RunText.AgentsSummary([.. Agents.Select(agent => agent.Status)]);

    /// <summary>The worst of the agents' tones, which the footer's one dot shows.</summary>
    public StatusTone AgentsTone => new[] { StatusTone.Problem, StatusTone.Waiting, StatusTone.Running, StatusTone.Complete }
        .FirstOrDefault(tone => Agents.Any(agent => agent.Tone == tone), StatusTone.Neutral);

    /// <summary>The clients with a problem, which show over the left area's footer until the problem clears.</summary>
    public IReadOnlyList<AgentRow> AgentsNeedingSetup => [.. Agents.Where(agent => agent.Tone == StatusTone.Problem)];

    public bool HasAgentsNeedingSetup => AgentsNeedingSetup.Count > 0;

    /// <summary>Probes every client again. Each row keeps its last status until its new answer arrives.</summary>
    public ICommand RefreshAgentsCommand => _refreshAgents;

    /// <summary>The nodes that run now in every open project.</summary>
    internal IEnumerable<TaskNodeViewModel> RunningNodes => Projects.SelectMany(project => project.RunningNodes);

    /// <summary>
    /// Adds the folder as a project and shows its first workflow. A folder that is already open is shown instead. A folder
    /// that fails to open leaves every open project as it was.
    /// </summary>
    public void Open(string folder)
    {
        if (TryAdd(folder, $"Couldn't open {folder}", out var notices) is not { } project)
        {
            Status = notices;
            return;
        }

        if (Canvas?.Project != project)
        {
            Select(project.Workflows[0]);
        }

        Status = notices;
        Persist();
    }

    public void Select(WorkflowCanvasViewModel canvas)
    {
        CloseIndexFlyout();
        if (canvas == Canvas)
        {
            return;
        }

        Show(canvas);
        OpenShownNodes(canvas);
        Persist();
    }

    // The Projects section opens to the shown workflow's row and shows it, and the Nodes section lists its nodes (ADR 0071).
    private void OpenShownNodes(WorkflowCanvasViewModel canvas)
    {
        canvas.Project.IsExpanded = true;
        TreeReveal?.Invoke(canvas, null);
    }

    /// <summary>Shows the node's workflow and selects the node, as a choice from outside its canvas.</summary>
    internal void ShowNode(TaskNodeViewModel node)
    {
        CloseIndexFlyout();
        if (node.Canvas != Canvas)
        {
            Select(node.Canvas);
            LayOut();
        }

        node.Canvas.Project.IsExpanded = true;
        // Showing a node is a request for its row, so its group opens even when it is large.
        node.Canvas.Groups.First(group => group.Kind == NodeGroup.Of(node)).Open();
        node.Canvas.Inspect(node);
        NodeShown?.Invoke(node);
        TreeReveal?.Invoke(node.Canvas, node);
    }

    /// <summary>Shows the node and what it needs on its Chat page, with the keys in the composer.</summary>
    internal void Attend(TaskNodeViewModel node)
    {
        ShowNode(node);
        OpenChat(new ConversationTarget(node.Canvas, node.Id));
    }

    /// <summary>Selects the next node that waits or failed, after the selected one, in the Activity list's order.</summary>
    /// <summary>Opens the inspector for what the person asked to see in it.</summary>
    internal void ShowInspector() => ChooseInspector(true);

    // Closing the Agent panel ends its on-demand opening for this node and leaves the person's other choices as they were.
    private void ToggleInspector()
    {
        AgentPanel.IsRoom = false;
        if (IsInspectorSwapped)
        {
            _swapClosedFor = Canvas?.SelectedNode;
            RaiseInspector();
        }
        else
        {
            ChooseInspector(!IsInspectorOpen);
        }
    }

    /// <summary>Shows the Index: wide if the tier allows it, and otherwise as the Rail's flyout.</summary>
    internal void ShowSidebar()
    {
        if (IsSidebarShown)
        {
            return;
        }

        if (IsCompact && IsInspectorShown)
        {
            IsIndexFlyoutOpen = true;
        }
        else
        {
            IsSidebarOpen = true;
        }
    }

    // In the Compact tier the wide Index replaces the Agent panel, so the toggle closes the panel first.
    private void ToggleSidebar()
    {
        IsIndexFlyoutOpen = false;
        if (IsCompact && IsInspectorShown)
        {
            if (IsInspectorSwapped)
            {
                _swapClosedFor = Canvas?.SelectedNode;
            }
            else
            {
                _inspectorChoice = false;
            }

            IsSidebarOpen = true;
            RaiseInspector();
        }
        else
        {
            IsSidebarOpen = !IsSidebarOpen;
        }
    }

    /// <summary>Opens the Rail's Index flyout, or closes it when it is open.</summary>
    internal void ToggleIndexFlyout() => IsIndexFlyoutOpen = !IsIndexFlyoutOpen;

    /// <summary>The Rail's Inbox: the flyout, with the list of what needs the person open.</summary>
    internal void OpenInbox()
    {
        Activity.IsOpen = true;
        IsIndexFlyoutOpen = true;
    }

    /// <summary>A person's choice in the flyout closes it.</summary>
    private void CloseIndexFlyout() => IsIndexFlyoutOpen = false;

    private void ChooseInspector(bool open)
    {
        _inspectorChoice = open;
        RaiseInspector();
    }

    private void RaiseProjects()
    {
        OnPropertyChanged(nameof(HasProjects));
        RaiseInspector();
    }

    private void RaiseInspector()
    {
        OnPropertyChanged(nameof(IsInspectorOpen));
        OnPropertyChanged(nameof(IsInspectorSwapped));
        OnPropertyChanged(nameof(IsInspectorShown));
        OnPropertyChanged(nameof(IsSidebarShown));
        OnPropertyChanged(nameof(ShowsRail));
        OnPropertyChanged(nameof(CanResizeIndex));
        OnPropertyChanged(nameof(CanResizePanel));
        OnPropertyChanged(nameof(SidebarToggleLabel));
        SyncConversation();
    }

    private void Remember(string folder)
    {
        foreach (var known in Recent.Where(project => ProjectFolders.Comparer.Equals(project.Folder, folder)).ToList())
        {
            Recent.Remove(known);
        }

        Recent.Insert(0, new RecentProject(folder));
        while (Recent.Count > MaxRecent)
        {
            Recent.RemoveAt(Recent.Count - 1);
        }
    }

    internal TaskNodeViewModel? ShowNext(Canvas.NodeState state)
    {
        var nodes = Activity.InState(state).ToList();
        if (nodes.Count == 0)
        {
            return null;
        }

        var after = Canvas?.SelectedNode is { } selected ? nodes.IndexOf(selected) : -1;
        var next = nodes[(after + 1) % nodes.Count];
        Attend(next);
        return next;
    }

    private void Choose(FindResult result)
    {
        if (result.Node is { } node)
        {
            ShowNode(node);
            node.Canvas.ShowFound(node);
        }
        else
        {
            Select(result.Canvas);
        }
    }

    /// <summary>Adds an unsaved, empty workflow to the project, named "Workflow N", shows it, and returns it.</summary>
    public WorkflowCanvasViewModel? NewWorkflow(ProjectViewModel project)
    {
        WorkflowDocument document;
        try
        {
            document = WorkflowDocument.Create(project.Folder, project.NextWorkflowName());
        }
        catch (ProjectException e)
        {
            Status = e.Message;
            return null;
        }

        var canvas = project.Add(document);
        Select(canvas);
        return canvas;
    }

    /// <summary>Opens the node's Chat page, in its workflow, with the panel shown and the keys in the composer.</summary>
    private void OpenChat(ConversationTarget target)
    {
        if (target.Canvas != Canvas)
        {
            Select(target.Canvas);
        }

        if (target.Canvas.Nodes.FirstOrDefault(node => node.Id == target.Task) is { } found)
        {
            // The open node's row shows in the tree: its group opens and lists all its rows (ADR 0070).
            target.Canvas.Groups.First(group => group.Kind == NodeGroup.Of(found)).Open();
            if (target.Canvas.SelectedNode != found)
            {
                target.Canvas.Inspect(found);
            }
        }

        RevealPanel();
        AgentPanel.Show(AgentPage.Chat);
        SyncConversation();
        ComposerRequested?.Invoke();
    }

    // The node's Chat page and then the Room around it, so a Room opens where Enter would have gone.
    private void OpenRoom(ConversationTarget target)
    {
        OpenChat(target);
        AgentPanel.IsRoom = Canvas?.Inspected is TaskNodeViewModel;
    }

    /// <summary>Goes back to the panel, with the same node selected.</summary>
    internal void LeaveRoom() => AgentPanel.IsRoom = false;

    private void ToggleRoom() => AgentPanel.IsRoom = !AgentPanel.IsRoom && Canvas?.Inspected is TaskNodeViewModel;

    // The panel shows: as the person last chose, or on demand in the Compact tier.
    private void RevealPanel()
    {
        if (IsInspectorShown)
        {
            return;
        }

        if (IsCompact && _inspectorChoice is null)
        {
            _swapClosedFor = null;
            RaiseInspector();
        }
        else
        {
            ChooseInspector(true);
        }
    }

    // The Chat page reads its node's log only while it shows.
    private void SyncConversation()
    {
        if (AgentPanel.IsRoom && !IsInspectorShown)
        {
            AgentPanel.IsRoom = false;
        }

        QueueRoomEnd();
        var node = IsInspectorShown && AgentPanel.ShowsChat ? Canvas?.Inspected as TaskNodeViewModel : null;
        if (node is null)
        {
            CloseConversation();
        }
        else if (Conversation is not { } open || open.Target.Canvas != Canvas || open.Node != node)
        {
            ShowConversation(new ConversationTarget(Canvas!, node.Id));
        }
    }

    // A Room is one node's, so it ends with the node's selection. A change from one node to another passes through no selection,
    // so the check waits for the change to end.
    private void QueueRoomEnd()
    {
        if (AgentPanel.IsRoom && Canvas?.Inspected is not TaskNodeViewModel)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (Canvas?.Inspected is not TaskNodeViewModel)
                {
                    AgentPanel.IsRoom = false;
                }
            }, DispatcherPriority.Background);
        }
    }

    private void CloseConversation()
    {
        Conversation?.Dispose();
        Conversation = null;
    }

    private void ShowConversation(ConversationTarget target)
    {
        Conversation?.Dispose();
        var project = target.Canvas.Project;
        Conversation = new ConversationViewModel(target, project.ConversationOf(target.Canvas, target.Task), target.Canvas.Engine.Conversation(target.Task), _copy);
    }

    /// <summary>
    /// Closes the project without saving. The window shows a neighbour's workflow, or nothing, at once. The project's running
    /// nodes then stop, and each reopens as interrupted. The next start does not reopen the project.
    /// </summary>
    public async Task Close(ProjectViewModel project)
    {
        var index = Projects.IndexOf(project);
        if (index < 0)
        {
            return;
        }

        Projects.RemoveAt(index);
        Remember(project.Folder);
        if (Canvas?.Project == project)
        {
            Show(Projects.Count == 0 ? null : Projects[Math.Min(index, Projects.Count - 1)].Workflows[0]);
        }

        Activity.Refresh();
        Persist();
        await project.CloseAsync();
    }

    /// <summary>
    /// Stops remembering a folder that did not open, so the next start does not try it again, and clears a status line that
    /// names it.
    /// </summary>
    public void Forget(UnopenedProject project)
    {
        if (Unopened.Remove(project))
        {
            Persist();
        }

        if (Status?.Contains(project.Reason, StringComparison.Ordinal) == true)
        {
            Status = null;
        }
    }

    /// <summary>Stops every running node, as closing the window does. The open projects stay remembered for the next start.</summary>
    public Task Leave() => Task.WhenAll(Projects.Select(project => project.CloseAsync().AsTask()));

    /// <summary>
    /// Opens the projects the last session left open, with the workflow rows it left expanded and the workflow it showed,
    /// then the folder, if any. A folder that is missing or fails to open is skipped with a status line and listed in
    /// <see cref="Unopened"/>.
    /// </summary>
    public void Restore(string? folder)
    {
        var session = _sessionFile is null ? WorkspaceSession.Empty : WorkspaceSession.Read(_sessionFile);
        foreach (var known in session.Recent.Take(MaxRecent))
        {
            Recent.Add(new RecentProject(known));
        }

        List<string> notes = [];
        _restoring = true;
        try
        {
            foreach (var saved in session.Projects)
            {
                if (TryAdd(saved, $"Couldn't reopen {saved}", out var notice) is null)
                {
                    Unopened.Add(new UnopenedProject(saved, notice!));
                }

                if (notice is not null)
                {
                    notes.Add(notice);
                }
            }

            Activity.IsOpen = session.ActivityOpen;
            var canvases = Projects.SelectMany(project => project.Workflows).ToList();
            foreach (var canvas in canvases)
            {
                canvas.RestoreExpandedCards(session.Cards.Where(saved => saved.Workflow.Is(canvas.Project.Folder, canvas.Workflow.Id))
                    .SelectMany(saved => saved.Tasks));
            }

            foreach (var project in Projects)
            {
                project.IsExpanded = !session.Folded.Contains(project.Folder, ProjectFolders.Comparer);
            }

            var selected = session.Selected is { } choice
                ? canvases.FirstOrDefault(canvas => choice.Is(canvas.Project.Folder, canvas.Workflow.Id))
                  ?? Projects.FirstOrDefault(project => ProjectFolders.Comparer.Equals(project.Folder, choice.Folder))?.Workflows[0]
                : null;
            if ((selected ?? canvases.FirstOrDefault()) is { } shown)
            {
                Select(shown);
                OpenShownNodes(shown);
            }
        }
        finally
        {
            _restoring = false;
        }

        if (folder is null)
        {
            Status = null;
            Persist();
        }
        else
        {
            Open(folder);
        }

        Status = string.Join(" ", [.. notes, .. Status is { } status ? [status] : Array.Empty<string>()]) is { Length: > 0 } all ? all : null;
    }

    private void TrySave()
    {
        if (Canvas is { } canvas)
        {
            TrySave([canvas.Document]);
        }
    }

    /// <summary>Saves each document in turn and stops at the first that fails, whose reason the status shows.</summary>
    internal bool TrySave(IEnumerable<WorkflowDocument> documents)
    {
        try
        {
            foreach (var document in documents)
            {
                document.Save();
            }

            Status = null;
            return true;
        }
        catch (Exception e) when (e is ProjectException or IOException or UnauthorizedAccessException)
        {
            Status = Describe(e, "Couldn't save");
            return false;
        }
    }

    /// <summary>Adds the folder as a project, or finds it open. Returns null when it fails to open, with the reason.</summary>
    private ProjectViewModel? TryAdd(string folder, string failure, out string? notices)
    {
        notices = null;
        ImmutableArray<WorkflowDocument> documents;
        string identity;
        try
        {
            identity = ProjectFolders.Identity(folder);
            if (Projects.FirstOrDefault(project => ProjectFolders.Comparer.Equals(project.Folder, identity)) is { } open)
            {
                return open;
            }

            documents = WorkflowDocument.OpenProject(identity);
        }
        catch (Exception e) when (e is ProjectException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            notices = Describe(e, failure);
            return null;
        }

        ProjectLock? windowLock;
        try
        {
            windowLock = ProjectLock.TryTake(identity);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            notices = Describe(e, failure);
            return null;
        }

        if (windowLock is null)
        {
            notices = $"{identity} is already open in another window.";
            return null;
        }

        var project = new ProjectViewModel(identity, windowLock, documents, NewCanvas);
        project.ConversationRequested += OpenChat;
        project.RoomRequested += OpenRoom;
        project.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProjectViewModel.IsExpanded))
            {
                Persist();
            }
        };
        Projects.Add(project);
        Activity.Refresh();
        foreach (var unopened in Unopened.Where(unopened => ProjectFolders.Comparer.Equals(unopened.Folder, identity)).ToList())
        {
            Unopened.Remove(unopened);
        }

        notices = string.Join(" ", documents.Select(document => document.Converted).OfType<string>()) is { Length: > 0 } notice
            ? notice
            : null;
        return project;
    }

    private WorkflowCanvasViewModel NewCanvas(ProjectViewModel project, WorkflowDocument document)
    {
        var canvas = new WorkflowCanvasViewModel(project, document, _clients, notice => Status = notice, _copy, _personalBlueprints, GitEnvironment)
        {
            OpenFolder = folder => OpenFolder(folder),
            OpenFile = file => OpenFile(file),
            ShowNext = state => NextRequested?.Invoke(state),
            FrameRunning = () => FrameRunningRequested?.Invoke(),
        };
        document.Changed += (_, _) =>
        {
            if (canvas == Canvas)
            {
                OnDocumentChanged();
            }
        };
        canvas.PropertyChanged += (_, e) => OnCanvasChanged(canvas, e);
        return canvas;
    }

    private void OnCanvasChanged(WorkflowCanvasViewModel canvas, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkflowCanvasViewModel.SelectedNode) && canvas == Canvas && canvas.SelectedNode is { } selected)
        {
            TreeReveal?.Invoke(canvas, selected);
        }

        switch (e.PropertyName)
        {
            case nameof(WorkflowCanvasViewModel.ExpandedCards):
                Persist();
                break;
            case nameof(WorkflowCanvasViewModel.Summary):
                Activity.Refresh();
                break;
            case nameof(WorkflowCanvasViewModel.Sheet) when canvas == Canvas:
                OnEditableChanged();
                break;
            case nameof(WorkflowCanvasViewModel.Name):
                Activity.Refresh();
                if (canvas == Canvas)
                {
                    OnPropertyChanged(nameof(WorkflowName));
                }

                break;
            case nameof(WorkflowCanvasViewModel.Inspected) when canvas == Canvas:
                AgentPanel.Follow(canvas.Inspected as TaskNodeViewModel);
                SyncConversation();
                break;
        }
    }

    // Every switch comes here, so the shown palette reads the libraries again, which a sibling may have changed.
    private void Show(WorkflowCanvasViewModel? canvas)
    {
        if (Canvas is { } shown)
        {
            shown.IsSelected = false;
            shown.Project.RaiseCurrent();
        }

        Canvas = canvas;
        if (canvas is not null)
        {
            canvas.IsSelected = true;
            canvas.Project.RaiseCurrent();
            canvas.Blueprints.Reload();
        }

        AgentPanel.Follow(canvas?.Inspected as TaskNodeViewModel);
        SyncConversation();
        OnPropertyChanged(nameof(ProjectName));
        OnPropertyChanged(nameof(WorkflowName));
        OnDocumentChanged();
    }

    private void Persist()
    {
        if (_sessionFile is not { } file || _restoring)
        {
            return;
        }

        var session = new WorkspaceSession(
            [.. Projects.Select(project => project.Folder), .. Unopened.Select(unopened => unopened.Folder)],
            Canvas is { } current ? new WorkflowRef(current.Project.Folder, current.Workflow.Id) : null)
        {
            Cards = [.. Projects.SelectMany(project => project.Workflows).Where(canvas => canvas.AnyCardExpanded)
                .Select(canvas => new WorkflowCards(new WorkflowRef(canvas.Project.Folder, canvas.Workflow.Id), canvas.ExpandedCards))],
            Folded = [.. Projects.Where(project => !project.IsExpanded).Select(project => project.Folder)],
            ActivityOpen = Activity.IsOpen,
            Recent = [.. Recent.Select(project => project.Folder).Where(folder => !Projects.Any(open => ProjectFolders.Comparer.Equals(open.Folder, folder)))],
        };
        try
        {
            session.Write(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Logger.TryGet(LogEventLevel.Warning, LogArea.Control)?.Log(this, "Couldn't remember the open projects in {File}: {Message}", file, e.Message);
        }
    }

    // A ProjectException's message is already written for the user. A file system error's message needs the action.
    private static string Describe(Exception e, string action) => e is ProjectException ? e.Message : $"{action}: {e.Message}";

    private void OnClientsChanged()
    {
        OnPropertyChanged(nameof(Agents));
        OnPropertyChanged(nameof(AgentsSummary));
        OnPropertyChanged(nameof(AgentsTone));
        OnPropertyChanged(nameof(AgentsNeedingSetup));
        OnPropertyChanged(nameof(HasAgentsNeedingSetup));
        foreach (var project in Projects)
        {
            foreach (var canvas in project.Workflows)
            {
                canvas.OnClientsChanged();
            }
        }
    }

    private async Task RefreshAgents()
    {
        _refreshingAgents = true;
        _refreshAgents.NotifyCanExecuteChanged();
        try
        {
            await _clients.RefreshAsync();
        }
        finally
        {
            _refreshingAgents = false;
            _refreshAgents.NotifyCanExecuteChanged();
        }
    }

    private void OnDocumentChanged()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnEditableChanged();
    }

    private void OnEditableChanged()
    {
        _save.NotifyCanExecuteChanged();
        _undo.NotifyCanExecuteChanged();
        _redo.NotifyCanExecuteChanged();
    }
}
