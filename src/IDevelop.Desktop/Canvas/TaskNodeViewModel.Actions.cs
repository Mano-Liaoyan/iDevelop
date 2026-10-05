using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

/// <summary>The edits that the canvas's node actions build from one node.</summary>
public sealed partial class TaskNodeViewModel
{
    /// <summary>The blueprint the node was placed from, as the workflow embeds it.</summary>
    internal Blueprint Blueprint => _task.Blueprint;

    /// <summary>A copy of the node titled "{Title} copy", with its field values and settings.</summary>
    internal WorkflowEdit.PlaceNode CopyAs(TaskId id, CanvasPoint position) => new(id, _task.Blueprint, position)
    {
        Title = $"{_task.Title} copy",
        Fields = _task.Fields.ToImmutableDictionary(),
        Settings = new NodeSettings(_task.Execution, _task.Conversation),
    };

    /// <summary>
    /// A node of <paramref name="blueprint"/> in this node's place: its title, the field values whose keys the blueprint
    /// also has, and its agent when the new work takes one.
    /// </summary>
    internal WorkflowEdit.PlaceNode ReplacementAs(TaskId id, Blueprint blueprint, CanvasPoint position) => new(id, blueprint, position)
    {
        Title = _task.Title,
        Fields = _task.Fields.Where(field => blueprint.Fields.Any(spec => spec.Key == field.Key)).ToImmutableDictionary(),
        Settings = blueprint.Work is WorkSpec.Person
            ? null
            : blueprint.Defaults with { Execution = _task.Execution ?? blueprint.Defaults.Execution },
    };
}
