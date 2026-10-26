using System.Diagnostics.CodeAnalysis;
using IDevelop.Desktop.Mvvm;
using IDevelop.Workflows;
using JetBrains.Annotations;

namespace IDevelop.Desktop.Conversation;

/// <summary>Names one node's conversation in an open project. A node id never moves between workflows.</summary>
internal readonly record struct ConversationKey([UsedImplicitly] WorkflowRef Workflow, [UsedImplicitly] TaskId Task);

/// <summary>
/// One node's unsent work in the conversation view: the composer text, and where the person reads. The project keeps it
/// while it is open, across node, workflow, project, and layout switches, and nothing keeps it across a restart.
/// </summary>
public sealed class ConversationState : ObservableObject
{
    [AllowNull]
    public string Draft
    {
        get;
        set => SetProperty(ref field, value ?? "");
    } = "";

    /// <summary>The line at the top of the transcript and how far into it the person scrolled, or null at the latest message.</summary>
    public (int Entry, double Offset)? Anchor { get; set; }
}
