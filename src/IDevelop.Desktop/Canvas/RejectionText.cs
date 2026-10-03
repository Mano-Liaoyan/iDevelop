using System.Diagnostics;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

public static class RejectionText
{
    public static string Describe(EditRejection reason, Workflow workflow) => reason switch
    {
        EditRejection.SelfConnection => "A task can't connect to itself.",
        EditRejection.DuplicateConnection duplicate =>
            $"These tasks are already connected by a {duplicate.ExistingKind.ToString().ToLowerInvariant()} connection.",
        EditRejection.OrderingCycle cycle =>
            $"That would create a cycle: {string.Join(" → ", cycle.Path.Select(id => workflow.Tasks[id].Title))}.",
        EditRejection.UnknownTask => "That task no longer exists.",
        EditRejection.UnknownConnection => "That connection no longer exists.",
        EditRejection.TaskAlreadyExists => "A task with that id already exists.",
        _ => throw new UnreachableException(),
    };
}
