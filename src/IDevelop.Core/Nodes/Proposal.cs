using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IDevelop.Execution;
using IDevelop.Workflows;

namespace IDevelop.Nodes;

/// <summary>Fills an empty node with a title, when the proposal gives one, and field values.</summary>
public sealed record ProposedFill(TaskId Slot, string? Title, ImmutableSortedDictionary<string, string> Fields);

/// <summary>A new node. Its id is minted from the attempt, the turn, and the agent's own id for it.</summary>
public sealed record ProposedNode(TaskId Id, string Name, Blueprint Blueprint, string Title, ImmutableSortedDictionary<string, string> Fields);

public sealed record ProposedConnection(TaskId From, TaskId To, ConnectionKind Kind);

/// <summary>What reading a planner's attempt found.</summary>
public abstract record ProposalRead
{
    private ProposalRead() { }

    /// <summary>The attempt is no planner's, or no turn of it ended with a proposal.</summary>
    public sealed record None : ProposalRead;

    /// <summary>The latest proposal could not be read. The text is for the user.</summary>
    public sealed record Problem(string Text) : ProposalRead;

    public sealed record Ready(Proposal Proposal) : ProposalRead;
}

/// <summary>
/// A planner's graph edits, read from the latest turn that ended with a proposal block. It adds nodes, fills empty
/// nodes, and adds connections, and it never deletes, moves, or retypes anything. The person accepts all of it or a
/// part, as one <see cref="WorkflowEdit.Batch"/>.
/// </summary>
public sealed record Proposal(
    TaskId Planner, AttemptId Attempt, int Turn,
    ImmutableArray<ProposedFill> Fills, ImmutableArray<ProposedNode> Nodes, ImmutableArray<ProposedConnection> Connections)
{
    /// <summary>The handle with which a proposal names its planner.</summary>
    public const string PlannerHandle = "planner";

    // A card's footprint with its gutter, so new cards never touch.
    private const double ColumnStep = 320;
    private const double RowStep = 190;

    /// <summary>
    /// Reads the latest proposal of a planner's attempt. The same attempt always gives the same proposal and the same new
    /// ids, so accepting it twice is rejected because the ids exist.
    /// </summary>
    /// <param name="blueprint">The blueprint behind a type handle's key, or null when this machine has none.</param>
    public static ProposalRead Read(AttemptRecord attempt, Func<BlueprintKey, Blueprint?> blueprint)
    {
        if (attempt.Planning is not { } handles)
        {
            return new ProposalRead.None();
        }

        foreach (var turn in attempt.Turns.Reverse())
        {
            switch (ResultBlock.Read(turn.FinalText))
            {
                case ResultBlock.Readable { Status: "proposal" } block:
                    try
                    {
                        return new ProposalRead.Ready(Parse(block.Value, attempt, turn.Number, handles, blueprint));
                    }
                    catch (ProposalException e)
                    {
                        return new ProposalRead.Problem(e.Message);
                    }

                case ResultBlock.Unreadable unreadable:
                    return new ProposalRead.Problem($"The agent's last block could not be read. {unreadable.Problem}");
            }
        }

        return new ProposalRead.None();
    }

    /// <summary>The ids of the slots it fills and the nodes it adds, in the order the agent wrote them.</summary>
    public IEnumerable<TaskId> Items => Fills.Select(fill => fill.Slot).Concat(Nodes.Select(node => node.Id));

    /// <summary>
    /// The new nodes' places: a column to the right of the planner and the slots it fills, from the planner's height
    /// down, skipping any place a card already takes.
    /// </summary>
    public ImmutableDictionary<TaskId, CanvasPoint> Layout(Workflow workflow)
    {
        var anchor = workflow.Positions.GetValueOrDefault(Planner);
        var x = Fills
            .Select(fill => workflow.Positions.TryGetValue(fill.Slot, out var slot) ? slot.X : anchor.X)
            .Append(anchor.X)
            .Max() + ColumnStep;
        var taken = workflow.Positions.Values.ToList();
        var y = anchor.Y;
        var layout = ImmutableDictionary.CreateBuilder<TaskId, CanvasPoint>();
        foreach (var node in Nodes)
        {
            while (taken.Any(card => Math.Abs(card.X - x) < ColumnStep - 20 && Math.Abs(card.Y - y) < RowStep - 16))
            {
                y += RowStep;
            }

            var place = new CanvasPoint(x, y);
            layout.Add(node.Id, place);
            taken.Add(place);
            y += RowStep;
        }

        return layout.ToImmutable();
    }

    /// <summary>
    /// The one edit that accepts the chosen items: their nodes, their fills, and each connection whose two ends are the
    /// planner or chosen. A connection the workflow already holds is left as it is.
    /// </summary>
    public WorkflowEdit.Batch Accept(Workflow workflow, IReadOnlySet<TaskId> chosen)
    {
        var layout = Layout(workflow);
        bool Kept(TaskId end) => end == Planner || chosen.Contains(end);
        return new(
        [
            .. Nodes.Where(node => chosen.Contains(node.Id)).Select(node =>
                new WorkflowEdit.PlaceNode(node.Id, node.Blueprint, layout[node.Id]) { Title = node.Title, Fields = node.Fields.ToImmutableDictionary() }),
            .. Fills.Where(fill => chosen.Contains(fill.Slot)).SelectMany(Fill),
            .. Connections
                .Where(connection => Kept(connection.From) && Kept(connection.To))
                .Where(connection => !workflow.Connections.ContainsKey(new ConnectionKey(connection.From, connection.To)))
                .Select(connection => new WorkflowEdit.Connect(new ConnectionKey(connection.From, connection.To), connection.Kind)),
        ]);
    }

    /// <summary>The title the proposal gives a node it adds or fills, or null.</summary>
    public string? TitleOf(TaskId id) =>
        Nodes.FirstOrDefault(node => node.Id == id)?.Title ?? Fills.FirstOrDefault(fill => fill.Slot == id)?.Title;

    private static IEnumerable<WorkflowEdit> Fill(ProposedFill fill) =>
    [
        .. fill.Title is { } title ? [new WorkflowEdit.EditTitle(fill.Slot, title)] : Array.Empty<WorkflowEdit>(),
        .. fill.Fields.Select(field => new WorkflowEdit.SetField(fill.Slot, field.Key, field.Value)),
    ];

    private static Proposal Parse(JsonElement block, AttemptRecord attempt, int turn, PlanningHandles handles, Func<BlueprintKey, Blueprint?> blueprint)
    {
        var ends = new Dictionary<string, TaskId>(StringComparer.Ordinal) { [PlannerHandle] = attempt.Task };
        var fills = ImmutableArray.CreateBuilder<ProposedFill>();
        foreach (var item in Entries(block, "fill"))
        {
            var slot = Text(item, "slot", "fill") ?? throw new ProposalException("A fill names no slot.");
            if (Index(slot, "slot-", handles.Slots.Length) is not { } index)
            {
                throw new ProposalException($"The proposal fills {slot}, which is not one of the empty tasks it was given.");
            }

            if (!ends.TryAdd(slot, handles.Slots[index]))
            {
                throw new ProposalException($"The proposal fills {slot} twice.");
            }

            fills.Add(new ProposedFill(handles.Slots[index], Text(item, "title", slot), Fields(item, slot)));
        }

        // Every fill's slot is an end, and so is every slot the person drew, which a connection may name without filling.
        for (var index = 0; index < handles.Slots.Length; index++)
        {
            ends.TryAdd(PlanningContext.Slot(index), handles.Slots[index]);
        }

        var nodes = ImmutableArray.CreateBuilder<ProposedNode>();
        foreach (var item in Entries(block, "add"))
        {
            var name = Text(item, "id", "new task") ?? throw new ProposalException("A new task has no id.");
            var type = Text(item, "type", name) ?? throw new ProposalException($"The new task {name} names no type.");
            if (Index(type, "type-", handles.Types.Length) is not { } index)
            {
                throw new ProposalException($"The new task {name} has type {type}, which is not one of the types it was given.");
            }

            var key = handles.Types[index];
            var found = blueprint(key) ?? throw new ProposalException($"The new task {name} is a {key}, which this machine does not have.");
            var id = Mint(attempt.Id, turn, name);
            if (!ends.TryAdd(name, id))
            {
                throw new ProposalException($"The proposal uses the id {name} twice.");
            }

            var title = Text(item, "title", name) is { } given && !string.IsNullOrWhiteSpace(given) ? given.Trim() : found.Name;
            nodes.Add(new ProposedNode(id, name, found, title, Fields(item, name)));
        }

        var connections = ImmutableArray.CreateBuilder<ProposedConnection>();
        foreach (var item in Entries(block, "connect"))
        {
            var from = End(item, "from", ends);
            var to = End(item, "to", ends);
            var kind = Text(item, "kind", "connection") switch
            {
                null or "dependency" => ConnectionKind.Dependency,
                "context" => ConnectionKind.Context,
                var other => throw new ProposalException($"A connection has kind {other}, which is neither dependency nor context."),
            };
            connections.Add(new ProposedConnection(from, to, kind));
        }

        if (fills.Count == 0 && nodes.Count == 0)
        {
            throw new ProposalException("The proposal fills and adds no task.");
        }

        return new Proposal(attempt.Task, attempt.Id, turn, fills.ToImmutable(), nodes.ToImmutable(), connections.ToImmutable());
    }

    /// <summary>
    /// A version 8 id from a hash of the attempt, the turn, and the agent's id for the node. Reading the same attempt
    /// again mints the same ids.
    /// </summary>
    internal static TaskId Mint(AttemptId attempt, int turn, string name)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{attempt}/{turn}/{name}"));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new TaskId(new Guid(hash.AsSpan(0, 16), bigEndian: true));
    }

    private static IEnumerable<JsonElement> Entries(JsonElement block, string property) =>
        !block.TryGetProperty(property, out var items) || items.ValueKind == JsonValueKind.Null ? []
        : items.ValueKind != JsonValueKind.Array ? throw new ProposalException($"The proposal's \"{property}\" is not a list.")
        : items.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.Object
            ? item
            : throw new ProposalException($"An entry of the proposal's \"{property}\" is not an object."));

    private static string? Text(JsonElement item, string property, string owner) =>
        !item.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null ? null
        : value.ValueKind == JsonValueKind.String ? value.GetString()
        : throw new ProposalException($"The \"{property}\" of {owner} is not text.");

    private static ImmutableSortedDictionary<string, string> Fields(JsonElement item, string owner)
    {
        if (!item.TryGetProperty("fields", out var fields) || fields.ValueKind == JsonValueKind.Null)
        {
            return ImmutableSortedDictionary<string, string>.Empty;
        }

        if (fields.ValueKind != JsonValueKind.Object)
        {
            throw new ProposalException($"The fields of {owner} are not an object.");
        }

        var values = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var field in fields.EnumerateObject())
        {
            values[field.Name] = field.Value.ValueKind == JsonValueKind.String
                ? field.Value.GetString()!
                : throw new ProposalException($"The field {field.Name} of {owner} is not text.");
        }

        return values.ToImmutable();
    }

    private static TaskId End(JsonElement item, string property, Dictionary<string, TaskId> ends)
    {
        var name = Text(item, property, "a connection") ?? throw new ProposalException($"A connection has no \"{property}\".");
        return ends.TryGetValue(name, out var id)
            ? id
            : throw new ProposalException($"A connection names {name}, which is not the planner, a slot, or a new task.");
    }

    /// <summary>The zero-based index of a handle such as <c>slot-2</c>, or null when it names none of the <paramref name="count"/>.</summary>
    private static int? Index(string handle, string prefix, int count) =>
        handle.StartsWith(prefix, StringComparison.Ordinal)
        && int.TryParse(handle.AsSpan(prefix.Length), System.Globalization.NumberStyles.None, null, out var number)
        && number >= 1 && number <= count
            ? number - 1
            : null;

    private sealed class ProposalException(string message) : Exception(message);
}
