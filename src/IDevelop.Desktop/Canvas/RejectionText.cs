using System.Diagnostics;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

public static class RejectionText
{
    public static string Describe(EditRejection reason, Workflow workflow) => Describe(reason, id => workflow.Tasks[id].Title);

    /// <param name="title">The title of a task the reason names, which may be one an edit has yet to add.</param>
    public static string Describe(EditRejection reason, Func<TaskId, string> title) => reason switch
    {
        EditRejection.SelfConnection => "A task can't connect to itself.",
        EditRejection.DuplicateConnection duplicate =>
            $"These tasks are already connected by a {duplicate.ExistingKind.ToString().ToLowerInvariant()} connection.",
        EditRejection.OrderingCycle cycle =>
            $"That would create a cycle: {string.Join(" → ", cycle.Path.Select(title))}.",
        EditRejection.UnknownTask => "That task no longer exists.",
        EditRejection.UnknownConnection => "That connection no longer exists.",
        EditRejection.TaskAlreadyExists => "A task with that id already exists.",
        EditRejection.UnknownField field => $"\"{title(field.Task)}\" has no field {field.Key}.",
        EditRejection.BlueprintConflict conflict => $"This workflow already holds a different {conflict.Key}.",
        _ => throw new UnreachableException(),
    };
}
