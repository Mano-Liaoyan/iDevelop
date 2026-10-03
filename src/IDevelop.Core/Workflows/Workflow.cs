using System.Collections.Immutable;
using System.Diagnostics;

namespace IDevelop.Workflows;

public readonly record struct TaskId(Guid Value) : IComparable<TaskId>
{
    public static TaskId New() => new(Guid.CreateVersion7());

    public int CompareTo(TaskId other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString("D");
}

public readonly record struct WorkflowId(Guid Value)
{
    public static WorkflowId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct ConnectionKey(TaskId From, TaskId To) : IComparable<ConnectionKey>
{
    public int CompareTo(ConnectionKey other)
    {
        var byFrom = From.CompareTo(other.From);
        return byFrom != 0 ? byFrom : To.CompareTo(other.To);
    }
}

public readonly record struct CanvasPoint(double X, double Y);

public enum ConnectionKind { Dependency, Context, Review }

public static class ConnectionKindRules
{
    public static bool Blocks(this ConnectionKind kind) => kind switch
    {
        ConnectionKind.Dependency => true,
        ConnectionKind.Review => true,
        ConnectionKind.Context => false,
    };
}

public enum TaskField { Title, Instructions, AcceptanceCriteria }

public sealed record TaskDefinition(TaskId Id)
{
    public TaskId Id { get; } = Id;

    public string Title { get; init => field = NormalizeLineBreaks(value); } = "";

    public string Instructions { get; init => field = NormalizeLineBreaks(value); } = "";

    public string AcceptanceCriteria { get; init => field = NormalizeLineBreaks(value); } = "";

    internal TaskDefinition With(TaskField which, string text) => which switch
    {
        TaskField.Title => this with { Title = text },
        TaskField.Instructions => this with { Instructions = text },
        TaskField.AcceptanceCriteria => this with { AcceptanceCriteria = text },
    };

    private static string NormalizeLineBreaks(string value) => value.Replace("\r\n", "\n").Replace('\r', '\n');
}

public readonly record struct TaskPosition(TaskId Task, CanvasPoint Position);

public abstract record WorkflowEdit
{
    private WorkflowEdit() { }

    public sealed record CreateTask(TaskDefinition Task, CanvasPoint Position) : WorkflowEdit;

    public sealed record EditTask(TaskId Task, TaskField Field, string Text) : WorkflowEdit;

    public sealed record MoveTasks(ImmutableArray<TaskPosition> Moves) : WorkflowEdit;

    public sealed record Connect(ConnectionKey Key, ConnectionKind Kind) : WorkflowEdit;

    public sealed record SetConnectionKind(ConnectionKey Key, ConnectionKind Kind) : WorkflowEdit;

    public sealed record Delete(ImmutableArray<TaskId> Tasks, ImmutableArray<ConnectionKey> Connections) : WorkflowEdit;
}

public abstract record EditRejection
{
    private EditRejection() { }

    public sealed record UnknownTask(TaskId Task) : EditRejection;

    public sealed record UnknownConnection(ConnectionKey Key) : EditRejection;

    public sealed record TaskAlreadyExists(TaskId Task) : EditRejection;

    public sealed record SelfConnection(TaskId Task) : EditRejection;

    public sealed record DuplicateConnection(ConnectionKey Key, ConnectionKind ExistingKind) : EditRejection;

    /// <summary>The cycle the edit would close, from the new connection's source back to it.</summary>
    public sealed record OrderingCycle(ImmutableArray<TaskId> Path) : EditRejection;
}

public abstract record EditResult
{
    private EditResult() { }

    /// <summary>An edit with no effect returns the same workflow instance.</summary>
    public sealed record Applied(Workflow Workflow) : EditResult;

    public sealed record Rejected(EditRejection Reason) : EditResult;
}

/// <summary>
/// An immutable workflow snapshot. Only <see cref="Empty"/> and <see cref="Apply"/> create one,
/// so every instance satisfies the graph rules. Layout lives apart from tasks and connections:
/// a move keeps their instances.
/// </summary>
public sealed class Workflow
{
    private Workflow(
        WorkflowId id,
        ImmutableSortedDictionary<TaskId, TaskDefinition> tasks,
        ImmutableSortedDictionary<ConnectionKey, ConnectionKind> connections,
        ImmutableSortedDictionary<TaskId, CanvasPoint> positions)
    {
        Id = id;
        Tasks = tasks;
        Connections = connections;
        Positions = positions;
    }

    public WorkflowId Id { get; }

    public ImmutableSortedDictionary<TaskId, TaskDefinition> Tasks { get; }

    public ImmutableSortedDictionary<ConnectionKey, ConnectionKind> Connections { get; }

    public ImmutableSortedDictionary<TaskId, CanvasPoint> Positions { get; }

    public static Workflow Empty(WorkflowId id) => new(
        id,
        ImmutableSortedDictionary<TaskId, TaskDefinition>.Empty,
        ImmutableSortedDictionary<ConnectionKey, ConnectionKind>.Empty,
        ImmutableSortedDictionary<TaskId, CanvasPoint>.Empty);

    public EditResult Apply(WorkflowEdit edit) => edit switch
    {
        WorkflowEdit.CreateTask e => CreateTask(e),
        WorkflowEdit.EditTask e => EditTask(e),
        WorkflowEdit.MoveTasks e => MoveTasks(e),
        WorkflowEdit.Connect e => Connect(e),
        WorkflowEdit.SetConnectionKind e => SetConnectionKind(e),
        WorkflowEdit.Delete e => Delete(e),
        _ => throw new UnreachableException($"Unhandled edit {edit.GetType().Name}"),
    };

    private EditResult CreateTask(WorkflowEdit.CreateTask e)
    {
        var id = e.Task.Id;
        if (Tasks.ContainsKey(id))
        {
            return Reject(new EditRejection.TaskAlreadyExists(id));
        }

        return Applied(new Workflow(Id, Tasks.Add(id, e.Task), Connections, Positions.Add(id, e.Position)));
    }

    private EditResult EditTask(WorkflowEdit.EditTask e)
    {
        if (!Tasks.TryGetValue(e.Task, out var task))
        {
            return Reject(new EditRejection.UnknownTask(e.Task));
        }

        var edited = task.With(e.Field, e.Text);
        return edited == task
            ? Applied(this)
            : Applied(new Workflow(Id, Tasks.SetItem(e.Task, edited), Connections, Positions));
    }

    private EditResult MoveTasks(WorkflowEdit.MoveTasks e)
    {
        var positions = Positions;
        foreach (var move in e.Moves)
        {
            if (positions.TryGetValue(move.Task, out var current) && current != move.Position)
            {
                positions = positions.SetItem(move.Task, move.Position);
            }
        }

        return ReferenceEquals(positions, Positions)
            ? Applied(this)
            : Applied(new Workflow(Id, Tasks, Connections, positions));
    }

    private EditResult Connect(WorkflowEdit.Connect e)
    {
        var (from, to) = e.Key;
        if (from == to)
        {
            return Reject(new EditRejection.SelfConnection(from));
        }

        if (!Tasks.ContainsKey(from) || !Tasks.ContainsKey(to))
        {
            return Reject(new EditRejection.UnknownTask(Tasks.ContainsKey(from) ? to : from));
        }

        if (Connections.TryGetValue(e.Key, out var existing))
        {
            return Reject(new EditRejection.DuplicateConnection(e.Key, existing));
        }

        if (e.Kind.Blocks() && FindOrderingPath(to, from) is { } path)
        {
            return Reject(new EditRejection.OrderingCycle([from, .. path]));
        }

        return Applied(new Workflow(Id, Tasks, Connections.Add(e.Key, e.Kind), Positions));
    }

    private EditResult SetConnectionKind(WorkflowEdit.SetConnectionKind e)
    {
        if (!Connections.TryGetValue(e.Key, out var current))
        {
            return Reject(new EditRejection.UnknownConnection(e.Key));
        }

        if (current == e.Kind)
        {
            return Applied(this);
        }

        if (e.Kind.Blocks() && !current.Blocks() && FindOrderingPath(e.Key.To, e.Key.From) is { } path)
        {
            return Reject(new EditRejection.OrderingCycle([e.Key.From, .. path]));
        }

        return Applied(new Workflow(Id, Tasks, Connections.SetItem(e.Key, e.Kind), Positions));
    }

    private EditResult Delete(WorkflowEdit.Delete e)
    {
        var tasks = e.Tasks.Where(Tasks.ContainsKey).ToHashSet();
        var connections = Connections.Keys
            .Where(key => tasks.Contains(key.From) || tasks.Contains(key.To))
            .Concat(e.Connections.Where(Connections.ContainsKey))
            .ToHashSet();
        if (tasks.Count == 0 && connections.Count == 0)
        {
            return Applied(this);
        }

        return Applied(new Workflow(
            Id,
            Tasks.RemoveRange(tasks),
            Connections.RemoveRange(connections),
            Positions.RemoveRange(tasks)));
    }

    private ImmutableArray<TaskId>? FindOrderingPath(TaskId start, TaskId goal)
    {
        var successors = Connections.Where(c => c.Value.Blocks()).ToLookup(c => c.Key.From, c => c.Key.To);
        var parents = new Dictionary<TaskId, TaskId> { [start] = start };
        var queue = new Queue<TaskId>([start]);
        while (queue.TryDequeue(out var task))
        {
            if (task == goal)
            {
                var path = new List<TaskId> { goal };
                while (path[^1] != start)
                {
                    path.Add(parents[path[^1]]);
                }

                path.Reverse();
                return [.. path];
            }

            foreach (var successor in successors[task])
            {
                if (parents.TryAdd(successor, task))
                {
                    queue.Enqueue(successor);
                }
            }
        }

        return null;
    }

    private static EditResult.Applied Applied(Workflow workflow) => new(workflow);

    private static EditResult.Rejected Reject(EditRejection reason) => new(reason);
}
