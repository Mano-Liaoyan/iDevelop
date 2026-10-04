using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia;
using IDevelop.Desktop.Mvvm;
using IDevelop.Execution;
using IDevelop.Projects;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

public sealed class WorkflowCanvasViewModel : ObservableObject
{
    // The visible card is 240 wide. The container adds a 10 px gutter on each side, where the ports sit on the card's edge.
    public const double TaskCardWidth = 260;

    public const double TaskCardHeight = 144;

    private static readonly Size TaskFootprint = new(TaskCardWidth + 40, TaskCardHeight + 30);

    private readonly WorkflowDocument _document;
    private readonly Action<string?> _setNotice;
    private readonly Dictionary<TaskId, TaskNodeViewModel> _nodes = [];
    private readonly Dictionary<ConnectionKey, ConnectionViewModel> _connections = [];
    private Workflow? _projected;
    private TaskNodeViewModel? _selectedNode;
    private ConnectionViewModel? _selectedConnection;
    private Point _viewportLocation;

    public WorkflowCanvasViewModel(WorkflowDocument document, ClientDirectory clients, Action<string?> setNotice)
    {
        _document = document;
        Clients = clients;
        _setNotice = setNotice;
        PendingConnection = new PendingConnectionViewModel(this);
        AddTaskCommand = new RelayCommand(AddTaskInView);
        AddTaskAtCommand = new RelayCommand<Point>(location => AddTask(new CanvasPoint(location.X, location.Y)));
        DeleteSelectionCommand = new RelayCommand(DeleteSelection);
        ConnectCommand = new RelayCommand<(object Source, object? Target)>(drop => Connect(drop.Source, drop.Target));
        RemoveConnectionCommand = new RelayCommand<ConnectionViewModel>(connection => Edit(new WorkflowEdit.Delete([], [connection.Key])));
        CommitMovesCommand = new RelayCommand(CommitMoves);
        _document.Changed += (_, _) => Sync();
        Sync();
    }

    public ObservableCollection<TaskNodeViewModel> Nodes { get; } = [];

    public ObservableCollection<ConnectionViewModel> Connections { get; } = [];

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

    internal Workflow Workflow => _document.Current;

    internal ClientDirectory Clients { get; }

    /// <summary>Called on the UI thread after the client directory changes.</summary>
    internal void OnClientsChanged()
    {
        foreach (var node in Nodes)
        {
            node.OnAgentChanged();
        }
    }

    internal EditResult Edit(WorkflowEdit edit)
    {
        var result = _document.Apply(edit);
        _setNotice(result is EditResult.Rejected rejected ? RejectionText.Describe(rejected.Reason, _document.Current) : null);
        return result;
    }

    internal static ConnectionKey? ResolveEndpoints(object? source, object? target) => (source, target) switch
    {
        (PortViewModel { Side: PortSide.Output } output, PortViewModel { Side: PortSide.Input } input) => new ConnectionKey(output.Node.Id, input.Node.Id),
        (PortViewModel { Side: PortSide.Input } input, PortViewModel { Side: PortSide.Output } output) => new ConnectionKey(output.Node.Id, input.Node.Id),
        _ => null,
    };

    private void AddTaskInView()
    {
        var position = new CanvasPoint(ViewportLocation.X + 60, ViewportLocation.Y + 60);
        while (Workflow.Positions.Values.Any(other =>
            Math.Abs(other.X - position.X) < TaskFootprint.Width && Math.Abs(other.Y - position.Y) < TaskFootprint.Height))
        {
            position = position with { Y = position.Y + TaskFootprint.Height };
        }

        AddTask(position);
    }

    private void AddTask(CanvasPoint position)
    {
        var task = new TaskDefinition(TaskId.New()) { Title = "New task" };
        if (Edit(new WorkflowEdit.CreateTask(task, position)) is EditResult.Applied)
        {
            SelectedNodes.Clear();
            SelectedNodes.Add(_nodes[task.Id]);
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
        var current = _document.Current;
        _projected = current;
        var connectionsChanged = !ReferenceEquals(previous?.Connections, current.Connections);

        if (connectionsChanged)
        {
            foreach (var key in _connections.Keys.Where(key => !current.Connections.ContainsKey(key)).ToList())
            {
                var connection = _connections[key];
                SelectedConnections.Remove(connection);
                Connections.Remove(connection);
                _connections.Remove(key);
            }
        }

        if (!ReferenceEquals(previous?.Tasks, current.Tasks) || !ReferenceEquals(previous?.Positions, current.Positions))
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

        if (connectionsChanged)
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
    }

    internal static Point ToPoint(CanvasPoint point) => new(point.X, point.Y);
}
