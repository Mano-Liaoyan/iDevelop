using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.TestSupport;

/// <summary>Nodes of the built-in Implement, the only built-in blueprint so far.</summary>
internal static class TestNodes
{
    public static TaskDefinition Implement(
        TaskId id, string title = "", string instructions = "", string acceptanceCriteria = "", ExecutionSettings? execution = null,
        ConversationMode conversation = ConversationMode.Autonomous) =>
        new TaskDefinition(id, BuiltInBlueprints.Implement) { Title = title, Execution = execution, Conversation = conversation }
            .WithField("instructions", instructions)!
            .WithField("acceptanceCriteria", acceptanceCriteria)!;

    /// <summary>The edit that places <paramref name="task"/> as it is.</summary>
    public static WorkflowEdit.PlaceNode Place(TaskDefinition task, CanvasPoint position) => new(task.Id, task.Blueprint, position)
    {
        Title = task.Title,
        Fields = task.Fields.ToImmutableDictionary(),
        Settings = new NodeSettings(task.Execution, task.Conversation),
    };
}
