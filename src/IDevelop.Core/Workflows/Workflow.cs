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

public enum ConnectionKind { Dependency, Context }

public static class ConnectionKindRules
{
    public static bool Blocks(this ConnectionKind kind) => kind switch
    {
        ConnectionKind.Dependency => true,
        ConnectionKind.Context => false,
    };
}

/// <summary>The coding clients a task can run with.</summary>
public enum ClientId { ClaudeCode, Codex, Pi, Antigravity }

/// <summary>
/// The agent a task asks for. <see cref="Model"/> and <see cref="Reasoning"/> are the client's own ids, such as
/// "deepseek/deepseek-v4-pro" for Pi, or "gemini-3.8-flash" with "high" for Antigravity. Null means not chosen yet.
/// The workflow never checks them against a catalog, because a catalog belongs to one machine and the file opens on any machine.
/// </summary>
public sealed record ExecutionSettings(ClientId Client)
{
    public ClientId Client { get; } = Client;

    public string? Model { get; init => field = Blank(value); }

    public string? Reasoning { get; init => field = Blank(value); }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// A node: its id and title, the blueprint version it was placed from, a value for each of that blueprint's fields, and
/// its settings. The blueprint is the node's own embedded copy, so a later edit of a library blueprint changes nothing
/// already placed.
/// </summary>
public sealed record TaskDefinition
{
    /// <summary>A node with the blueprint's field defaults and settings.</summary>
    public TaskDefinition(TaskId id, Blueprint blueprint)
    {
        Id = id;
        Blueprint = blueprint;
        Fields = blueprint.Fields.ToImmutableSortedDictionary(field => field.Key, field => field.Default, StringComparer.Ordinal);
        Execution = blueprint.Defaults.Execution;
        Conversation = blueprint.Defaults.Conversation;
    }

    public TaskId Id { get; }

    public Blueprint Blueprint { get; }

    public string Title { get; init => field = NormalizeLineBreaks(value); } = "";

    /// <summary>One value for each of the blueprint's fields, by key.</summary>
    public ImmutableSortedDictionary<string, string> Fields { get; private init; }

    /// <summary>Null when the task has no agent yet.</summary>
    public ExecutionSettings? Execution { get; init; }

    public ConversationMode Conversation { get; init; }

    public string Field(string key) => Fields[key];

    /// <summary>Null when the blueprint has no such field. The same instance when the value does not change.</summary>
    public TaskDefinition? WithField(string key, string text)
    {
        if (!Fields.TryGetValue(key, out var current))
        {
            return null;
        }

        text = NormalizeLineBreaks(text);
        return text == current ? this : this with { Fields = Fields.SetItem(key, text) };
    }

    // The fields compare by content, which a dictionary does not.
    public bool Equals(TaskDefinition? other) =>
        other is not null && Id == other.Id && Blueprint.Equals(other.Blueprint) && Title == other.Title &&
        Fields.SequenceEqual(other.Fields) && Execution == other.Execution && Conversation == other.Conversation;

    public override int GetHashCode() => HashCode.Combine(Id, Blueprint.Key, Title);

    private static string NormalizeLineBreaks(string value) => value.Replace("\r\n", "\n").Replace('\r', '\n');
}

public readonly record struct TaskPosition(TaskId Task, CanvasPoint Position);

public abstract record WorkflowEdit
{
    private WorkflowEdit() { }

    /// <summary>
    /// Places a node of <paramref name="Blueprint"/>, which the workflow embeds while a node uses it. The node starts with
    /// the blueprint's field defaults and settings, then takes <see cref="Title"/>, each value in <see cref="Fields"/>, and
    /// <see cref="Settings"/> when set. A field the blueprint does not have is rejected.
    /// </summary>
    public sealed record PlaceNode(TaskId Id, Blueprint Blueprint, CanvasPoint Position) : WorkflowEdit
    {
        public string Title { get; init; } = "";

        public ImmutableDictionary<string, string> Fields { get; init; } = ImmutableDictionary<string, string>.Empty;

        /// <summary>Null keeps the blueprint's default settings.</summary>
        public NodeSettings? Settings { get; init; }
    }

    public sealed record EditTitle(TaskId Task, string Title) : WorkflowEdit;

    public sealed record SetField(TaskId Task, string Key, string Text) : WorkflowEdit;

    public sealed record SetConversation(TaskId Task, ConversationMode Mode) : WorkflowEdit;

    /// <summary>Sets or clears a task's agent. A running attempt keeps the settings it started with.</summary>
    public sealed record SetExecution(TaskId Task, ExecutionSettings? Execution) : WorkflowEdit;

    public sealed record MoveTasks(ImmutableArray<TaskPosition> Moves) : WorkflowEdit;

    public sealed record Connect(ConnectionKey Key, ConnectionKind Kind) : WorkflowEdit;

    public sealed record SetConnectionKind(ConnectionKey Key, ConnectionKind Kind) : WorkflowEdit;

    public sealed record Delete(ImmutableArray<TaskId> Tasks, ImmutableArray<ConnectionKey> Connections) : WorkflowEdit;

    /// <summary>Applies each edit in order, as one change: all of them, or none when one is rejected.</summary>
    public sealed record Batch(ImmutableArray<WorkflowEdit> Edits) : WorkflowEdit;
}

public abstract record EditRejection
{
    private EditRejection() { }

    public sealed record UnknownTask(TaskId Task) : EditRejection;

    public sealed record UnknownConnection(ConnectionKey Key) : EditRejection;

    public sealed record TaskAlreadyExists(TaskId Task) : EditRejection;

    /// <summary>The task's blueprint has no field with this key.</summary>
    public sealed record UnknownField(TaskId Task, string Key) : EditRejection;

    /// <summary>The workflow already embeds a different blueprint under this key.</summary>
    public sealed record BlueprintConflict(BlueprintKey Key) : EditRejection;

    public sealed record SelfConnection(TaskId Task) : EditRejection;

    public sealed record DuplicateConnection(ConnectionKey Key, ConnectionKind ExistingKind) : EditRejection;

    /// <summary>The cycle the edit would close, from the new connection's source back to it.</summary>
    public sealed record OrderingCycle(ImmutableArray<TaskId> Path) : EditRejection;

    /// <summary>The review already depends on <paramref name="Subject"/>, the one node that edits the project it reviews.</summary>
    public sealed record SecondSubject(TaskId Review, TaskId Subject) : EditRejection;
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
/// a move keeps their instances. Each node holds its blueprint, and <see cref="Blueprints"/> is the one copy of each
/// version that a node uses, so a copy goes when its last node goes.
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
        Blueprints = tasks.Values
            .Select(task => task.Blueprint)
            .DistinctBy(blueprint => blueprint.Key)
            .ToImmutableSortedDictionary(blueprint => blueprint.Key, blueprint => blueprint);
    }

    public WorkflowId Id { get; }

    public ImmutableSortedDictionary<TaskId, TaskDefinition> Tasks { get; }

    public ImmutableSortedDictionary<ConnectionKey, ConnectionKind> Connections { get; }

    public ImmutableSortedDictionary<TaskId, CanvasPoint> Positions { get; }

    public ImmutableSortedDictionary<BlueprintKey, Blueprint> Blueprints { get; }

    public static Workflow Empty(WorkflowId id) => new(
        id,
        ImmutableSortedDictionary<TaskId, TaskDefinition>.Empty,
        ImmutableSortedDictionary<ConnectionKey, ConnectionKind>.Empty,
        ImmutableSortedDictionary<TaskId, CanvasPoint>.Empty);

    public EditResult Apply(WorkflowEdit edit) => edit switch
    {
        WorkflowEdit.PlaceNode e => PlaceNode(e),
        WorkflowEdit.EditTitle e => Edit(e.Task, task => task with { Title = e.Title }),
        WorkflowEdit.SetField e => Edit(e.Task, task => task.WithField(e.Key, e.Text), new EditRejection.UnknownField(e.Task, e.Key)),
        WorkflowEdit.SetExecution e => Edit(e.Task, task => task with { Execution = e.Execution }),
        WorkflowEdit.SetConversation e => Edit(e.Task, task => task with { Conversation = e.Mode }),
        WorkflowEdit.MoveTasks e => MoveTasks(e),
        WorkflowEdit.Connect e => Connect(e),
        WorkflowEdit.SetConnectionKind e => SetConnectionKind(e),
        WorkflowEdit.Delete e => Delete(e),
        WorkflowEdit.Batch e => Batch(e),
        _ => throw new UnreachableException($"Unhandled edit {edit.GetType().Name}"),
    };

    private EditResult PlaceNode(WorkflowEdit.PlaceNode e)
    {
        if (Tasks.ContainsKey(e.Id))
        {
            return Reject(new EditRejection.TaskAlreadyExists(e.Id));
        }

        var blueprint = e.Blueprint;
        if (Blueprints.TryGetValue(blueprint.Key, out var embedded))
        {
            if (!embedded.Equals(blueprint))
            {
                return Reject(new EditRejection.BlueprintConflict(blueprint.Key));
            }

            blueprint = embedded;
        }

        var task = new TaskDefinition(e.Id, blueprint) { Title = e.Title };
        if (e.Settings is { } settings)
        {
            task = task with { Execution = settings.Execution, Conversation = settings.Conversation };
        }

        foreach (var (key, text) in e.Fields)
        {
            if (task.WithField(key, text) is not { } filled)
            {
                return Reject(new EditRejection.UnknownField(e.Id, key));
            }

            task = filled;
        }

        return Applied(new Workflow(Id, Tasks.Add(e.Id, task), Connections, Positions.Add(e.Id, e.Position)));
    }

    /// <param name="edit">Returns the edited task, or null when the edit does not fit it.</param>
    private EditResult Edit(TaskId id, Func<TaskDefinition, TaskDefinition?> edit, EditRejection? misfit = null)
    {
        if (!Tasks.TryGetValue(id, out var task))
        {
            return Reject(new EditRejection.UnknownTask(id));
        }

        if (edit(task) is not { } edited)
        {
            return Reject(misfit!);
        }

        return edited.Equals(task)
            ? Applied(this)
            : Applied(new Workflow(Id, Tasks.SetItem(id, edited), Connections, Positions));
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

        if (e.Kind == ConnectionKind.Dependency && SecondSubject(e.Key) is { } second)
        {
            return Reject(second);
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

        if (e.Kind == ConnectionKind.Dependency && SecondSubject(e.Key) is { } second)
        {
            return Reject(second);
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

    private EditResult Batch(WorkflowEdit.Batch e)
    {
        var workflow = this;
        foreach (var edit in e.Edits)
        {
            switch (workflow.Apply(edit))
            {
                case EditResult.Applied applied:
                    workflow = applied.Workflow;
                    break;
                case var rejected:
                    return rejected;
            }
        }

        return Applied(workflow);
    }

    /// <summary>
    /// The review's subject: its one dependency predecessor that edits the project. Null when the task is not a review or
    /// has none.
    /// </summary>
    public TaskId? SubjectOf(TaskId review) =>
        Tasks.TryGetValue(review, out var task) && task.Blueprint.Work is WorkSpec.Review
            ? Connections
                .Where(connection => connection.Key.To == review && connection.Value == ConnectionKind.Dependency && ProducesChange(Tasks[connection.Key.From]))
                .Select(connection => (TaskId?)connection.Key.From)
                .FirstOrDefault()
            : null;

    /// <summary>A node whose agent may edit the project, which a review can take as its subject.</summary>
    public static bool ProducesChange(TaskDefinition task) => task.Blueprint.Work is WorkSpec.Agent { Access: AgentAccess.Edit };

    /// <summary>Why a dependency would give a review a second subject, or null.</summary>
    private EditRejection? SecondSubject(ConnectionKey key) =>
        ProducesChange(Tasks[key.From]) && SubjectOf(key.To) is { } subject && subject != key.From
            ? new EditRejection.SecondSubject(key.To, subject)
            : null;

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
