using System.Collections.Immutable;
using System.Text.Json.Serialization;
using IDevelop.Workflows;

namespace IDevelop.Execution;

public readonly record struct TurnKey(AttemptId Attempt, int Number);

/// <summary>Provider ids keep numeric and string ids apart, using n: and s: prefixes.</summary>
public readonly record struct RequestKey(TurnKey Turn, string Id);

public readonly record struct EntryId(string Value);

/// <summary>An opaque position token. Pass it back without interpreting it.</summary>
public readonly record struct HistoryCursor(string Value);

/// <summary>An opaque window token used to refresh the same visible history.</summary>
public readonly record struct HistoryWindow(string Value);

public sealed record RequestDeadline(DateTimeOffset AnswerBy, DateTimeOffset StopBy);

public sealed record QuestionOption(string Id, string Label, string? Detail);

public sealed record AskedQuestion(
    string Id, string Header, string Text, ImmutableArray<QuestionOption> Options,
    bool MultiSelect, bool AllowsOther);

public sealed record QuestionAnswer(
    string QuestionId, ImmutableArray<string> OptionIds, string? Text);

public sealed record QuestionsReply(ImmutableArray<QuestionAnswer> Answers);

public sealed record PermissionAction(string Tool, string InputJson, string Scope);

public enum RequestCloseReason
{
    Resolved, TurnEnded, Stopped, Cancelled, Deferred, Unsupported,
    PolicyDenied, DeliveryUnknown, Interrupted
}

/// <summary>An open question accepts an answer until its deadline. A recorded answer stays available after closure.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(QuestionState.Open), "open")]
[JsonDerivedType(typeof(QuestionState.AnswerRecorded), "answerRecorded")]
[JsonDerivedType(typeof(QuestionState.Closed), "closed")]
public abstract record QuestionState
{
    private QuestionState() { }

    public sealed record Open(RequestDeadline Deadline) : QuestionState;

    public sealed record AnswerRecorded(QuestionsReply Reply) : QuestionState;

    public sealed record Closed(
        RequestCloseReason Reason, QuestionsReply? RecordedReply) : QuestionState;
}

/// <summary>Declining awaits the denial write. Denied confirms delivery; DeliveryUnknown cannot confirm it.</summary>
public enum PermissionState { Declining, Denied, DeliveryUnknown }

public abstract record RequestRecord(RequestKey Key)
{
    public DateTimeOffset At { get; init; }

    public sealed record Question(
        RequestKey Key, ImmutableArray<AskedQuestion> Questions, QuestionState State)
        : RequestRecord(Key);

    public sealed record Permission(
        RequestKey Key, PermissionAction Action, PermissionState State)
        : RequestRecord(Key);
}

public enum MessageAuthor { Person, Agent, Application }

public enum MessageState { Queued, Submitted, NotDelivered, Streaming, Complete, Partial }

public abstract record ConversationContent
{
    private ConversationContent() { }

    public sealed record Message(
        MessageAuthor Author, string Text, MessageState State) : ConversationContent;

    public sealed record Activity(string Text, bool IsTool) : ConversationContent;

    public sealed record Marker(string Kind, string Text) : ConversationContent;

    public sealed record Request(RequestKey Key) : ConversationContent;
}

public sealed record ConversationEntry(
    EntryId Id, TurnKey Turn, long Order, DateTimeOffset At, ConversationContent Content);

public sealed record AttemptSummary(
    AttemptId Id, AttemptId? Continues, DateTimeOffset Started,
    AttemptStatus Status, ExecutionSettings Settings);

public abstract record HistoryQuery
{
    private HistoryQuery() { }

    public sealed record Latest : HistoryQuery;

    public sealed record Before(HistoryCursor Cursor) : HistoryQuery;

    public sealed record After(HistoryCursor Cursor) : HistoryQuery;

    public sealed record AroundRequest(RequestKey Request) : HistoryQuery;

    public sealed record RefreshWindow(HistoryWindow Window) : HistoryQuery;
}

public abstract record HistoryResult
{
    private HistoryResult() { }

    public sealed record Page(
        long Revision, ImmutableArray<ConversationEntry> Entries, HistoryWindow Window,
        HistoryCursor Before, HistoryCursor After, bool HasEarlier, bool HasLater)
        : HistoryResult;

    public sealed record Unavailable(string Reason) : HistoryResult;
}

public sealed record ActionAvailability(bool Enabled, string Reason);

public sealed record ConversationActions(
    ActionAvailability Send, ActionAvailability StopAndSend,
    ActionAvailability Cancel, ActionAvailability MarkDone, ActionAvailability Terminal);

public sealed record ClientCapabilities(bool StreamsText, bool LiveQuestions, string Limitations);

public sealed record ConversationSnapshot(
    long Revision, long LogRevision, TurnKey? Current, AttemptRecord? Latest,
    ClientCapabilities Capabilities, ConversationActions Actions);

public enum AnswerOutcome { Recorded, AlreadyRecorded, Stale, Invalid, Unavailable, DeliveryUnknown }

public sealed record AnswerResult(AnswerOutcome Outcome, string Detail);

public enum CommandOutcome { Applied, Stale, Unavailable, Refused }

public sealed record ConversationCommandResult(CommandOutcome Outcome, string Detail);

public interface IConversationSession : IDisposable
{
    TaskId Task { get; }

    ConversationSnapshot Snapshot { get; }

    /// <summary>Invalidates the snapshot and history. Notifications may arrive off the UI thread.</summary>
    event Action<long>? Changed;

    Task<ImmutableArray<AttemptSummary>> ListAttemptsAsync(CancellationToken ct);

    Task<HistoryResult> ReadPageAsync(
        AttemptId head, HistoryQuery query, int count, CancellationToken ct);

    Task<RequestRecord?> ReadRequestAsync(RequestKey key, CancellationToken ct);

    Task<SendResult> SendAsync(TurnKey expected, string text, bool stopTurn, CancellationToken ct);

    Task<AnswerResult> AnswerAsync(RequestKey key, QuestionsReply reply, CancellationToken ct);

    Task<ConversationCommandResult> CancelAsync(TurnKey expected, CancellationToken ct);

    Task<ConversationCommandResult> MarkDoneAsync(TurnKey expected, CancellationToken ct);

    Task<TerminalResult> OpenInTerminalAsync(TurnKey expected, CancellationToken ct);
}

public abstract record HostQuestions
{
    private HostQuestions() { }

    public sealed record Disabled : HostQuestions;

    public sealed record DeferImmediately : HostQuestions;

    public sealed record Bounded(TimeSpan AnswerTime, TimeSpan ShutdownTime) : HostQuestions
    {
        /// <summary>The single default for the host question answer and shutdown timeouts.</summary>
        public static Bounded Recommended { get; } = new(TimeSpan.FromSeconds(55), TimeSpan.FromSeconds(5));
    }
}

public readonly record struct TurnRequestId(int Turn, string Id);

public sealed record QueuedMessage(string Id, string Text);
