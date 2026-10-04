using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json.Serialization;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>One execution of one task. Version 7, so attempt folders sort by start time.</summary>
public readonly record struct AttemptId(Guid Value) : IComparable<AttemptId>
{
    public static AttemptId New() => new(Guid.CreateVersion7());

    public int CompareTo(AttemptId other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString("D");
}

public enum AttemptStatus { Running, Succeeded, Failed, Cancelled, Interrupted }

public enum ActivityKind { Tool, Message, Notice }

public sealed record ActivityLine(DateTimeOffset At, ActivityKind Kind, string Text);

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
internal abstract record AttemptEvent([property: JsonPropertyOrder(-1)] DateTimeOffset At)
{
    /// <summary>Always the first line. A snapshot, so a later edit of the task never changes what this attempt ran.</summary>
    public sealed record Requested(
        DateTimeOffset At, AttemptId Attempt, TaskId Task, string TaskTitle, ExecutionSettings Settings,
        string Prompt, string Command, ImmutableArray<string> Arguments) : AttemptEvent(At);

    public sealed record Launched(DateTimeOffset At, int ProcessId, DateTimeOffset ProcessStarted) : AttemptEvent(At);

    public sealed record LaunchFailed(DateTimeOffset At, string Reason) : AttemptEvent(At);

    public sealed record Agent(DateTimeOffset At, AgentEvent Event) : AttemptEvent(At);

    public sealed record CancelRequested(DateTimeOffset At) : AttemptEvent(At);

    /// <summary>Leaving the project: opening another folder or closing the window.</summary>
    public sealed record InterruptRequested(DateTimeOffset At, string Reason) : AttemptEvent(At);

    public sealed record Exited(DateTimeOffset At, int ExitCode, string StderrTail) : AttemptEvent(At);

    /// <summary>Written by whoever next takes the run lock and finds the attempt still running. Null when it never launched.</summary>
    public sealed record Reconciled(DateTimeOffset At, ProcessMatch? Process) : AttemptEvent(At);
}

/// <summary>
/// One attempt as of its log. Only <see cref="AttemptReducer"/> creates one. Once <see cref="Status"/> leaves Running,
/// the record is final and later events change nothing.
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
    }

    public AttemptId Id { get; }

    public TaskId Task { get; }

    /// <summary>The task's title when the attempt started.</summary>
    public string TaskTitle { get; }

    public ExecutionSettings Requested { get; }

    public DateTimeOffset RequestedAt { get; }

    public AttemptStatus Status { get; internal init; }

    /// <summary>True between a cancel or leave request and the end of the attempt.</summary>
    public bool Stopping { get; internal init; }

    public DateTimeOffset? EndedAt { get; internal init; }

    /// <summary>The client's final text: its verdict's text, else its last message. Kept for every outcome.</summary>
    public string? Result { get; internal init; }

    /// <summary>Why the attempt failed or was interrupted, in the client's words when it gave any.</summary>
    public string? Detail { get; internal init; }

    public string? SessionId { get; internal init; }

    public string? ReportedModel { get; internal init; }

    public string? ReportedReasoning { get; internal init; }

    /// <summary>The latest tool starts, messages, and notices, oldest first.</summary>
    public ImmutableList<ActivityLine> Activity { get; internal init; } = [];

    internal ProcessIdentity? Process { get; init; }

    internal bool CancelRequested { get; init; }

    internal string? InterruptReason { get; init; }

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

    public static AttemptRecord Apply(AttemptRecord record, AttemptEvent e)
    {
        if (record.Status != AttemptStatus.Running)
        {
            return record;
        }

        return e switch
        {
            AttemptEvent.Requested => record,
            AttemptEvent.Launched launched => record with { Process = new ProcessIdentity(launched.ProcessId, launched.ProcessStarted) },
            AttemptEvent.LaunchFailed failed => Settle(record, AttemptStatus.Failed, failed.Reason, failed.At),
            AttemptEvent.Agent agent => Apply(record, agent.Event, agent.At),
            AttemptEvent.CancelRequested => record with { CancelRequested = true, Stopping = true },
            AttemptEvent.InterruptRequested interrupt => record with { InterruptReason = record.InterruptReason ?? interrupt.Reason, Stopping = true },
            AttemptEvent.Exited exited => SettleAtExit(record, exited),
            AttemptEvent.Reconciled reconciled => Settle(record, AttemptStatus.Interrupted, Reconciliation(record, reconciled.Process), reconciled.At),
            _ => throw new UnreachableException($"Unhandled attempt event {e.GetType().Name}"),
        };
    }

    /// <summary>Ends an attempt whose log can no longer be written. Only the record in memory changes.</summary>
    public static AttemptRecord Abandon(AttemptRecord record, string reason, DateTimeOffset at) =>
        record.Status == AttemptStatus.Running ? Settle(record, AttemptStatus.Failed, reason, at) : record;

    private static AttemptRecord Apply(AttemptRecord record, AgentEvent e, DateTimeOffset at) => e switch
    {
        AgentEvent.SessionStarted session => record with { SessionId = session.SessionId },
        AgentEvent.Reported reported => record with
        {
            ReportedModel = reported.Model ?? record.ReportedModel,
            ReportedReasoning = reported.Reasoning ?? record.ReportedReasoning,
        },
        AgentEvent.Message message => Log(record with { LastMessage = message.Text }, at, ActivityKind.Message, FirstLine(message.Text)),
        AgentEvent.ToolStarted tool => Log(record, at, ActivityKind.Tool, tool.Detail is null ? tool.Tool : $"{tool.Tool}: {tool.Detail}"),
        AgentEvent.Notice notice => Log(record, at, ActivityKind.Notice, notice.Text),
        AgentEvent.Succeeded or AgentEvent.Failed => record with { Verdict = e },
        _ => throw new UnreachableException($"Unhandled agent event {e.GetType().Name}"),
    };

    /// <summary>
    /// The one exit policy, the same for every client, applied in order: a leave request, then a cancel request,
    /// then the client's failure, then its success with exit code 0. Anything else failed.
    /// Success needs both signals: Pi exits 0 after a failed turn, and Claude Code exits 1 after a bad model.
    /// </summary>
    private static AttemptRecord SettleAtExit(AttemptRecord record, AttemptEvent.Exited exited)
    {
        var client = Clients.Name(record.Requested.Client);
        (AttemptStatus Status, string? Detail) outcome = record switch
        {
            { InterruptReason: { } reason } => (AttemptStatus.Interrupted, reason),
            { CancelRequested: true } => (AttemptStatus.Cancelled, null),
            { Verdict: AgentEvent.Failed failed } => (AttemptStatus.Failed, failed.Reason),
            { Verdict: AgentEvent.Succeeded } when exited.ExitCode == 0 => (AttemptStatus.Succeeded, null),
            { Verdict: AgentEvent.Succeeded } => (AttemptStatus.Failed, $"{client} reported success but exited with code {exited.ExitCode}."),
            _ when exited.ExitCode == 0 => (AttemptStatus.Failed, $"{client} ended without a result."),
            _ => (AttemptStatus.Failed, LastLine(exited.StderrTail) is { } line
                ? $"{client} exited with code {exited.ExitCode}: {line}"
                : $"{client} exited with code {exited.ExitCode}."),
        };
        return Settle(record, outcome.Status, outcome.Detail, exited.At);
    }

    private static string Reconciliation(AttemptRecord record, ProcessMatch? process)
    {
        if (process is not { } match)
        {
            return "iDevelop stopped while starting the client. If the client started, it may still be running.";
        }

        return match switch
        {
            ProcessMatch.Same => "iDevelop stopped while this task ran. Its client was still running and was stopped.",
            ProcessMatch.Gone => "iDevelop stopped while this task ran.",
            ProcessMatch.Reused => $"iDevelop stopped while this task ran. Process {record.Process?.Id} now belongs to another program and was left alone.",
        };
    }

    private static AttemptRecord Settle(AttemptRecord record, AttemptStatus status, string? detail, DateTimeOffset at) => record with
    {
        Status = status,
        Detail = detail,
        EndedAt = at,
        Stopping = false,
        Result = (record.Verdict as AgentEvent.Succeeded)?.Result ?? record.LastMessage,
    };

    private static AttemptRecord Log(AttemptRecord record, DateTimeOffset at, ActivityKind kind, string text)
    {
        var activity = record.Activity.Count < ActivityLimit ? record.Activity : record.Activity.RemoveAt(0);
        return record with { Activity = activity.Add(new ActivityLine(at, kind, text)) };
    }

    private static string FirstLine(string text) => text.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? "";

    private static string? LastLine(string text) => text.Split('\n').Select(line => line.Trim()).LastOrDefault(line => line.Length > 0);
}
