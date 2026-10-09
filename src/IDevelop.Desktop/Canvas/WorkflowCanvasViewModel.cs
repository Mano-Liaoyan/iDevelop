using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Threading;
using IDevelop.Desktop.Blueprints;
using IDevelop.Desktop.Execution;
using IDevelop.Desktop.Mvvm;
using IDevelop.Execution;
using IDevelop.Nodes;
using IDevelop.Projects;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// The canvas: its nodes, connections, and selection, kept in step with the workflow document and the project's runs.
/// The card highlight, the add surfaces, and Generate each extend it in a file of their own.
/// </summary>
public sealed partial class WorkflowCanvasViewModel : ObservableObject
{
    // The visible card is 240 wide. The container adds a 10 px gutter on each side, where the ports sit on the card's edge.
    public const double TaskCardWidth = 260;

    public const double TaskCardHeight = 64;

    /// <summary>The input port's center in the card's container. The card template places the port here.</summary>
    public static readonly Point InputPortCenter = new(10, 32);

    /// <summary>The output port's center in the card's container. The card template places the port here.</summary>
    public static readonly Point OutputPortCenter = new(250, 32);

    internal static readonly Size TaskFootprint = new(TaskCardWidth + 40, TaskCardHeight + 30);

    private readonly Action<string?> _setNotice;
    private readonly Func<string, Task> _copy;
    private readonly Dictionary<TaskId, TaskNodeViewModel> _nodes = [];
    private readonly Dictionary<ConnectionKey, ConnectionViewModel> _connections = [];
    private readonly HashSet<object> _closedProposals = [];
    private Workflow? _projected;
    private TaskNodeViewModel? _selectedNode;
    private ConnectionViewModel? _selectedConnection;
    private Point _viewportLocation;
    private double _viewportZoom = 1;
    private bool _isExpanded;
    private bool _isSelected;
    private bool _isRunning;
    private bool _isRenamingWorkflow;
    private (TaskNodeViewModel[] Nodes, ConnectionViewModel[] Connections)? _heldSelection;

    /// <param name="project">The open project whose runner and task owners this canvas shares.</param>
    /// <param name="personalBlueprints">The personal library's folder, or null for none.</param>
    internal WorkflowCanvasViewModel(
        ProjectViewModel project, WorkflowDocument document, ClientDirectory clients, Action<string?> setNotice, Func<string, Task> copy, string? personalBlueprints = null)
    {
        Project = project;
        Document = document;
        Clients = clients;
        _setNotice = setNotice;
        _copy = copy;
        ActiveRun = new ActiveRunViewModel(Runs, clients);
        Runs.Changed += (_, _) => Dispatcher.UIThread.Post(ShowAttempts);
        PendingConnection = new PendingConnectionViewModel(this);
        Blueprints = new BlueprintsViewModel(
            this, BlueprintLibrary.Project(document.ProjectFolder), personalBlueprints is null ? null : BlueprintLibrary.Personal(personalBlueprints));
        DeleteSelectionCommand = new RelayCommand(DeleteSelection);
        ConnectCommand = new RelayCommand<(object Source, object? Target)>(drop => Connect(drop.Source, drop.Target));
        RemoveConnectionCommand = new RelayCommand<ConnectionViewModel>(connection => Edit(new WorkflowEdit.Delete([], [connection.Key])));
        CommitMovesCommand = new RelayCommand(CommitMoves);
        NextWaitingCommand = new RelayCommand(SelectNextWaiting);
        Document.Changed += (_, _) => Sync();
        Sync();
        InitializeHighlight();
        InitializeAdd();
        InitializeGenerate();
    }

    public ObservableCollection<TaskNodeViewModel> Nodes { get; } = [];

    public ObservableCollection<ConnectionViewModel> Connections { get; } = [];

    /// <summary>The tasks and connections that open proposals add or fill, and a dropped wire while the Add popover is open.</summary>
    public ObservableCollection<Ghost> Ghosts { get; } = [];

    public ObservableCollection<TaskNodeViewModel> SelectedNodes { get; } = [];

    public ObservableCollection<ConnectionViewModel> SelectedConnections { get; } = [];

    // Clicking a connection does not clear the editor's task selection, so each selection
    // clears the other one here and the inspector follows the most recent click.
    public TaskNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (SetProperty(ref _selectedNode, value))
            {
                if (value is not null)
                {
                    SelectedConnections.Clear();
                }

                OnPropertyChanged(nameof(Inspected));
            }
        }
    }

    public ConnectionViewModel? SelectedConnection
    {
        get => _selectedConnection;
        set
        {
            if (SetProperty(ref _selectedConnection, value))
            {
                if (value is not null)
                {
                    SelectedNodes.Clear();
                }

                OnPropertyChanged(nameof(Inspected));
            }
        }
    }

    public object? Inspected => (object?)SelectedNode ?? SelectedConnection;

    public PendingConnectionViewModel PendingConnection { get; }

    public ActiveRunViewModel ActiveRun { get; }

    /// <summary>The palette and the blueprint editor.</summary>
    public BlueprintsViewModel Blueprints { get; }

    public Point ViewportLocation
    {
        get => _viewportLocation;
        set => SetProperty(ref _viewportLocation, value);
    }

    public double ViewportZoom
    {
        get => _viewportZoom;
        set => SetProperty(ref _viewportZoom, value);
    }

    /// <summary>Whether a view has shown this canvas yet. The first view places the cards below the floating chrome.</summary>
    internal bool IsPositioned { get; set; }

    /// <summary>The workflow's name as the sidebar and the breadcrumb show it.</summary>
    public string Name => Workflow.DisplayName;

    public bool HasUnsavedChanges => Document.HasUnsavedChanges;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(ExpandLabel));
            }
        }
    }

    /// <summary>What the row's disclosure toggle does now, for a screen reader and the tooltip.</summary>
    public string ExpandLabel => $"{(IsExpanded ? "Hide" : "Show")} tasks of {Name}";

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    /// <summary>Whether a task this workflow holds or held this session runs in this window.</summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set => SetProperty(ref _isRunning, value);
    }

    public bool IsRenamingWorkflow
    {
        get => _isRenamingWorkflow;
        private set => SetProperty(ref _isRenamingWorkflow, value);
    }

    public ICommand DeleteSelectionCommand { get; }

    public ICommand ConnectCommand { get; }

    public ICommand RemoveConnectionCommand { get; }

    public ICommand CommitMovesCommand { get; }

    /// <summary>How many tasks wait for the person.</summary>
    public int WaitingCount => Nodes.Count(node => node.IsWaiting);

    public bool HasWaiting => WaitingCount > 0;

    public string WaitingLabel => WaitingCount == 1 ? "1 task waits for you" : $"{WaitingCount} tasks wait for you";

    /// <summary>Selects the next task that waits for the person, after the selected one, in the order the sidebar lists them.</summary>
    public ICommand NextWaitingCommand { get; }

    internal ProjectViewModel Project { get; }

    internal WorkflowDocument Document { get; }

    internal Workflow Workflow => Document.Current;

    internal ProjectRuns Runs => Project.Runs;

    internal ClientDirectory Clients { get; }

    /// <summary>Called on the UI thread after the client directory changes.</summary>
    internal void OnClientsChanged()
    {
        foreach (var node in Nodes)
        {
            node.OnAgentChanged();
            node.RecheckProblem();
        }
    }

    /// <summary>
    /// Clears the selection and holds it until a view lays out and calls <see cref="RestoreSelection"/>. A view that never
    /// lays out, because the window showed another workflow first, leaves it held for the next view.
    /// </summary>
    internal void HoldSelection()
    {
        _heldSelection ??= ([.. SelectedNodes], [.. SelectedConnections]);
        SelectedNodes.Clear();
        SelectedConnections.Clear();
        SelectedNode = null;
        SelectedConnection = null;
    }

    /// <summary>Selects again what <see cref="HoldSelection"/> held, leaving out what the workflow no longer holds.</summary>
    internal void RestoreSelection()
    {
        if (_heldSelection is not { } selection)
        {
            return;
        }

        _heldSelection = null;
        SelectedNodes.Clear();
        SelectedConnections.Clear();
        foreach (var node in selection.Nodes.Where(Nodes.Contains))
        {
            SelectedNodes.Add(node);
        }

        foreach (var connection in selection.Connections.Where(Connections.Contains))
        {
            SelectedConnections.Add(connection);
        }
    }

    internal void BeginRenameWorkflow() => IsRenamingWorkflow = true;

    /// <summary>
    /// Ends a rename with the new name, or with null to keep the old one. A new name is an edit that Undo takes back, and a
    /// blank one leaves the workflow unnamed.
    /// </summary>
    internal void EndRenameWorkflow(string? name)
    {
        if (!IsRenamingWorkflow)
        {
            return;
        }

        IsRenamingWorkflow = false;
        if (name is not null && name.Trim() != Name)
        {
            Edit(new WorkflowEdit.Rename(name));
        }
    }

    internal void Notice(string? text) => _setNotice(text);

    internal Task Copy(string text) => _copy(text);

    /// <param name="title">Names a task in a rejection, for an edit that adds tasks. Null names them from the workflow.</param>
    internal EditResult Edit(WorkflowEdit edit, Func<TaskId, string>? title = null)
    {
        var result = Document.Apply(edit);
        _setNotice(result is EditResult.Rejected rejected
            ? title is null ? RejectionText.Describe(rejected.Reason, Document.Current) : RejectionText.Describe(rejected.Reason, title)
            : null);
        return result;
    }

    /// <summary>A blueprint the palette offers, or a copy the workflow embeds, by key.</summary>
    internal Blueprint? FindBlueprint(BlueprintKey key) =>
        Blueprints.Placeable.FirstOrDefault(blueprint => blueprint.Key == key) ?? Workflow.Blueprints.GetValueOrDefault(key);

    /// <summary>The kind of a blueprint, following its derivation through the workflow's copies and then the libraries.</summary>
    internal NodeKind KindOf(Blueprint blueprint) =>
        NodeKinds.Of(blueprint, key => Workflow.Blueprints.GetValueOrDefault(key) ?? Blueprints.Placeable.FirstOrDefault(placeable => placeable.Key == key));

    /// <summary>What the planner may fill and place when it starts now: the palette's blueprints are its types.</summary>
    internal PlanningContext Planning(TaskId planner) => PlanningContext.For(Workflow, planner, Blueprints.Placeable, HasStarted);

    /// <summary>Whether the task has an attempt in this project.</summary>
    internal bool HasStarted(TaskId task) => Runs.Latest.ContainsKey(task);

    /// <summary>Hides a proposal until its planner proposes again. Only this window forgets it.</summary>
    internal void CloseProposal(ProposalViewModel proposal)
    {
        _closedProposals.Add(proposal.Identity);
        foreach (var node in Nodes)
        {
            node.ShowProposal();
        }

        ShowGhosts();
    }

    internal bool IsClosed(ProposalViewModel proposal) => _closedProposals.Contains(proposal.Identity) || proposal.IsSettled(Workflow);

    /// <summary>Draws the chosen tasks of every open proposal, with their connections, and a dropped wire.</summary>
    internal void ShowGhosts()
    {
        Ghosts.Clear();
        var proposals = Nodes.Select(node => node.Proposal).OfType<ProposalViewModel>().SelectMany(proposal => proposal.Ghosts(Workflow));
        foreach (var ghost in DanglingWire() is { } wire ? proposals.Prepend(wire) : proposals)
        {
            Ghosts.Add(ghost);
        }
    }

    internal static ConnectionKey? ResolveEndpoints(object? source, object? target) => (source, target) switch
    {
        (PortViewModel { Side: PortSide.Output } output, PortViewModel { Side: PortSide.Input } input) => new ConnectionKey(output.Node.Id, input.Node.Id),
        (PortViewModel { Side: PortSide.Input } input, PortViewModel { Side: PortSide.Output } output) => new ConnectionKey(output.Node.Id, input.Node.Id),
        _ => null,
    };

    // Each change reads the newest attempts, which is never older than the change itself. A start, even one that another
    // window's run refuses, also reads the other tasks' attempts again, which another window may have ended or a crash
    // may have left running.
    internal void ShowAttempts()
    {
        var changed = Nodes.Where(node => node.ShowAttempt(Runs.Latest.GetValueOrDefault(node.Id))).Select(node => node.Id).ToList();
        // A review that rests between its turns can stop for a reason that changes no attempt, such as a fix round that
        // closing iDevelop interrupted, which waits for the person's choice.
        var resting = Nodes.Where(node => node.IsReview && Runs.Latest.GetValueOrDefault(node.Id) is { Status: AttemptStatus.InReview }).Select(node => node.Id);
        RecheckProblems(changed.Concat(changed.SelectMany(DependencyNeighbors)).Concat(resting));

        foreach (var node in Nodes)
        {
            node.Proposal?.Refresh();
        }

        var own = Runs.Active.Where(run => Project.Owns(this, run.Task)).ToImmutableArray();
        ActiveRun.Show(own);
        _standaloneRunning = !own.IsEmpty;
        ShowActivity();
        ShowGhosts();
    }

    private void OnWaitingChanged()
    {
        OnPropertyChanged(nameof(WaitingCount));
        OnPropertyChanged(nameof(HasWaiting));
        OnPropertyChanged(nameof(WaitingLabel));
    }

    /// <summary>Places a node of the blueprint near the top left of the view, below any card already there, and selects it.</summary>
    internal void PlaceInView(Blueprint blueprint) =>
        Place(NewTask(blueprint, FreeSpot(new CanvasPoint(ViewportLocation.X + 60, ViewportLocation.Y + 60))));

    /// <summary>Applies the placement, selects the new node, and returns it, or returns null when the edit was rejected.</summary>
    internal TaskNodeViewModel? Place(WorkflowEdit.PlaceNode edit)
    {
        if (Edit(edit) is not EditResult.Applied)
        {
            return null;
        }

        var node = _nodes[edit.Id];
        Select(node);
        return node;
    }

    /// <summary>The first spot at or below <paramref name="start"/>, in steps of a card's footprint, that no card is near.</summary>
    internal CanvasPoint FreeSpot(CanvasPoint start) => Offset(start, FreeShift([start]));

    /// <summary>
    /// The shortest shift down, in steps of a card's footprint, after which no card is near any of the spots. The spots move
    /// together, so a group keeps its layout.
    /// </summary>
    private Vector FreeShift(IReadOnlyCollection<CanvasPoint> spots)
    {
        var shift = default(Vector);
        while (spots.Any(spot => Workflow.Positions.Values.Any(other =>
            Math.Abs(other.X - spot.X - shift.X) < TaskFootprint.Width && Math.Abs(other.Y - spot.Y - shift.Y) < TaskFootprint.Height)))
        {
            shift += new Vector(0, TaskFootprint.Height);
        }

        return shift;
    }

    private static WorkflowEdit.PlaceNode NewTask(Blueprint blueprint, CanvasPoint position) =>
        new(TaskId.New(), blueprint, position) { Title = "New task" };

    private void Select(TaskNodeViewModel node)
    {
        SelectedNodes.Clear();
        SelectedNodes.Add(node);
    }

    private void SelectNextWaiting()
    {
        var ordered = Outline.ToList();
        var start = SelectedNode is { } selected ? ordered.IndexOf(selected) + 1 : 0;
        if (Enumerable.Range(0, ordered.Count).Select(step => ordered[(start + step) % ordered.Count]).FirstOrDefault(node => node.IsWaiting) is { } next)
        {
            Select(next);
        }
    }

    private void DeleteSelection() => Edit(new WorkflowEdit.Delete(
        [.. SelectedNodes.Select(node => node.Id)],
        [.. SelectedConnections.Select(connection => connection.Key)]));

    // Nodify passes a null target for a wire dropped anywhere but on a port.
    private void Connect(object source, object? target)
    {
        if (ResolveEndpoints(source, target) is { } key)
        {
            Edit(new WorkflowEdit.Connect(key, ConnectionKind.Dependency));
        }
        else if (target is null && source is PortViewModel port)
        {
            DropWire(port);
        }
    }

    // The editor writes each dragged container's final location into its node before it runs
    // this command, so the nodes that differ from the snapshot are the ones the gesture moved.
    private void CommitMoves()
    {
        var moves = _nodes.Values
            .Where(node => node.Location != ToPoint(Workflow.Positions[node.Id]))
            .Select(node => new TaskPosition(node.Id, new CanvasPoint(node.Location.X, node.Location.Y)))
            .ToImmutableArray();
        if (moves.Length > 0)
        {
            Edit(new WorkflowEdit.MoveTasks(moves));
        }
    }

    private void Sync()
    {
        var previous = _projected;
        var current = Document.Current;
        _projected = current;
        var connectionsChanged = !ReferenceEquals(previous?.Connections, current.Connections);

        // A connection holds its two task nodes, so a connection leaves before its nodes can, and comes after they exist.
        if (connectionsChanged)
        {
            DropConnections(current);
        }

        if (!ReferenceEquals(previous?.Tasks, current.Tasks) || !ReferenceEquals(previous?.Positions, current.Positions))
        {
            SyncNodes(current);
            if (Run is not null)
            {
                ShowRun();
            }

            OnWaitingChanged();
        }

        if (connectionsChanged)
        {
            SyncConnections(current);
        }

        if (!ReferenceEquals(previous?.Positions, current.Positions))
        {
            Reroute();
        }

        if (connectionsChanged || !ReferenceEquals(previous?.Tasks, current.Tasks) || !ReferenceEquals(previous?.Positions, current.Positions))
        {
            ArrangeOutline();
        }

        if (!ReferenceEquals(previous, current))
        {
            foreach (var node in Nodes)
            {
                node.ShowProposal();
            }

            ShowGhosts();
        }

        if (!ReferenceEquals(previous?.Name, current.Name))
        {
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(ExpandLabel));
        }

        OnPropertyChanged(nameof(HasUnsavedChanges));

        // The project's runner already follows this change, so the checks see it. A move changes no reason not to start.
        if (!ReferenceEquals(previous?.Tasks, current.Tasks) || connectionsChanged)
        {
            // An edit of a task or of the connections into it can put an earlier result out of date (#90).
            ShowHistory();
            RecheckProblems(Changed(previous, current));
        }
    }

    /// <summary>
    /// The tasks whose reason not to start may differ between the two workflows: a new or edited task, both ends of an
    /// added, removed, or retyped connection, and every review, whose subject is whatever it depends on now.
    /// </summary>
    private static IEnumerable<TaskId> Changed(Workflow? previous, Workflow current)
    {
        var tasks = current.Tasks
            .Where(task => previous?.Tasks.GetValueOrDefault(task.Key) is not { } old || !ReferenceEquals(old, task.Value) || task.Value.Blueprint.Work is WorkSpec.Review)
            .Select(task => task.Key);
        var connections = previous is null ? current.Connections.Keys : current.Connections
            .Where(connection => !previous.Connections.TryGetValue(connection.Key, out var kind) || kind != connection.Value)
            .Select(connection => connection.Key)
            .Concat(previous.Connections.Keys.Where(key => !current.Connections.ContainsKey(key)));
        return tasks.Concat(connections.SelectMany(key => new[] { key.From, key.To }));
    }

    /// <summary>The tasks connected to this one by a dependency in either direction.</summary>
    private IEnumerable<TaskId> DependencyNeighbors(TaskId task) =>
        Workflow.Connections.Where(connection => connection.Value.Blocks() && (connection.Key.From == task || connection.Key.To == task))
            .Select(connection => connection.Key.From == task ? connection.Key.To : connection.Key.From);

    private void RecheckProblems(IEnumerable<TaskId> tasks)
    {
        foreach (var id in tasks.Distinct().ToList())
        {
            if (_nodes.TryGetValue(id, out var node))
            {
                node.RecheckProblem();
            }
        }
    }

    private void DropConnections(Workflow current)
    {
        foreach (var key in _connections.Keys.Where(key => !current.Connections.ContainsKey(key)).ToList())
        {
            var connection = _connections[key];
            connection.Detach();
            SelectedConnections.Remove(connection);
            Connections.Remove(connection);
            _connections.Remove(key);
        }
    }

    private void SyncNodes(Workflow current)
    {
        foreach (var (id, task) in current.Tasks)
        {
            if (_nodes.TryGetValue(id, out var node))
            {
                node.Update(task, current.Positions[id]);
            }
            else
            {
                // In the workflow's order, so a task that an undo brings back returns to its place among the cards.
                node = new TaskNodeViewModel(this, task, current.Positions[id]);
                _nodes.Add(id, node);
                Nodes.Insert(Nodes.Count(other => other.Id.CompareTo(id) < 0), node);
            }
        }

        foreach (var id in _nodes.Keys.Where(id => !current.Tasks.ContainsKey(id)).ToList())
        {
            var node = _nodes[id];
            SelectedNodes.Remove(node);
            Nodes.Remove(node);
            _nodes.Remove(id);
        }
    }

    private void SyncConnections(Workflow current)
    {
        foreach (var (key, kind) in current.Connections)
        {
            if (_connections.TryGetValue(key, out var connection))
            {
                connection.Update(kind);
            }
            else
            {
                connection = new ConnectionViewModel(this, key, _nodes[key.From], _nodes[key.To], kind);
                _connections.Add(key, connection);
                Connections.Add(connection);
            }
        }

        var sources = current.Connections.Keys.Select(key => key.From).ToHashSet();
        var targets = current.Connections.Keys.Select(key => key.To).ToHashSet();
        foreach (var (id, node) in _nodes)
        {
            node.Output.IsConnected = sources.Contains(id);
            node.Input.IsConnected = targets.Contains(id);
        }
    }

    internal static Point ToPoint(CanvasPoint point) => new(point.X, point.Y);

    partial void InitializeHighlight();

    partial void InitializeAdd();

    partial void InitializeGenerate();
}
