using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json.Serialization;
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

public enum AttemptStatus { Running, Succeeded, Failed, Cancelled, Interrupted }

/// <summary>How one client process ended. Stopped means the person stopped it, with Stop and send or Cancel.</summary>
public enum TurnOutcome { Running, Succeeded, Failed, Stopped, Interrupted }

public sealed record ActivityLine(DateTimeOffset At, string Text);

/// <summary>
/// One client process of an attempt. <see cref="Message"/> is what the person sent, which is the turn's whole prompt,
/// and null for a first turn whose prompt is the task. <see cref="FinalText"/> is null while the turn runs.
/// <see cref="Detail"/> says why the turn failed or was interrupted, in the client's words when it gave any.
/// </summary>
public sealed record TurnRecord(int Number, string? Message, TurnOutcome Outcome, string? FinalText, string? Detail = null);

/// <summary>The settled attempt whose client session an attempt resumes, and that session's id.</summary>
public sealed record Continuation(AttemptId Attempt, string Session);

/// <summary>The command a person copied to open the attempt's session in the client's own terminal interface.</summary>
public sealed record TerminalHandoff(DateTimeOffset At, string Command);

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
        /// <summary>Set when the first turn resumes an earlier attempt's session, and then its prompt is the person's message.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Continuation? Continues { get; init; }
    }

    public sealed record Launched(DateTimeOffset At, int ProcessId, DateTimeOffset ProcessStarted) : AttemptEvent(At);

    public sealed record LaunchFailed(DateTimeOffset At, string Reason) : AttemptEvent(At);

    public sealed record Agent(DateTimeOffset At, AgentEvent Event) : AttemptEvent(At);

    public sealed record CancelRequested(DateTimeOffset At) : AttemptEvent(At);

    /// <summary>Leaving the project: opening another folder or closing the window.</summary>
    public sealed record InterruptRequested(DateTimeOffset At, string Reason) : AttemptEvent(At);

    public sealed record Exited(DateTimeOffset At, int ExitCode, string StderrTail) : AttemptEvent(At);

    /// <summary>Written by whoever next takes the run lock and finds the attempt still running. Null when it never launched.</summary>
    public sealed record Reconciled(DateTimeOffset At, ProcessMatch? Process) : AttemptEvent(At);

    /// <summary>A message the person sent while the attempt ran. It waits for the turn to end, and <paramref name="StopsTurn"/> ends it.</summary>
    public sealed record MessageQueued(DateTimeOffset At, string Text, bool StopsTurn) : AttemptEvent(At);

    /// <summary>A turn after the first, which resumes the session with the waiting messages. Launched or LaunchFailed follows it.</summary>
    public sealed record TurnRequested(DateTimeOffset At, string Prompt, string Command, ImmutableArray<string> Arguments) : AttemptEvent(At);

    /// <summary>Appended to a settled attempt. Turns the person takes in the terminal are not in this log.</summary>
    public sealed record HandedToTerminal(DateTimeOffset At, string Command) : AttemptEvent(At);
}

/// <summary>
/// One attempt as of its log. Only <see cref="AttemptReducer"/> creates one. Once <see cref="Status"/> leaves Running,
/// the record is final, and later events change nothing but <see cref="Terminal"/>.
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
        Continues = requested.Continues;
        SessionId = requested.Continues?.Session;
        Turns = [new TurnRecord(1, requested.Continues is null ? null : requested.Prompt, TurnOutcome.Running, null)];
    }

    public AttemptId Id { get; }

    public TaskId Task { get; }

    /// <summary>The task's title when the attempt started.</summary>
    public string TaskTitle { get; }

    public ExecutionSettings Requested { get; }

    public DateTimeOffset RequestedAt { get; }

    public Continuation? Continues { get; }

    public AttemptStatus Status { get; internal init; }

    /// <summary>True between a cancel or leave request and the end of the attempt.</summary>
    public bool Stopping { get; internal init; }

    public DateTimeOffset? EndedAt { get; internal init; }

    /// <summary>The latest turn's final text: its verdict's text, else its last message. Kept for every outcome.</summary>
    public string? Result { get; internal init; }

    /// <summary>Why the attempt failed or was interrupted, in the client's words when it gave any.</summary>
    public string? Detail { get; internal init; }

    /// <summary>The client's session, which every turn shares. The client reported it, or a continuation resumed it.</summary>
    public string? SessionId { get; internal init; }

    public string? ReportedModel { get; internal init; }

    public string? ReportedReasoning { get; internal init; }

    /// <summary>The latest tool starts, messages, and notices of every turn, oldest first.</summary>
    public ImmutableList<ActivityLine> Activity { get; internal init; } = [];

    /// <summary>Every turn, oldest first. Only the last one can be running.</summary>
    public ImmutableList<TurnRecord> Turns { get; internal init; }

    /// <summary>The person's messages that wait for the running turn to end. A settled attempt keeps the ones it never sent.</summary>
    public ImmutableList<string> Queued { get; internal init; } = [];

    /// <summary>The latest hand-off of the session to the client's terminal interface.</summary>
    public TerminalHandoff? Terminal { get; internal init; }

    /// <summary>True while the attempt runs and its latest turn has ended, until the next turn starts.</summary>
    internal bool BetweenTurns => Turns[^1].Outcome != TurnOutcome.Running;

    internal ProcessIdentity? Process { get; init; }

    internal bool CancelRequested { get; init; }

    internal string? InterruptReason { get; init; }

    /// <summary>Stop and send asked to stop the running turn.</summary>
    internal bool StopTurnRequested { get; init; }

    internal AgentEvent? Verdict { get; init; }

    internal string? LastMessage { get; init; }
}

/// <summary>The only writer of <see cref="AttemptRecord"/>. Pure.</summary>
internal static class AttemptReducer
{
    private const int ActivityLimit = 100;

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

    public static AttemptRecord Start(AttemptEvent.Requested requested) => new(requested) { Status = AttemptStatus.Running };

    public static AttemptRecord Apply(AttemptRecord record, AttemptEvent e) => (e, record) switch
    {
        (AttemptEvent.HandedToTerminal handoff, _) => record with { Terminal = new TerminalHandoff(handoff.At, handoff.Command) },
        (_, { Status: not AttemptStatus.Running }) => record,
        (AttemptEvent.Requested, _) => record,
        (AttemptEvent.Launched launched, _) => record with { Process = new ProcessIdentity(launched.ProcessId, launched.ProcessStarted) },
        (AttemptEvent.LaunchFailed failed, _) => EndAttempt(record, TurnOutcome.Failed, AttemptStatus.Failed, failed.Reason, failed.At),
        (AttemptEvent.Agent agent, _) => ApplyAgent(record, agent.Event, agent.At),
        (AttemptEvent.CancelRequested cancel, { BetweenTurns: true }) => Settle(record, AttemptStatus.Cancelled, null, cancel.At),
        (AttemptEvent.CancelRequested, _) => record with { CancelRequested = true, Stopping = true },
        (AttemptEvent.InterruptRequested interrupt, { BetweenTurns: true }) => Settle(record, AttemptStatus.Interrupted, interrupt.Reason, interrupt.At),
        (AttemptEvent.InterruptRequested interrupt, _) => record with { InterruptReason = record.InterruptReason ?? interrupt.Reason, Stopping = true },
        (AttemptEvent.MessageQueued message, _) => record with
        {
            Queued = record.Queued.Add(message.Text),
            StopTurnRequested = record.StopTurnRequested || (message.StopsTurn && !record.BetweenTurns),
        },
        (AttemptEvent.TurnRequested turn, _) => record with
        {
            Turns = record.Turns.Add(new TurnRecord(record.Turns.Count + 1, turn.Prompt, TurnOutcome.Running, null)),
            Queued = [],
            Process = null,
            StopTurnRequested = false,
            Verdict = null,
            LastMessage = null,
        },
        (AttemptEvent.Exited exited, _) => AtExit(record, exited),
        (AttemptEvent.Reconciled reconciled, _) =>
            EndAttempt(record, TurnOutcome.Interrupted, AttemptStatus.Interrupted, Reconciliation(record, reconciled.Process), reconciled.At),
        _ => throw new UnreachableException($"Unhandled attempt event {e.GetType().Name}"),
    };

    /// <summary>Ends an attempt whose log can no longer be written. Only the record in memory changes.</summary>
    public static AttemptRecord Abandon(AttemptRecord record, string reason, DateTimeOffset at) =>
        record.Status == AttemptStatus.Running ? EndAttempt(record, TurnOutcome.Failed, AttemptStatus.Failed, reason, at) : record;

    private static AttemptRecord ApplyAgent(AttemptRecord record, AgentEvent e, DateTimeOffset at) => e switch
    {
        AgentEvent.SessionStarted session => record with { SessionId = session.SessionId },
        AgentEvent.Reported reported => record with
        {
            ReportedModel = reported.Model ?? record.ReportedModel,
            ReportedReasoning = reported.Reasoning ?? record.ReportedReasoning,
        },
        AgentEvent.Message message => Log(record with { LastMessage = message.Text }, at, TextLines.FirstLine(message.Text) ?? ""),
        AgentEvent.ToolStarted tool => Log(record, at, tool.Detail is null ? tool.Tool : $"{tool.Tool}: {tool.Detail}"),
        AgentEvent.Notice notice => Log(record, at, notice.Text),
        AgentEvent.Succeeded or AgentEvent.Failed => record with { Verdict = e },
        _ => throw new UnreachableException($"Unhandled agent event {e.GetType().Name}"),
    };

    /// <summary>
    /// The exit ends the turn. A message the person sent then starts the next turn, unless a cancel or leave came first
    /// or there is no session to resume. Otherwise the exit policy settles the attempt.
    /// </summary>
    private static AttemptRecord AtExit(AttemptRecord record, AttemptEvent.Exited exited)
    {
        var (turn, status, detail) = ExitPolicy(record, exited);
        return record is { Queued.IsEmpty: false, CancelRequested: false, InterruptReason: null, SessionId: not null }
            ? EndTurn(record, turn, detail)
            : EndAttempt(record, turn, status, detail, exited.At);
    }

    /// <summary>
    /// The one exit policy, the same for every client, applied in order: a leave request, then a cancel request, then a
    /// stopped turn, then the client's failure, then its success with exit code 0. Anything else failed.
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
    /// Why the attempt ended: a crash between turns, when no client runs; otherwise leaving when leaving gave up on it, else
    /// a crash, then what reconciling found.
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
    };

    private static string? FinalText(AttemptRecord record) => (record.Verdict as AgentEvent.Succeeded)?.Result ?? record.LastMessage;

    private static AttemptRecord Log(AttemptRecord record, DateTimeOffset at, string text)
    {
        var activity = record.Activity.Count < ActivityLimit ? record.Activity : record.Activity.RemoveAt(0);
        return record with { Activity = activity.Add(new ActivityLine(at, text)) };
    }
}
