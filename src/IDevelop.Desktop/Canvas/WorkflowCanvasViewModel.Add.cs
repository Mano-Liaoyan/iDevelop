using System.Collections.Immutable;
using System.Diagnostics;
using Avalonia;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

/// <summary>Where the Add popover places a node, and what it connects the node to.</summary>
public abstract record AddTarget
{
    private AddTarget()
    {
    }

    /// <summary>Right-click, double-click, or N on empty canvas. The card centers on the point.</summary>
    public sealed record AtPoint(CanvasPoint Point) : AddTarget;

    /// <summary>The sidebar's Add Node button. The card takes the first free spot near the view's top left.</summary>
    public sealed record InView : AddTarget;

    /// <summary>A wire from an output dropped on empty canvas. The new node's input port lands on the drop.</summary>
    public sealed record FromOutput(TaskId Source, CanvasPoint Drop) : AddTarget;

    /// <summary>A wire from an input dropped on empty canvas. The new node's output port lands on the drop.</summary>
    public sealed record ToInput(TaskId Target, CanvasPoint Drop) : AddTarget;

    /// <summary>Insert Node on a connection. The new node takes the connection's place in its two halves.</summary>
    public sealed record Between(ConnectionKey Connection, CanvasPoint Point) : AddTarget;
}

/// <summary>What only the canvas view can do: move the viewport, and focus a box in the inspector.</summary>
internal interface ICanvasView
{
    /// <summary>Where the pointer last was over the canvas, in canvas coordinates.</summary>
    CanvasPoint Pointer { get; }

    void FitToView();

    void ZoomToActual();

    /// <summary>Focuses the inspector's title box with its text selected.</summary>
    void FocusTitle();
}

public sealed partial class WorkflowCanvasViewModel
{
    private static readonly Vector DuplicateOffset = new(30, 30);

    private AddNodeViewModel? _addNode;

    /// <summary>The open Add popover, or null.</summary>
    public AddNodeViewModel? AddNode
    {
        get => _addNode;
        private set
        {
            if (SetProperty(ref _addNode, value))
            {
                ShowGhosts();
            }
        }
    }

    internal ICanvasView? View { get; set; }

    internal void OpenAdd(AddTarget target) => AddNode = new AddNodeViewModel(this, target);

    internal void CloseAdd() => AddNode = null;

    /// <summary>Places a node of the blueprint for the target in one edit, connects it as the target says, and selects it.</summary>
    internal TaskNodeViewModel? Add(AddTarget target, Blueprint blueprint)
    {
        var place = NewTask(blueprint, Position(target));
        var id = place.Id;
        ImmutableArray<WorkflowEdit> wiring = target switch
        {
            AddTarget.FromOutput from => [new WorkflowEdit.Connect(new(from.Source, id), ConnectionKind.Dependency)],
            AddTarget.ToInput to => [new WorkflowEdit.Connect(new(id, to.Target), ConnectionKind.Dependency)],
            AddTarget.Between between when Workflow.Connections.TryGetValue(between.Connection, out var kind) =>
            [
                new WorkflowEdit.Delete([], [between.Connection]),
                new WorkflowEdit.Connect(new(between.Connection.From, id), kind),
                new WorkflowEdit.Connect(new(id, between.Connection.To), kind),
            ],
            _ => [],
        };
        if (!Apply([place, .. wiring], [place]))
        {
            return null;
        }

        var node = _nodes[id];
        Select(node);
        return node;
    }

    /// <summary>Selects every node.</summary>
    internal void SelectAll() => SelectOnly([.. Nodes]);

    internal void ClearSelection()
    {
        SelectedNodes.Clear();
        SelectedConnections.Clear();
    }

    /// <summary>Copies the selected nodes 30 px down and right, with the connections among them, and selects the copies.</summary>
    internal void Duplicate()
    {
        var copies = SelectedNodes.ToDictionary(node => node.Id, _ => TaskId.New());
        if (copies.Count == 0)
        {
            return;
        }

        var places = SelectedNodes.Select(node => node.CopyAs(copies[node.Id], Offset(Workflow.Positions[node.Id], DuplicateOffset))).ToList();
        var connections = Workflow.Connections
            .Where(connection => copies.ContainsKey(connection.Key.From) && copies.ContainsKey(connection.Key.To))
            .Select(connection => (WorkflowEdit)new WorkflowEdit.Connect(new(copies[connection.Key.From], copies[connection.Key.To]), connection.Value));
        if (Apply([.. places, .. connections], places))
        {
            SelectOnly([.. copies.Values.Select(id => _nodes[id])]);
        }
    }

    /// <summary>Deletes every connection that touches one of the tasks, in one edit.</summary>
    internal void Disconnect(IReadOnlyCollection<TaskId> tasks)
    {
        var touching = Workflow.Connections.Keys.Where(key => tasks.Contains(key.From) || tasks.Contains(key.To)).ToImmutableArray();
        if (!touching.IsEmpty)
        {
            Edit(new WorkflowEdit.Delete([], touching));
        }
    }

    /// <summary>Deletes every connection of the port's own side of its node.</summary>
    internal void Disconnect(PortViewModel port)
    {
        var touching = Workflow.Connections.Keys
            .Where(key => port.Side == PortSide.Output ? key.From == port.Node.Id : key.To == port.Node.Id)
            .ToImmutableArray();
        if (!touching.IsEmpty)
        {
            Edit(new WorkflowEdit.Delete([], touching));
        }
    }

    /// <summary>
    /// Puts a node of the blueprint in the node's place in one edit: the node goes first, so a review that depended on it
    /// takes the new node as its subject. A rejection changes nothing.
    /// </summary>
    internal void Replace(TaskNodeViewModel node, Blueprint blueprint)
    {
        var place = node.ReplacementAs(TaskId.New(), blueprint, Workflow.Positions[node.Id]);
        var connections = Workflow.Connections
            .Where(connection => connection.Key.From == node.Id || connection.Key.To == node.Id)
            .Select(connection => (WorkflowEdit)new WorkflowEdit.Connect(
                new(connection.Key.From == node.Id ? place.Id : connection.Key.From, connection.Key.To == node.Id ? place.Id : connection.Key.To),
                connection.Value));
        if (Apply([new WorkflowEdit.Delete([node.Id], []), place, .. connections], [place]))
        {
            Select(_nodes[place.Id]);
        }
    }

    /// <summary>The node menu for the selection as it is now.</summary>
    internal NodeMenuViewModel NodeMenu() => new(this);

    internal ConnectionMenuViewModel ConnectionMenu(ConnectionViewModel connection, CanvasPoint point) => new(this, connection, point);

    /// <summary>Whether no card covers the point, so a click or a drop there is on the canvas itself.</summary>
    internal bool IsEmptyAt(CanvasPoint point) => !Workflow.Positions.Values.Any(position =>
        point.X >= position.X && point.X < position.X + TaskCardWidth && point.Y >= position.Y && point.Y < position.Y + TaskCardHeight);

    /// <summary>While the Add popover offers a node for a dropped wire, the wire stays from its port to the drop.</summary>
    private DanglingWireViewModel? DanglingWire() => AddNode?.Target switch
    {
        AddTarget.FromOutput from when _nodes.TryGetValue(from.Source, out var source) =>
            DanglingWireViewModel.Between(source.Location + OutputPortCenter, ToPoint(from.Drop), source.Kind),
        AddTarget.ToInput to when _nodes.TryGetValue(to.Target, out var target) =>
            DanglingWireViewModel.Between(target.Location + InputPortCenter, ToPoint(to.Drop), target.Kind),
        _ => null,
    };

    // A wire dropped on empty canvas offers to add the node at its other end.
    private void DropWire(PortViewModel port)
    {
        if (View?.Pointer is { } drop && IsEmptyAt(drop))
        {
            OpenAdd(port.Side == PortSide.Output ? new AddTarget.FromOutput(port.Node.Id, drop) : new AddTarget.ToInput(port.Node.Id, drop));
        }
    }

    private CanvasPoint Position(AddTarget target) => target switch
    {
        AddTarget.AtPoint at => Offset(at.Point, -new Vector(TaskCardWidth / 2, TaskCardHeight / 2)),
        AddTarget.InView => FreeSpot(new CanvasPoint(ViewportLocation.X + 60, ViewportLocation.Y + 60)),
        AddTarget.FromOutput from => Offset(from.Drop, -(Vector)InputPortCenter),
        AddTarget.ToInput to => Offset(to.Drop, -(Vector)OutputPortCenter),
        AddTarget.Between between => Offset(between.Point, -new Vector(TaskCardWidth / 2, TaskCardHeight / 2)),
        _ => throw new UnreachableException(),
    };

    private static CanvasPoint Offset(CanvasPoint point, Vector by) => new(point.X + by.X, point.Y + by.Y);

    /// <summary>Applies the edits as one batch. A rejection names the tasks the batch adds by their new titles.</summary>
    private bool Apply(ImmutableArray<WorkflowEdit> edits, IReadOnlyList<WorkflowEdit.PlaceNode> added) =>
        Edit(new WorkflowEdit.Batch(edits), id => added.FirstOrDefault(place => place.Id == id)?.Title ?? Workflow.Tasks[id].Title)
            is EditResult.Applied;

    private void SelectOnly(IReadOnlyList<TaskNodeViewModel> nodes)
    {
        SelectedNodes.Clear();
        foreach (var node in nodes)
        {
            SelectedNodes.Add(node);
        }
    }
}
