using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace IDevelop.Execution;

/// <summary>What a client said, normalized. Interpreters produce these, and only the attempt reducer consumes them.</summary>
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
internal abstract record AgentEvent
{
    private AgentEvent() { }

    public sealed record SessionStarted(string SessionId) : AgentEvent;

    /// <summary>The model or reasoning level the client says it used. A null field leaves the previous value.</summary>
    public sealed record Reported(string? Model, string? Reasoning) : AgentEvent;

    /// <summary>A complete assistant message. The last one is the result when the verdict carries no text.</summary>
    public sealed record Message(string Text) : AgentEvent
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Id { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Partial { get; init; }
    }

    public sealed record MessageDelta(string MessageId, string Text) : AgentEvent;

    public sealed record QuestionAsked(string RequestId, ImmutableArray<AskedQuestion> Questions) : AgentEvent;

    public sealed record PermissionRequested(string RequestId, PermissionAction Action) : AgentEvent;

    public sealed record RequestClosed(string RequestId) : AgentEvent;

    public sealed record ToolStarted(string Tool, string? Detail) : AgentEvent;

    /// <summary>A diagnostic that does not decide the outcome.</summary>
    public sealed record Notice(string Text) : AgentEvent;

    /// <summary>The client's own verdict. A success still needs exit code 0 to count.</summary>
    public sealed record Succeeded(string? Result) : AgentEvent;

    public sealed record Failed(string Reason) : AgentEvent;
}
