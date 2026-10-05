using System.Collections.Immutable;
using System.Text;
using IDevelop.Workflows;

namespace IDevelop.Nodes;

/// <summary>
/// What a planner's proposal may name: the empty nodes after it as <c>slot-1</c> and on, and the blueprints it may place
/// as <c>type-1</c> and on. Its attempt records them when it starts, so a proposal reads each handle as the agent read
/// it, whatever the person changes meanwhile. <paramref name="Plan"/> is new at each start and goes with every
/// continuation of the session, so a node the agent proposes again under its own id keeps one id.
/// </summary>
public sealed record PlanningHandles(Guid Plan, ImmutableArray<TaskId> Slots, ImmutableArray<BlueprintKey> Types)
{
    // ImmutableArray compares by reference.
    public bool Equals(PlanningHandles? other) =>
        other is not null && Plan == other.Plan && Slots.SequenceEqual(other.Slots) && Types.SequenceEqual(other.Types);

    public override int GetHashCode() => HashCode.Combine(Slots.Length, Types.Length);
}

/// <summary>The empty nodes after a planner and the blueprints it may place, which its prompt lists by handle.</summary>
public sealed record PlanningContext(ImmutableArray<TaskDefinition> Slots, ImmutableArray<Blueprint> Types)
{
    public static readonly PlanningContext None = new([], []);

    public PlanningHandles Handles(Guid plan) => new(plan, [.. Slots.Select(slot => slot.Id)], [.. Types.Select(type => type.Key)]);

    /// <summary>
    /// The slots are the nodes that the planner's dependency connections reach, whose fields are all blank, and that have
    /// not started, in the order of their ids.
    /// </summary>
    /// <param name="placeable">The blueprints a person can place, in the order a palette lists them.</param>
    /// <param name="started">Whether a task has an attempt.</param>
    public static PlanningContext For(Workflow workflow, TaskId planner, IEnumerable<Blueprint> placeable, Func<TaskId, bool> started)
    {
        var successors = workflow.Connections.Where(connection => connection.Value.Blocks()).ToLookup(connection => connection.Key.From, connection => connection.Key.To);
        var reached = new HashSet<TaskId>();
        var queue = new Queue<TaskId>([planner]);
        while (queue.TryDequeue(out var task))
        {
            foreach (var next in successors[task])
            {
                if (reached.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }

        return new([.. reached.Order().Where(id => !started(id)).Select(id => workflow.Tasks[id]).Where(IsEmpty)], [.. placeable]);
    }

    public static bool IsEmpty(TaskDefinition node) => node.Fields.Values.All(string.IsNullOrWhiteSpace);

    public static string Slot(int index) => $"slot-{index + 1}";

    public static string Type(int index) => $"type-{index + 1}";

    /// <summary>What a planner reads after its template: how to write a proposal, then each slot and type.</summary>
    internal string Contract(ConversationMode mode)
    {
        var text = new StringBuilder();
        text.Append(mode == ConversationMode.Chat
            ? "End every message with a proposal block that holds your whole plan as it stands, so the person sees it after each reply."
            : "When your plan is ready, end your final message with one proposal block.");
        text.Append(" Nothing comes after the block. The person accepts the tasks they want from it:\n\n");
        text.Append("```idevelop\n");
        text.Append("{\"status\": \"proposal\",\n");
        text.Append(" \"fill\": [{\"slot\": \"slot-1\", \"title\": \"...\", \"fields\": {\"key\": \"...\"}}],\n");
        text.Append(" \"add\": [{\"id\": \"new-1\", \"type\": \"type-1\", \"title\": \"...\", \"fields\": {\"key\": \"...\"}}],\n");
        text.Append(" \"connect\": [{\"from\": \"planner\", \"to\": \"new-1\"}]}\n");
        text.Append("```\n\n");
        text.Append("- \"fill\" gives work to an empty task that the person drew after you, by its slot.\n");
        text.Append("- \"add\" creates a task of one of the types below. Give each one an id of your own, such as new-1.\n");
        text.Append("- \"connect\" makes \"to\" wait for \"from\" and receive its result. Add \"kind\": \"context\" when \"to\" only reads the latest result of \"from\" and does not wait. \"from\" and \"to\" each name a slot, an id from \"add\", or \"planner\", which is you.\n");
        text.Append("- Write each field under its key, and leave out a field you have nothing for. Do not choose agents or models. The person does.\n\n");
        text.Append("Empty tasks you may fill:\n");
        if (Slots.IsEmpty)
        {
            text.Append("- None.\n");
        }

        foreach (var (slot, index) in Slots.Select((slot, index) => (slot, index)))
        {
            var title = string.IsNullOrWhiteSpace(slot.Title) ? "untitled" : $"\"{slot.Title.Trim()}\"";
            text.Append($"- {Slot(index)}: {title}, type {slot.Blueprint.Name}. Fields: {FieldList(slot.Blueprint)}.\n");
        }

        text.Append("\nTypes you may add:\n");
        if (Types.IsEmpty)
        {
            text.Append("- None.\n");
        }

        foreach (var (type, index) in Types.Select((type, index) => (type, index)))
        {
            var description = string.IsNullOrWhiteSpace(type.Description) ? "" : $" {type.Description.Trim()}";
            text.Append($"- {Type(index)}: {type.Name}.{description} Fields: {FieldList(type)}.\n");
        }

        return text.ToString().TrimEnd();
    }

    private static string FieldList(Blueprint blueprint) => blueprint.Fields.IsEmpty
        ? "none"
        : string.Join(", ", blueprint.Fields.Select(field => $"{field.Key} ({field.Label}{(field.Required ? ", required" : "")})"));
}
