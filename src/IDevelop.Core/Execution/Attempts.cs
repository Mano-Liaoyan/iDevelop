using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using IDevelop.Nodes;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>
/// One execution of one task. Version 7, so attempt folders sort by the millisecond they were created in. Two ids
/// created in the same millisecond sort in random order.
/// </summary>
public readonly record struct AttemptId(Guid Value)
{
    public static AttemptId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

/// <summary>
/// WaitingForInput holds no process and no lock: the latest turn ended with the node waiting for the person, who replies,
/// marks it done, or cancels it. InReview also holds none: a review's reviewer turn ended, and its subject fixes the
/// findings, or iDevelop starts the reviewer's next turn or settles the review.
/// </summary>
public enum AttemptStatus { Running, Succeeded, Failed, Cancelled, Interrupted, WaitingForInput, InReview }

/// <summary>How one client process ended. Stopped means the person stopped it, with Stop and send or Cancel.</summary>
public enum TurnOutcome { Running, Succeeded, Failed, Stopped, Interrupted, Deferred }

/// <summary>One line of what an attempt did. <paramref name="IsTool"/> marks a tool call, such as a command, rather than what the agent or iDevelop said.</summary>
public sealed record ActivityLine(DateTimeOffset At, string Text, bool IsTool = false);

/// <summary>
/// One client process of an attempt. <see cref="Message"/> is what the person sent, which is the turn's whole prompt,
/// and null for a first turn whose prompt is the task. <see cref="FinalText"/> is null while the turn runs.
/// <see cref="Detail"/> says why the turn failed or was interrupted, in the client's words when it gave any.
/// </summary>
public sealed record TurnRecord(int Number, string? Message, TurnOutcome Outcome, string? FinalText, string? Detail = null)
{
    /// <summary>The project's files as a Git tree when the turn started, or null outside Git.</summary>
    public string? StartTree { get; init; }

    /// <summary>The project's files as a Git tree when the turn ended, or null while it runs or outside Git.</summary>
    public string? EndTree { get; init; }

    /// <summary>A review's turn after a fix round: what the subject reported in that round.</summary>
    public FixReport? Report { get; init; }

    public ImmutableArray<TurnRequestId> Replies { get; init; } = [];
}

/// <summary>
/// Marks an attempt of a review's subject as fix round <paramref name="Round"/> of the review attempt
/// <paramref name="Attempt"/>. <paramref name="Guidance"/> counts the review's guidance notes up to this round, which
/// the round's prompt carried the rest of.
/// </summary>
public sealed record ReviewLink(TaskId Review, AttemptId Attempt, int Round, int Guidance);

/// <summary>What the subject's fix attempt reported, which the reviewer's next turn reads.</summary>
public sealed record FixReport(AttemptId Attempt, string? Text, int Guidance);

/// <summary>The person's guidance to a review. <paramref name="AfterTurn"/> is the number of reviewer turns requested before it.</summary>
public sealed record GuidanceNote(DateTimeOffset At, string Text, int AfterTurn);

/// <summary>The settled attempt whose client session an attempt resumes, and that session's id.</summary>
public sealed record Continuation(AttemptId Attempt, string Session);

/// <summary>
/// The command a person copied to open the attempt's session in the client's own terminal interface. It changes into
/// <see cref="Folder"/>, the project folder, first.
/// </summary>
public sealed record TerminalHandoff(DateTimeOffset At, string Folder, string Command);

/// <summary>The attempt log's vocabulary. Only the record folded from it is public.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Requested), "requested")]
[JsonDerivedType(typeof(Launched), "launched")]
[JsonDerivedType(typeof(LaunchFailed), "launchFailed")]
[JsonDerivedType(typeof(Agent), "agent")]
[JsonDerivedType(typeof(CancelRequested), "cancelRequested")]
[JsonDerivedType(typeof(InterruptRequested), "interruptRequested")]
[JsonDerivedType(typeof(Exited), "exited")]
[JsonDerivedType(typeof(Reconciled), "reconciled")]
[JsonDerivedType(typeof(MessageQueued), "messageQueued")]
[JsonDerivedType(typeof(TurnRequested), "turnRequested")]
[JsonDerivedType(typeof(HandedToTerminal), "handedToTerminal")]
[JsonDerivedType(typeof(MarkedDone), "markedDone")]
[JsonDerivedType(typeof(GuidanceAdded), "guidanceAdded")]
[JsonDerivedType(typeof(Concluded), "concluded")]
[JsonDerivedType(typeof(QuestionRecorded), "questionRecorded")]
[JsonDerivedType(typeof(RequestAnswered), "requestAnswered")]
[JsonDerivedType(typeof(RequestClosed), "requestClosed")]
[JsonDerivedType(typeof(RequestDeferred), "requestDeferred")]
[JsonDerivedType(typeof(ShutdownForced), "shutdownForced")]
internal abstract record AttemptEvent([property: JsonPropertyOrder(-1)] DateTimeOffset At)
{
    /// <summary>
    /// Always the first line, and the request of the first turn. A snapshot, so a later edit of the task never changes
    /// what this attempt ran.
    /// </summary>
    public sealed record Requested(
        DateTimeOffset At, AttemptId Attempt, TaskId Task, string TaskTitle, ExecutionSettings Settings,
        string Prompt, string Command, ImmutableArray<string> Arguments) : AttemptEvent(At)
    {
        /// <summary>The task as a fresh standalone attempt ran it, which a run can check before reusing the report. Null in older logs.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonElement? StandaloneCapture { get; init; }

        /// <summary>The run that owns the attempt. Null for a standalone attempt.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public RunBinding? RunBinding { get; init; }

        /// <summary>Set when the first turn resumes an earlier attempt's session, and then its prompt is the person's message.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Continuation? Continues { get; init; }

        /// <summary>Null in logs written before conversation modes, which never waited.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ConversationMode? Conversation { get; init; }

        /// <summary>The handles a planner's prompt listed. Null for a node that proposes nothing.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public PlanningHandles? Planning { get; init; }

        /// <summary>The project's files as a Git tree before the first turn started. Null outside Git and in older logs.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Tree { get; init; }

        /// <summary>The turns run in the client's read-only mode, so a turn that changes the project's files fails.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool ReadOnly { get; init; }

        /// <summary>Set on a review's attempt: the node whose change it reviews.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public TaskId? Subject { get; init; }

        /// <summary>Set on an attempt of a review's subject that fixes a round of the review's findings.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ReviewLink? Fix { get; init; }
    }

    public sealed record Launched(DateTimeOffset At, int ProcessId, DateTimeOffset ProcessStarted) : AttemptEvent(At);

    public sealed record LaunchFailed(DateTimeOffset At, string Reason) : AttemptEvent(At);

    public sealed record Agent(DateTimeOffset At, AgentEvent Event) : AttemptEvent(At)
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? Order { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? PresentationSequence { get; init; }
    }

    public sealed record QuestionRecorded(
        DateTimeOffset At, string RequestId, ImmutableArray<AskedQuestion> Questions, QuestionState State) : AttemptEvent(At);

    public sealed record RequestAnswered(DateTimeOffset At, string RequestId, QuestionsReply Reply) : AttemptEvent(At);

    public sealed record RequestClosed(DateTimeOffset At, string RequestId, RequestCloseReason Reason) : AttemptEvent(At);

    public sealed record RequestDeferred(DateTimeOffset At, ImmutableArray<string> RequestIds, string Question) : AttemptEvent(At);

    public sealed record ShutdownForced(DateTimeOffset At) : AttemptEvent(At);

    public sealed record CancelRequested(DateTimeOffset At) : AttemptEvent(At);

    /// <summary>Leaving the project: opening another folder or closing the window.</summary>
    public sealed record InterruptRequested(DateTimeOffset At, string Reason) : AttemptEvent(At);

    public sealed record Exited(DateTimeOffset At, int ExitCode, string StderrTail) : AttemptEvent(At)
    {
        /// <summary>The project's files as a Git tree after the client exited. Null outside Git and in older logs.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Tree { get; init; }
    }

    /// <summary>Written by whoever next takes the run lock and finds the attempt still running. Null when it never launched.</summary>
    public sealed record Reconciled(DateTimeOffset At, ProcessMatch? Process) : AttemptEvent(At);

    /// <summary>A message sent during a turn or while the attempt waits. <paramref name="StopsTurn"/> ends a running turn.</summary>
    public sealed record MessageQueued(DateTimeOffset At, string Text, bool StopsTurn) : AttemptEvent(At)
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Id { get; init; }
    }

    /// <summary>A turn after the first, which resumes the session with the waiting messages. Launched or LaunchFailed follows it.</summary>
    public sealed record TurnRequested(DateTimeOffset At, string Prompt, string Command, ImmutableArray<string> Arguments) : AttemptEvent(At)
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ImmutableArray<string> Consumed { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ImmutableArray<TurnRequestId> Replies { get; init; }

        /// <summary>The node's conversation mode from this turn on, when the person changed it while the node waited.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ConversationMode? Conversation { get; init; }

        /// <summary>The project's files as a Git tree before the turn started.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Tree { get; init; }

        /// <summary>A review's turn after a fix round: what the subject reported in that round.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public FixReport? Report { get; init; }
    }

    /// <summary>The person ended a waiting attempt as done.</summary>
    public sealed record MarkedDone(DateTimeOffset At) : AttemptEvent(At);

    /// <summary>Appended to a settled attempt. Turns the person takes in the terminal are not in this log.</summary>
    public sealed record HandedToTerminal(DateTimeOffset At, string Folder, string Command) : AttemptEvent(At);

    /// <summary>The person's guidance to a running review, which both agents read in their next message.</summary>
    public sealed record GuidanceAdded(DateTimeOffset At, string Text) : AttemptEvent(At);

    /// <summary>Settles a review between its turns: succeeded when <paramref name="Failure"/> is null, otherwise failed with it.</summary>
    public sealed record Concluded(DateTimeOffset At, string? Failure) : AttemptEvent(At);
}

/// <summary>
/// One attempt as of its log. Only <see cref="AttemptReducer"/> creates one. A waiting attempt goes on with the
/// person's reply, or ends when they mark it done or cancel it. Once the attempt is settled, the record is final, and
/// later events change nothing but <see cref="Terminal"/>.
/// </summary>
public sealed record AttemptRecord
{
    internal AttemptRecord(AttemptEvent.Requested requested)
    {
        Id = requested.Attempt;
        Task = requested.Task;
        TaskTitle = requested.TaskTitle;
        Requested = requested.Settings;
        RequestedAt = requested.At;
        Continues = requested.Continues?.Attempt;
        Conversation = requested.Conversation ?? ConversationMode.Autonomous;
        Planning = requested.Planning;
        ReadOnly = requested.ReadOnly;
        Subject = requested.Subject;
        Fix = requested.Fix;
        Turns = [new TurnRecord(1, requested.Continues is null ? null : requested.Prompt, TurnOutcome.Running, null) { StartTree = requested.Tree }];
    }

    public AttemptId Id { get; }

    public TaskId Task { get; }

    /// <summary>The task's title when the attempt started.</summary>
    public string TaskTitle { get; }

    public ExecutionSettings Requested { get; }

    public DateTimeOffset RequestedAt { get; }

    /// <summary>When the attempt waits for the person. The latest turn that set it decides.</summary>
    public ConversationMode Conversation { get; internal init; }

    /// <summary>Why the attempt waits, while its status is WaitingForInput.</summary>
    public Pending? Pending { get; internal init; }

    /// <summary>The handles the planner's prompt listed, which its proposals name. Null for a node that proposes nothing.</summary>
    public PlanningHandles? Planning { get; }

    /// <summary>The attempt whose session this one resumes. Its session id is <see cref="SessionId"/>, only once the reducer found it plain.</summary>
    public AttemptId? Continues { get; }

    /// <summary>Every turn ran in the client's read-only mode.</summary>
    public bool ReadOnly { get; }

    /// <summary>Set on a review's attempt: the node whose change it reviews.</summary>
    public TaskId? Subject { get; }

    /// <summary>Set on an attempt of a review's subject that fixes a round of the review's findings.</summary>
    public ReviewLink? Fix { get; }

    /// <summary>A review's guidance from the person, oldest first.</summary>
    public ImmutableList<GuidanceNote> Guidance { get; internal init; } = [];

    /// <summary>The project's files when the first turn started, or null outside Git.</summary>
    public string? StartTree => Turns[0].StartTree;

    /// <summary>The project's files when the latest ended turn ended, or null.</summary>
    public string? EndTree => Turns.LastOrDefault(turn => turn.Outcome != TurnOutcome.Running)?.EndTree;

    public AttemptStatus Status { get; internal init; }

    /// <summary>True between a cancel or leave request and the end of the attempt.</summary>
    public bool Stopping { get; internal init; }

    public DateTimeOffset? EndedAt { get; internal init; }

    /// <summary>The latest turn's final text: its verdict's text, else its last message. Kept for every outcome.</summary>
    public string? Result { get; internal init; }

    /// <summary>Why the attempt failed or was interrupted, in the client's words when it gave any.</summary>
    public string? Detail { get; internal init; }

    /// <summary>
    /// The client's session, which every turn shares. The client reported it, or a continuation resumed it. Always a plain
    /// id, safe in a command line.
    /// </summary>
    public string? SessionId { get; internal init; }

    public string? ReportedModel { get; internal init; }

    public string? ReportedReasoning { get; internal init; }

    /// <summary>The latest tool starts, messages, and notices of every turn, oldest first.</summary>
    public ImmutableList<ActivityLine> Activity { get; internal init; } = [];

    /// <summary>Every turn, oldest first. Only the last one can be running.</summary>
    public ImmutableList<TurnRecord> Turns { get; internal init; }

    /// <summary>The person's messages that wait for the running turn to end. A settled attempt keeps the ones it never sent.</summary>
    public ImmutableList<QueuedMessage> Queued { get; internal init; } = [];

    public ImmutableDictionary<RequestKey, RequestRecord> Requests { get; internal init; } = ImmutableDictionary<RequestKey, RequestRecord>.Empty;

    /// <summary>The latest hand-off of the session to the client's terminal interface.</summary>
    public TerminalHandoff? Terminal { get; internal init; }

    /// <summary>True while the latest turn has ended and the attempt goes on: until the next turn starts, or while it waits.</summary>
    internal bool BetweenTurns => Turns[^1].Outcome != TurnOutcome.Running;

    internal ProcessIdentity? Process { get; init; }

    internal bool CancelRequested { get; init; }

    internal string? InterruptReason { get; init; }

    /// <summary>Stop and send asked to stop the running turn.</summary>
    internal bool StopTurnRequested { get; init; }

    internal AgentEvent? Verdict { get; init; }

    internal string? LastMessage { get; init; }

    internal int AppliedEvents { get; init; } = 1;

    internal string? DeferredQuestion { get; init; }

    internal ImmutableArray<string> DeferredRequestIds { get; init; } = [];

    internal bool ShutdownForced { get; init; }
}

/// <summary>The only writer of <see cref="AttemptRecord"/>. Pure.</summary>
internal static partial class AttemptReducer
{
    private const int ActivityLimit = 100;

    /// <summary>
    /// A client's own session ids are UUIDs. Anything else could read as an option or as shell syntax in the next turn's
    /// arguments and in the command a person pastes in a terminal. A session id reaches the record only through this
    /// reducer, from client output and from attempt logs, which a shared repository can carry.
    /// </summary>
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._:-]*\z")]
    private static partial Regex PlainSessionId();

    /// <summary>Null when the log has no Requested line.</summary>
    public static AttemptRecord? Replay(IEnumerable<AttemptEvent> events)
    {
        AttemptRecord? record = null;
        foreach (var e in events)
        {
            record = record is not null ? Apply(record, e) : e is AttemptEvent.Requested requested ? Start(requested) : null;
        }

        return record;
    }

    public static AttemptRecord Start(AttemptEvent.Requested requested) => new(requested)
    {
        Status = AttemptStatus.Running,
        SessionId = requested.Continues is { Session: var session } && PlainSessionId().IsMatch(session) ? session : null,
    };

    public static AttemptRecord Apply(AttemptRecord record, AttemptEvent e)
    {
        var next = ApplyEvent(record, e);
        return record.Status is AttemptStatus.Running or AttemptStatus.WaitingForInput or AttemptStatus.InReview
            ? next with { AppliedEvents = record.AppliedEvents + 1 }
            : next;
    }

    private static AttemptRecord ApplyEvent(AttemptRecord record, AttemptEvent e) => (e, record) switch
    {
        (AttemptEvent.HandedToTerminal handoff, _) => record with { Terminal = new TerminalHandoff(handoff.At, handoff.Folder, handoff.Command) },
        (AttemptEvent.TurnRequested turn, { Status: AttemptStatus.WaitingForInput or AttemptStatus.InReview }) =>
            NextTurn(record with { Status = AttemptStatus.Running, Pending = null }, turn),
        (AttemptEvent.MarkedDone done, { Status: AttemptStatus.WaitingForInput }) => Settle(record, AttemptStatus.Succeeded, null, done.At),
        (AttemptEvent.CancelRequested cancel, { Status: AttemptStatus.WaitingForInput or AttemptStatus.InReview }) => Settle(record, AttemptStatus.Cancelled, null, cancel.At),
        (AttemptEvent.Concluded concluded, { Status: AttemptStatus.InReview }) =>
            Settle(record, concluded.Failure is null ? AttemptStatus.Succeeded : AttemptStatus.Failed, concluded.Failure, concluded.At),
        (AttemptEvent.GuidanceAdded guidance, { Status: AttemptStatus.Running or AttemptStatus.InReview }) =>
            record with { Guidance = record.Guidance.Add(new GuidanceNote(guidance.At, guidance.Text, record.Turns.Count)) },
        (AttemptEvent.MessageQueued message, { Status: AttemptStatus.Running or AttemptStatus.WaitingForInput }) => record with
        {
            Queued = record.Queued.Add(new QueuedMessage(message.Id ?? $"legacy:{record.Id}:{record.AppliedEvents}", message.Text)),
            StopTurnRequested = record.StopTurnRequested || (message.StopsTurn && !record.BetweenTurns),
        },
        (_, { Status: not AttemptStatus.Running }) => record,
        (AttemptEvent.Requested, _) => record,
        (AttemptEvent.Launched launched, _) => record with { Process = new ProcessIdentity(launched.ProcessId, launched.ProcessStarted) },
        (AttemptEvent.LaunchFailed failed, _) => EndAttempt(record, TurnOutcome.Failed, AttemptStatus.Failed, failed.Reason, failed.At),
        (AttemptEvent.Agent agent, _) => ApplyAgent(record, agent.Event, agent.At),
        (AttemptEvent.CancelRequested cancel, { BetweenTurns: true }) => Settle(record, AttemptStatus.Cancelled, null, cancel.At),
        (AttemptEvent.CancelRequested, _) => record with { CancelRequested = true, Stopping = true },
        (AttemptEvent.InterruptRequested interrupt, { BetweenTurns: true }) => Settle(record, AttemptStatus.Interrupted, interrupt.Reason, interrupt.At),
        (AttemptEvent.InterruptRequested interrupt, _) => record with { InterruptReason = record.InterruptReason ?? interrupt.Reason, Stopping = true },
        (AttemptEvent.QuestionRecorded question, _) => RecordQuestion(record, question),
        (AttemptEvent.RequestAnswered answer, _) => AnswerRequest(record, answer),
        (AttemptEvent.RequestClosed closed, _) => CloseRequest(record, closed.RequestId, closed.Reason),
        (AttemptEvent.RequestDeferred deferred, _) => DeferRequests(record, deferred),
        (AttemptEvent.ShutdownForced, _) => record with { ShutdownForced = true },
        (AttemptEvent.TurnRequested turn, _) => NextTurn(record, turn),
        (AttemptEvent.Exited exited, _) => AtExit(record, exited),
        (AttemptEvent.Reconciled reconciled, _) =>
            EndAttempt(record, TurnOutcome.Interrupted, AttemptStatus.Interrupted, Reconciliation(record, reconciled.Process), reconciled.At),
        (AttemptEvent.MarkedDone or AttemptEvent.Concluded or AttemptEvent.GuidanceAdded, _) => record,
        _ => throw new UnreachableException($"Unhandled attempt event {e.GetType().Name}"),
    };

    private static AttemptRecord NextTurn(AttemptRecord record, AttemptEvent.TurnRequested turn) => record with
    {
        Turns = record.Turns.Add(new TurnRecord(record.Turns.Count + 1, turn.Prompt, TurnOutcome.Running, null)
        {
            StartTree = turn.Tree,
            Report = turn.Report,
            Replies = turn.Replies.IsDefault ? [] : turn.Replies,
        }),
        Queued = turn.Consumed.IsDefault ? [] : record.Queued.RemoveAll(message => turn.Consumed.Contains(message.Id)),
        DeferredQuestion = null,
        DeferredRequestIds = [],
        ShutdownForced = false,
        Stopping = false,
        StopTurnRequested = false,
        Verdict = null,
        LastMessage = null,
        Conversation = turn.Conversation ?? record.Conversation,
    };

    private static RequestKey Key(AttemptRecord record, string id) => new(new TurnKey(record.Id, record.Turns.Count), id);

    private static AttemptRecord RecordQuestion(AttemptRecord record, AttemptEvent.QuestionRecorded question)
    {
        var key = Key(record, question.RequestId);
        return AddRequest(record, new RequestRecord.Question(key, question.Questions, question.State) { At = question.At });
    }

    private static AttemptRecord AddRequest(AttemptRecord record, RequestRecord request) => record.Requests.ContainsKey(request.Key)
        ? record
        : record with { Requests = record.Requests.Add(request.Key, request) };

    private static AttemptRecord AnswerRequest(AttemptRecord record, AttemptEvent.RequestAnswered answer)
    {
        var key = Key(record, answer.RequestId);
        return record.Requests.GetValueOrDefault(key) is RequestRecord.Question { State: QuestionState.Open } question
            ? record with { Requests = record.Requests.SetItem(key, question with { State = new QuestionState.AnswerRecorded(answer.Reply) }) }
            : record;
    }

    private static AttemptRecord CloseRequest(AttemptRecord record, string id, RequestCloseReason reason)
    {
        var key = Key(record, id);
        return record.Requests.TryGetValue(key, out var request)
            ? record with { Requests = record.Requests.SetItem(key, Close(request, reason)) }
            : record;
    }

    private static RequestRecord Close(RequestRecord request, RequestCloseReason reason) => request switch
    {
        RequestRecord.Question { State: QuestionState.Open } question => question with { State = new QuestionState.Closed(reason, null) },
        RequestRecord.Question { State: QuestionState.AnswerRecorded answer } question => question with { State = new QuestionState.Closed(reason, answer.Reply) },
        RequestRecord.Question { State: QuestionState.Closed closed } question when reason == RequestCloseReason.DeliveryUnknown =>
            question with { State = closed with { Reason = RequestCloseReason.DeliveryUnknown } },
        RequestRecord.Question => request,
        RequestRecord.Permission { State: PermissionState.Declining } permission => permission with
        {
            State = reason == RequestCloseReason.DeliveryUnknown ? PermissionState.DeliveryUnknown : PermissionState.Denied,
        },
        RequestRecord.Permission => request,
        _ => throw new UnreachableException($"Unhandled request {request.GetType().Name}"),
    };

    private static AttemptRecord DeferRequests(AttemptRecord record, AttemptEvent.RequestDeferred deferred)
    {
        foreach (var id in deferred.RequestIds)
        {
            if (record.Requests.GetValueOrDefault(Key(record, id)) is RequestRecord.Question { State: QuestionState.Open })
            {
                record = CloseRequest(record, id, RequestCloseReason.Deferred);
            }
        }

        return record with { DeferredQuestion = deferred.Question, DeferredRequestIds = deferred.RequestIds, Stopping = true };
    }

    private static ImmutableDictionary<RequestKey, RequestRecord> CloseTurnRequests(AttemptRecord record, RequestCloseReason reason) =>
        record.Requests.SetItems(record.Requests.Where(pair => pair.Key.Turn == new TurnKey(record.Id, record.Turns.Count))
            .Select(pair => new KeyValuePair<RequestKey, RequestRecord>(pair.Key, pair.Value switch
            {
                RequestRecord.Permission { State: PermissionState.Declining } => Close(pair.Value, RequestCloseReason.DeliveryUnknown),
                RequestRecord.Permission => pair.Value,
                RequestRecord.Question => Close(pair.Value, reason),
                _ => throw new UnreachableException($"Unhandled request {pair.Value.GetType().Name}"),
            })));

    /// <summary>Ends an attempt whose log can no longer be written. Only the record in memory changes.</summary>
    public static AttemptRecord Abandon(AttemptRecord record, string reason, DateTimeOffset at) =>
        record.Status == AttemptStatus.Running ? EndAttempt(record, TurnOutcome.Failed, AttemptStatus.Failed, reason, at) : record;

    private static AttemptRecord ApplyAgent(AttemptRecord record, AgentEvent e, DateTimeOffset at) => e switch
    {
        AgentEvent.SessionStarted session when PlainSessionId().IsMatch(session.SessionId) => record with { SessionId = session.SessionId },
        AgentEvent.SessionStarted => Log(record, at, $"iDevelop ignored the session id {Clients.Name(record.Requested.Client)} reported, because it is not a plain id."),
        AgentEvent.Reported reported => record with
        {
            ReportedModel = reported.Model ?? record.ReportedModel,
            ReportedReasoning = reported.Reasoning ?? record.ReportedReasoning,
        },
        AgentEvent.Message message => Log(record with { LastMessage = message.Text }, at, TextLines.FirstLine(message.Text) ?? ""),
        AgentEvent.ToolStarted tool => Log(record, at, tool.Detail is null ? tool.Tool : $"{tool.Tool}: {tool.Detail}", isTool: true),
        AgentEvent.Notice notice => Log(record, at, notice.Text),
        AgentEvent.PermissionRequested permission => AddRequest(record,
            new RequestRecord.Permission(Key(record, permission.RequestId), permission.Action, PermissionState.Declining) { At = at }),
        AgentEvent.MessageDelta or AgentEvent.QuestionAsked or AgentEvent.RequestClosed => record,
        AgentEvent.Succeeded or AgentEvent.Failed => record with { Verdict = e },
        _ => throw new UnreachableException($"Unhandled agent event {e.GetType().Name}"),
    };

    /// <summary>
    /// The exit ends the turn. A message the person sent then starts the next turn, unless a cancel or leave came first
    /// or there is no session to resume. A turn that succeeded can leave the node waiting for the person, as its
    /// conversation mode decides. Otherwise the exit policy settles the attempt.
    /// </summary>
    private static AttemptRecord AtExit(AttemptRecord record, AttemptEvent.Exited exited)
    {
        record = record with { Turns = record.Turns.SetItem(record.Turns.Count - 1, record.Turns[^1] with { EndTree = exited.Tree }) };
        var (turn, status, detail) = ExitPolicy(record, exited);
        var deferring = record is { DeferredQuestion: not null, CancelRequested: false, InterruptReason: null };
        if ((status == AttemptStatus.Succeeded || deferring) && record.ReadOnly && record.Turns[^1] is { StartTree: { } before, EndTree: { } after } && before != after)
        {
            return EndAttempt(record, TurnOutcome.Failed, AttemptStatus.Failed,
                "The turn changed files in the project, although this node's agent may only read.", exited.At);
        }

        if (deferring && record.DeferredQuestion is { } question)
        {
            return record.SessionId is null
                ? EndAttempt(record, TurnOutcome.Failed, AttemptStatus.Failed,
                    "The client reported no session; this question cannot be resumed.", exited.At)
                : EndTurn(record, TurnOutcome.Deferred, null) with
                {
                    Status = AttemptStatus.WaitingForInput,
                    Pending = new Pending.Question(question),
                    Result = FinalText(record),
                    Stopping = false,
                };
        }

        if (record.ShutdownForced && !record.CancelRequested && record.InterruptReason is null)
        {
            return EndAttempt(record, turn, status, detail, exited.At);
        }

        if (record is { Queued.IsEmpty: false, CancelRequested: false, InterruptReason: null, SessionId: not null })
        {
            return EndTurn(record, turn, detail);
        }

        // A review rests between its reviewer's turns, while its subject fixes the findings. Its work decides what comes next.
        if (status == AttemptStatus.Succeeded && record.Subject is not null)
        {
            return record.SessionId is null
                ? EndAttempt(record, TurnOutcome.Failed, AttemptStatus.Failed,
                    $"{Clients.Name(record.Requested.Client)} reported no session, so the review could not go on.", exited.At)
                : EndTurn(record, turn, detail) with { Status = AttemptStatus.InReview, Result = FinalText(record) };
        }

        if (status == AttemptStatus.Succeeded && AgentWork.AfterTurn(record.Conversation, FinalText(record)) is { } pending)
        {
            return record.SessionId is null
                ? EndAttempt(record, TurnOutcome.Failed, AttemptStatus.Failed,
                    $"{Clients.Name(record.Requested.Client)} reported no session, so iDevelop could not wait for your answer.", exited.At)
                : EndTurn(record, turn, detail) with { Status = AttemptStatus.WaitingForInput, Pending = pending, Result = FinalText(record) };
        }

        return EndAttempt(record, turn, status, detail, exited.At);
    }

    /// <summary>
    /// The one exit policy, the same for every client, applied in order: a leave request, then a cancel request, then
    /// forced shutdown, then a stopped turn, then the client's failure, then its success with exit code 0. Anything else failed.
    /// Success needs both signals: Pi exits 0 after a failed turn, and Claude Code exits 1 after a bad model.
    /// A stopped turn settles the attempt only when its client reported no session to send the message to.
    /// </summary>
    private static (TurnOutcome Turn, AttemptStatus Status, string? Detail) ExitPolicy(AttemptRecord record, AttemptEvent.Exited exited)
    {
        var client = Clients.Name(record.Requested.Client);
        return record switch
        {
            { InterruptReason: { } reason } => (TurnOutcome.Interrupted, AttemptStatus.Interrupted, reason),
            { CancelRequested: true } => (TurnOutcome.Stopped, AttemptStatus.Cancelled, null),
            { ShutdownForced: true } => (TurnOutcome.Failed, AttemptStatus.Failed, "The client reported success but did not shut down."),
            { StopTurnRequested: true } => (TurnOutcome.Stopped, AttemptStatus.Cancelled, $"{client} reported no session, so iDevelop could not send your message."),
            { Verdict: AgentEvent.Failed failed } => (TurnOutcome.Failed, AttemptStatus.Failed, failed.Reason),
            { Verdict: AgentEvent.Succeeded } when exited.ExitCode == 0 => (TurnOutcome.Succeeded, AttemptStatus.Succeeded, null),
            { Verdict: AgentEvent.Succeeded } => (TurnOutcome.Failed, AttemptStatus.Failed, $"{client} reported success but exited with code {exited.ExitCode}."),
            _ when exited.ExitCode == 0 => (TurnOutcome.Failed, AttemptStatus.Failed, $"{client} ended without a result."),
            _ => (TurnOutcome.Failed, AttemptStatus.Failed, TextLines.LastLine(exited.StderrTail) is { } line
                ? $"{client} exited with code {exited.ExitCode}: {line}"
                : $"{client} exited with code {exited.ExitCode}."),
        };
    }

    /// <summary>
    /// Why the attempt ended. No client runs between turns, so a crash there needs no process check. During a turn the
    /// cause is leaving when leaving gave up on the client, else a crash, followed by what reconciling found.
    /// </summary>
    private static string Reconciliation(AttemptRecord record, ProcessMatch? process)
    {
        if (record.BetweenTurns)
        {
            return "iDevelop stopped before the next turn started.";
        }

        var cause = record.InterruptReason is { } reason
            ? $"{reason} Its client did not stop in time, and iDevelop settled it when the project was opened again."
            : process is null ? "iDevelop stopped while starting the client." : "iDevelop stopped while this task ran.";
        var found = process is not { } match ? " If the client started, it may still be running." : match switch
        {
            ProcessMatch.Same => " Its client was still running and was stopped.",
            ProcessMatch.Gone => "",
            ProcessMatch.Reused => $" Process {record.Process?.Id} now belongs to another program and was left alone.",
        };
        return cause + found;
    }

    /// <summary>
    /// Ends the running turn with its final text, and with the detail if it failed or was interrupted. Its process is
    /// forgotten, so nothing checks a process that already exited. Between turns there is none to end.
    /// </summary>
    private static AttemptRecord EndTurn(AttemptRecord record, TurnOutcome outcome, string? detail) => record.BetweenTurns
        ? record
        : record with
        {
            Turns = record.Turns.SetItem(record.Turns.Count - 1, record.Turns[^1] with
            {
                Outcome = outcome,
                FinalText = FinalText(record),
                Detail = outcome is TurnOutcome.Failed or TurnOutcome.Interrupted ? detail : null,
            }),
            Process = null,
            Requests = CloseTurnRequests(record, outcome switch
            {
                TurnOutcome.Stopped => record.CancelRequested ? RequestCloseReason.Cancelled : RequestCloseReason.Stopped,
                TurnOutcome.Interrupted => RequestCloseReason.Interrupted,
                TurnOutcome.Deferred => RequestCloseReason.TurnEnded,
                TurnOutcome.Succeeded or TurnOutcome.Failed => RequestCloseReason.TurnEnded,
                TurnOutcome.Running => throw new UnreachableException("A running turn cannot end as running."),
            }),
        };

    /// <summary>Ends the running turn, if one runs, and settles the attempt, both for the same reason.</summary>
    private static AttemptRecord EndAttempt(AttemptRecord record, TurnOutcome turn, AttemptStatus status, string? detail, DateTimeOffset at) =>
        Settle(EndTurn(record, turn, detail), status, detail, at);

    private static AttemptRecord Settle(AttemptRecord record, AttemptStatus status, string? detail, DateTimeOffset at) => record with
    {
        Status = status,
        Detail = detail,
        EndedAt = at,
        Stopping = false,
        Result = FinalText(record),
        Pending = null,
    };

    private static string? FinalText(AttemptRecord record) => (record.Verdict as AgentEvent.Succeeded)?.Result ?? record.LastMessage;

    private static AttemptRecord Log(AttemptRecord record, DateTimeOffset at, string text, bool isTool = false)
    {
        var activity = record.Activity.Count < ActivityLimit ? record.Activity : record.Activity.RemoveAt(0);
        return record with { Activity = activity.Add(new ActivityLine(at, text, isTool)) };
    }
}
