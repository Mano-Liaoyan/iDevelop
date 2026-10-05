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

public sealed class WorkflowCanvasViewModel : ObservableObject
{
    // The visible card is 240 wide. The container adds a 10 px gutter on each side, where the ports sit on the card's edge.
    public const double TaskCardWidth = 260;

    public const double TaskCardHeight = 144;

    private static readonly Size TaskFootprint = new(TaskCardWidth + 40, TaskCardHeight + 30);

    private readonly Action<string?> _setNotice;
    private readonly Func<string, Task> _copy;
    private readonly Dictionary<TaskId, TaskNodeViewModel> _nodes = [];
    private readonly Dictionary<ConnectionKey, ConnectionViewModel> _connections = [];
    private readonly HashSet<object> _closedProposals = [];
    private Workflow? _projected;
    private TaskNodeViewModel? _selectedNode;
    private ConnectionViewModel? _selectedConnection;
    private Point _viewportLocation;

    /// <param name="personalBlueprints">The personal library's folder, or null for none.</param>
    public WorkflowCanvasViewModel(
        WorkflowDocument document, ProjectRuns runs, ClientDirectory clients, Action<string?> setNotice, Func<string, Task> copy, string? personalBlueprints = null)
    {
        Document = document;
        Runs = runs;
        Clients = clients;
        _setNotice = setNotice;
        _copy = copy;
        ActiveRun = new ActiveRunViewModel(runs, clients);
        runs.Changed += (_, _) => Dispatcher.UIThread.Post(ShowAttempts);
        PendingConnection = new PendingConnectionViewModel(this);
        Blueprints = new BlueprintsViewModel(
            this, BlueprintLibrary.Project(document.ProjectFolder), personalBlueprints is null ? null : BlueprintLibrary.Personal(personalBlueprints));
        AddTaskCommand = new RelayCommand(() => PlaceInView(BuiltInBlueprints.Implement));
        AddTaskAtCommand = new RelayCommand<Point>(location => Place(BuiltInBlueprints.Implement, new CanvasPoint(location.X, location.Y)));
        DeleteSelectionCommand = new RelayCommand(DeleteSelection);
        ConnectCommand = new RelayCommand<(object Source, object? Target)>(drop => Connect(drop.Source, drop.Target));
        RemoveConnectionCommand = new RelayCommand<ConnectionViewModel>(connection => Edit(new WorkflowEdit.Delete([], [connection.Key])));
        CommitMovesCommand = new RelayCommand(CommitMoves);
        NextWaitingCommand = new RelayCommand(SelectNextWaiting);
        Document.Changed += (_, _) => Sync();
        Sync();
    }

    public ObservableCollection<TaskNodeViewModel> Nodes { get; } = [];

    public ObservableCollection<ConnectionViewModel> Connections { get; } = [];

    /// <summary>The dashed cards of the tasks that open proposals add or fill.</summary>
    public ObservableCollection<GhostCardViewModel> Ghosts { get; } = [];

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

    public ICommand AddTaskCommand { get; }

    public ICommand AddTaskAtCommand { get; }

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

    internal WorkflowDocument Document { get; }

    internal Workflow Workflow => Document.Current;

    internal ProjectRuns Runs { get; }

    internal ClientDirectory Clients { get; }

    /// <summary>Called on the UI thread after the client directory changes.</summary>
    internal void OnClientsChanged()
    {
        foreach (var node in Nodes)
        {
            node.OnAgentChanged();
        }

        // A review whose fix round waited for a client goes on.
        Runs.Follow(Document.Current);
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

    /// <summary>Draws the chosen tasks of every open proposal.</summary>
    internal void ShowGhosts()
    {
        Ghosts.Clear();
        foreach (var ghost in Nodes.Select(node => node.Proposal).OfType<ProposalViewModel>().SelectMany(proposal => proposal.Ghosts(Workflow)))
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
    private void ShowAttempts()
    {
        foreach (var node in Nodes)
        {
            node.ShowAttempt(Runs.Latest.GetValueOrDefault(node.Id));
        }

        foreach (var node in Nodes)
        {
            node.Proposal?.Refresh();
        }

        ActiveRun.Show(Runs.Active);
        OnWaitingChanged();
        ShowGhosts();
    }

    private void OnWaitingChanged()
    {
        OnPropertyChanged(nameof(WaitingCount));
        OnPropertyChanged(nameof(HasWaiting));
        OnPropertyChanged(nameof(WaitingLabel));
    }

    /// <summary>Places a node of the blueprint near the top left of the view, below any card already there, and selects it.</summary>
    internal void PlaceInView(Blueprint blueprint)
    {
        var position = new CanvasPoint(ViewportLocation.X + 60, ViewportLocation.Y + 60);
        while (Workflow.Positions.Values.Any(other =>
            Math.Abs(other.X - position.X) < TaskFootprint.Width && Math.Abs(other.Y - position.Y) < TaskFootprint.Height))
        {
            position = position with { Y = position.Y + TaskFootprint.Height };
        }

        Place(blueprint, position);
    }

    private void Place(Blueprint blueprint, CanvasPoint position)
    {
        var id = TaskId.New();
        if (Edit(new WorkflowEdit.PlaceNode(id, blueprint, position) { Title = "New task" }) is EditResult.Applied)
        {
            Select(_nodes[id]);
        }
    }

    private void Select(TaskNodeViewModel node)
    {
        SelectedNodes.Clear();
        SelectedNodes.Add(node);
    }

    private void SelectNextWaiting()
    {
        var ordered = Nodes.ToList();
        var start = SelectedNode is { } selected ? ordered.IndexOf(selected) + 1 : 0;
        if (Enumerable.Range(0, ordered.Count).Select(step => ordered[(start + step) % ordered.Count]).FirstOrDefault(node => node.IsWaiting) is { } next)
        {
            Select(next);
        }
    }

    private void DeleteSelection() => Edit(new WorkflowEdit.Delete(
        [.. SelectedNodes.Select(node => node.Id)],
        [.. SelectedConnections.Select(connection => connection.Key)]));

    private void Connect(object source, object? target)
    {
        if (ResolveEndpoints(source, target) is { } key)
        {
            Edit(new WorkflowEdit.Connect(key, ConnectionKind.Dependency));
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
            OnWaitingChanged();
        }

        if (connectionsChanged)
        {
            SyncConnections(current);
        }

        if (!ReferenceEquals(previous, current))
        {
            foreach (var node in Nodes)
            {
                node.ShowProposal();
            }

            ShowGhosts();
        }

        Runs.Follow(current);
    }

    private void DropConnections(Workflow current)
    {
        foreach (var key in _connections.Keys.Where(key => !current.Connections.ContainsKey(key)).ToList())
        {
            var connection = _connections[key];
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
                node = new TaskNodeViewModel(this, task, current.Positions[id]);
                _nodes.Add(id, node);
                Nodes.Add(node);
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
}
