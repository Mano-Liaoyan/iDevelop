using System.Collections.Immutable;
using System.Text.Json.Serialization;
using JetBrains.Annotations;

namespace IDevelop.Clients;

/// <summary>What a client said, normalized. Interpreters produce these, and the node log records them.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SessionStarted), "sessionStarted")]
[JsonDerivedType(typeof(Reported), "reported")]
[JsonDerivedType(typeof(Message), "message")]
[JsonDerivedType(typeof(ToolStarted), "toolStarted")]
[JsonDerivedType(typeof(Notice), "notice")]
[JsonDerivedType(typeof(Succeeded), "succeeded")]
[JsonDerivedType(typeof(Failed), "failed")]
[JsonDerivedType(typeof(MessageDelta), "messageDelta")]
[JsonDerivedType(typeof(QuestionAsked), "questionAsked")]
[JsonDerivedType(typeof(PermissionRequested), "permissionRequested")]
[JsonDerivedType(typeof(RequestClosed), "requestClosed")]
[JsonDerivedType(typeof(BackgroundTasks), "backgroundTasks")]
internal abstract record AgentEvent
{
    private AgentEvent() { }

    public sealed record SessionStarted(string SessionId) : AgentEvent;

    /// <summary>The model or reasoning level the client says it used. A null field leaves the previous value.</summary>
    public sealed record Reported([UsedImplicitly] string? Model, [UsedImplicitly] string? Reasoning) : AgentEvent;

    /// <summary>A complete assistant message. The last one is the result when the verdict carries no text.</summary>
    public sealed record Message(string Text) : AgentEvent
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Id { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Partial { get; init; }

        /// <summary>The subagent that wrote it, or null when the task's own agent did.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Subagent { get; init; }
    }

    public sealed record MessageDelta(string MessageId, string Text) : AgentEvent;

    public sealed record QuestionAsked(string RequestId, ImmutableArray<AskedQuestion> Questions) : AgentEvent;

    public sealed record PermissionRequested(string RequestId, PermissionAction Action) : AgentEvent;

    public sealed record RequestClosed([UsedImplicitly] string RequestId) : AgentEvent;

    public sealed record ToolStarted(string Tool, string? Detail) : AgentEvent;

    /// <summary>A diagnostic that does not decide the outcome.</summary>
    public sealed record Notice(string Text) : AgentEvent;

    /// <summary>The client's own verdict. A success still needs exit code 0 to count.</summary>
    public sealed record Succeeded(string? Result) : AgentEvent;

    public sealed record Failed(string Reason) : AgentEvent;

    /// <summary>
    /// The work the client still does in the background for this turn, which it answers before it exits. It replaces the
    /// previous list; an empty one says none is left.
    /// </summary>
    public sealed record BackgroundTasks(ImmutableArray<BackgroundTask> Tasks) : AgentEvent
    {
        public bool Equals(BackgroundTasks? other) => other is not null && Tasks.SequenceEqual(other.Tasks);

        public override int GetHashCode() => Tasks.Length;
    }
}

/// <summary>Work a client does in the background for a turn: a subagent, or another task such as a background command.</summary>
public sealed record BackgroundTask(string Description, bool IsSubagent);
