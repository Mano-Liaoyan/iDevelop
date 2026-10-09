using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IDevelop.Execution;
using IDevelop.Workflows;

namespace IDevelop.Nodes;

/// <summary>Fills an empty node with a title, when the proposal gives one, and field values.</summary>
public sealed record ProposedFill(TaskId Slot, string? Title, ImmutableSortedDictionary<string, string> Fields);

/// <summary>A new node. Its id is minted from the plan and the agent's own id for it.</summary>
public sealed record ProposedNode(TaskId Id, string Name, Blueprint Blueprint, string Title, ImmutableSortedDictionary<string, string> Fields)
{
    /// <summary>
    /// The agent the planner chose for the node, or null when it chose none, as every planner before it was asked to.
    /// A run's journal leaves out a null agent, so a proposal without agents records as it always did.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProposedAgent? Agent { get; init; }
}

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
    public const double ColumnStep = 320;
    public const double RowStep = 112;

    /// <summary>
    /// Reads the latest proposal of a planner's attempt. The same attempt always gives the same proposal and the same new
    /// ids, so accepting it again places no node twice.
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
    /// Each proposed node's place. A node the workflow holds stays where it is. A new node goes into the first column,
    /// right of the planner and the slots it fills, unless it depends on other proposed nodes. Then it goes one column
    /// right of the deepest of them. Each column fills from the planner's height down, skipping any place a card takes.
    /// </summary>
    public ImmutableDictionary<TaskId, CanvasPoint> Layout(Workflow workflow)
    {
        var anchor = workflow.Positions.GetValueOrDefault(Planner);
        var firstColumn = Fills
            .Select(fill => workflow.Positions.TryGetValue(fill.Slot, out var slot) ? slot.X : anchor.X)
            .Append(anchor.X)
            .Max() + ColumnStep;
        var depths = Depths();
        var taken = workflow.Positions.Values.ToList();
        var nextY = new Dictionary<int, double>();
        var layout = ImmutableDictionary.CreateBuilder<TaskId, CanvasPoint>();
        foreach (var node in Nodes)
        {
            if (workflow.Positions.TryGetValue(node.Id, out var placed))
            {
                layout.Add(node.Id, placed);
                continue;
            }

            var depth = depths[node.Id];
            var x = firstColumn + (depth - 1) * ColumnStep;
            var y = nextY.GetValueOrDefault(depth, anchor.Y);
            while (taken.Any(card => Math.Abs(card.X - x) < ColumnStep - 20 && Math.Abs(card.Y - y) < RowStep - 16))
            {
                y += RowStep;
            }

            var place = new CanvasPoint(x, y);
            layout.Add(node.Id, place);
            taken.Add(place);
            nextY[depth] = y + RowStep;
        }

        return layout.ToImmutable();
    }

    /// <summary>
    /// The one edit that accepts the chosen items: their nodes, their fills, and each connection that touches no item the
    /// person left out. A node the workflow already holds was accepted before, so it stays as it is and its connections
    /// count, and so does a connection the workflow already holds. A slot that has started is never filled, even when
    /// chosen, and counts as left out.
    /// </summary>
    /// <param name="started">Whether a task has an attempt.</param>
    /// <param name="fallback">
    /// The agent that a new Agent or Review node takes when it has no other: no person's choice, no agent of its
    /// blueprint's own, and no planner's choice. It takes it with the blueprint's default conversation mode. Null leaves
    /// such a node without an agent. A fill never changes its node's agent.
    /// </param>
    /// <param name="planned">
    /// The planner's choice for each new Agent or Review node, one this machine can run. A node takes it only when its
    /// blueprint has no agent of its own, so a blueprint's saved agent wins, as the person chose it.
    /// </param>
    /// <param name="changed">
    /// The person's choice for each new Agent or Review node in the review, which wins over everything. A node that takes
    /// no agent, and a fill, ignore theirs.
    /// </param>
    public WorkflowEdit.Batch Accept(
        Workflow workflow, IReadOnlySet<TaskId> chosen, Func<TaskId, bool> started, ExecutionSettings? fallback = null,
        IReadOnlyDictionary<TaskId, ExecutionSettings>? planned = null, IReadOnlyDictionary<TaskId, ExecutionSettings>? changed = null)
    {
        var layout = Layout(workflow);
        var placed = Nodes.Where(node => workflow.Tasks.ContainsKey(node.Id)).Select(node => node.Id).ToHashSet();
        var unchosen = Items.Where(item => !chosen.Contains(item) && !placed.Contains(item))
            .Concat(Fills.Select(fill => fill.Slot).Where(started))
            .ToHashSet();
        bool Kept(TaskId end) => !unchosen.Contains(end);
        return new(
        [
            .. Nodes.Where(node => chosen.Contains(node.Id) && !placed.Contains(node.Id)).Select(node =>
                new WorkflowEdit.PlaceNode(node.Id, node.Blueprint, layout[node.Id])
                {
                    Title = node.Title,
                    Fields = node.Fields.ToImmutableDictionary(),
                    Settings = SettingsFor(node.Blueprint, changed?.GetValueOrDefault(node.Id), planned?.GetValueOrDefault(node.Id), fallback),
                }),
            .. Fills.Where(fill => Kept(fill.Slot)).SelectMany(Fill),
            .. Connections
                .Where(connection => Kept(connection.From) && Kept(connection.To))
                .Where(connection => !workflow.Connections.ContainsKey(new ConnectionKey(connection.From, connection.To)))
                .Select(connection => new WorkflowEdit.Connect(new ConnectionKey(connection.From, connection.To), connection.Kind)),
        ]);
    }

    /// <summary>The title the proposal gives a node it adds or fills, or null.</summary>
    public string? TitleOf(TaskId id) =>
        Nodes.FirstOrDefault(node => node.Id == id)?.Title ?? Fills.FirstOrDefault(fill => fill.Slot == id)?.Title;

    /// <summary>
    /// Each proposed node's depth: one more than the deepest proposed node it depends on, so 1 when it depends only on the
    /// planner and slots, or on nothing. Context connections add no depth. A dependency cycle, which Accept refuses, is
    /// cut where it closes.
    /// </summary>
    private Dictionary<TaskId, int> Depths()
    {
        var predecessors = Connections
            .Where(connection => connection.Kind == ConnectionKind.Dependency)
            .ToLookup(connection => connection.To, connection => connection.From);
        var proposed = Nodes.Select(node => node.Id).ToHashSet();
        var depths = new Dictionary<TaskId, int>();
        int Depth(TaskId id)
        {
            if (!proposed.Contains(id))
            {
                return 0;
            }

            if (depths.TryGetValue(id, out var known))
            {
                return known;
            }

            // A node in progress reads 0, so a cycle back to it ends there.
            depths[id] = 0;
            return depths[id] = 1 + predecessors[id].Select(Depth).DefaultIfEmpty().Max();
        }

        foreach (var node in Nodes)
        {
            Depth(node.Id);
        }

        return depths;
    }

    /// <summary>
    /// A new node's agent, first found of: the person's choice, its blueprint's own agent, the planner's choice, and the
    /// fallback. Null keeps the blueprint's defaults, as for a blueprint with an agent of its own or a node that takes none.
    /// </summary>
    private static NodeSettings? SettingsFor(Blueprint blueprint, ExecutionSettings? changed, ExecutionSettings? planned, ExecutionSettings? fallback) =>
        blueprint.Work.Kind == WorkKind.Person ? null
        : (changed ?? (blueprint.Defaults.Execution is null ? planned ?? fallback : null)) is { } agent ? blueprint.Defaults with { Execution = agent }
        : null;

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
            var named = Text(item, "slot", "fill") ?? throw new ProposalException("A fill names no slot.");
            if (Index(named, "slot-", handles.Slots.Length) is not { } index)
            {
                throw new ProposalException($"The proposal fills {named}, which is not one of the empty tasks it was given.");
            }

            var slot = PlanningContext.Slot(index);
            if (!ends.TryAdd(slot, handles.Slots[index]))
            {
                throw new ProposalException($"The proposal fills {slot} twice.");
            }

            var title = Text(item, "title", slot) is { } given && !string.IsNullOrWhiteSpace(given) ? given.Trim() : null;
            var fields = Fields(item, slot);
            if (title is null && fields.Values.All(string.IsNullOrWhiteSpace))
            {
                throw new ProposalException($"The proposal fills {slot} with nothing.");
            }

            fills.Add(new ProposedFill(handles.Slots[index], title, fields));
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
            if (name == PlannerHandle || Index(name, "slot-", handles.Slots.Length) is not null)
            {
                throw new ProposalException($"The new task {name} has the id of the planner or a slot.");
            }

            var type = Text(item, "type", name) ?? throw new ProposalException($"The new task {name} names no type.");
            if (Index(type, "type-", handles.Types.Length) is not { } index)
            {
                throw new ProposalException($"The new task {name} has type {type}, which is not one of the types it was given.");
            }

            var key = handles.Types[index];
            var found = blueprint(key) ?? throw new ProposalException($"The new task {name} is a {key}, which this machine does not have.");
            if (Fields(item, name).Keys.FirstOrDefault(field => found.Field(field) is null) is { } unknown)
            {
                throw new ProposalException($"The new task {name} has a field {unknown}, which its type {found.Name} does not have.");
            }

            var id = Mint(handles.Plan, name);
            if (!ends.TryAdd(name, id))
            {
                throw new ProposalException($"The proposal uses the id {name} twice.");
            }

            var title = Text(item, "title", name) is { } given && !string.IsNullOrWhiteSpace(given) ? given.Trim() : found.Name;
            nodes.Add(new ProposedNode(id, name, found, title, Fields(item, name)) { Agent = Agent(item, name) });
        }

        var connections = ImmutableArray.CreateBuilder<ProposedConnection>();
        var connected = new HashSet<(TaskId, TaskId)>();
        foreach (var item in Entries(block, "connect"))
        {
            var from = End(item, "from", ends, handles);
            var to = End(item, "to", ends, handles);
            if (from == to)
            {
                throw new ProposalException("A connection goes from a task to itself.");
            }

            var kind = Text(item, "kind", "connection") switch
            {
                null or "dependency" => ConnectionKind.Dependency,
                "context" => ConnectionKind.Context,
                var other => throw new ProposalException($"A connection has kind {other}, which is neither dependency nor context."),
            };
            if (connected.Add((from, to)))
            {
                connections.Add(new ProposedConnection(from, to, kind));
            }
        }

        if (fills.Count == 0 && nodes.Count == 0)
        {
            throw new ProposalException("The proposal fills and adds no task.");
        }

        return new Proposal(attempt.Task, attempt.Id, turn, fills.ToImmutable(), nodes.ToImmutable(), connections.ToImmutable());
    }

    /// <summary>
    /// A version 8 id from a hash of the plan and the agent's id for the node. Reading the same log again mints the same
    /// ids, and a later turn or continuation that proposes the node again under its id names the same node.
    /// </summary>
    internal static TaskId Mint(Guid plan, string name)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{plan}/{name}"));
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

    /// <summary>
    /// The agent a new task's entry chose, or null for none. An agent that cannot be read stays with its task as
    /// unreadable, so the person sees why it falls back, and the rest of the proposal still reads.
    /// </summary>
    private static ProposedAgent? Agent(JsonElement item, string owner)
    {
        if (!item.TryGetProperty("agent", out var agent) || agent.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (agent.ValueKind != JsonValueKind.Object)
        {
            return new ProposedAgent(null, null, null, null) { Unreadable = $"The agent of {owner} is not an object." };
        }

        string? unreadable = null;
        string? Part(string property)
        {
            if (!agent.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (value.ValueKind != JsonValueKind.String)
            {
                unreadable ??= $"The \"{property}\" of {owner}'s agent is not text.";
                return null;
            }

            return string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString()!.Trim();
        }

        return new ProposedAgent(Part("client"), Part("model"), Part("reasoning"), Part("reason")) { Unreadable = unreadable };
    }

    private static TaskId End(JsonElement item, string property, Dictionary<string, TaskId> ends, PlanningHandles handles)
    {
        var name = Text(item, property, "a connection") ?? throw new ProposalException($"A connection has no \"{property}\".");
        return ends.TryGetValue(Index(name, "slot-", handles.Slots.Length) is { } slot ? PlanningContext.Slot(slot) : name, out var id)
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
