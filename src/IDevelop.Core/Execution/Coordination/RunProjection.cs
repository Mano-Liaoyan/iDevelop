using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>What this window does for a task now. Each stage but the slot-holding two lasts until a durable step is recorded.</summary>
internal enum LiveStage { Starting, Running, Settling }

/// <summary>Why this window holds a task back without a durable record that says so.</summary>
internal abstract record TaskHold
{
    private TaskHold() { }

    /// <summary>A start or a step after settlement was refused. A transient refusal is retried after a delay.</summary>
    internal sealed record Refused(RunRejection Reason, StartProblem? Problem, bool Transient) : TaskHold;

    /// <summary>The turn's settlement is unresolved. A transient rejection is retried through reconciliation.</summary>
    internal sealed record Unresolved(UnresolvedReason Reason, RunRejection? Rejection, bool Transient) : TaskHold;

    /// <summary>A start returned a block that the journal does not show unresolved, so it is not attempted again until Resume.</summary>
    internal sealed record Blocked(MaterializationBlock Block) : TaskHold;

    /// <summary>Whether a retry clears it.</summary>
    public bool Retried => this is Refused { Transient: true } or Unresolved { Transient: true };
}

/// <summary>
/// The run as one window sees it: a pure fold of the journal, the logs of resting attempts, and the work this window has
/// in flight. Readiness, waiting, blocks, and completion are only projections; the journal stays the authority.
/// </summary>
internal static class RunProjection
{
    public static RunView Of(RunAddress address, RunRecord record, Func<RunAttempt, AttemptRecord?> log,
        IReadOnlyDictionary<TaskId, LiveStage> live, IReadOnlyDictionary<TaskId, TaskHold> holds, bool controlled, bool resumed)
    {
        var snapshot = record.Revision.Snapshot;
        var latest = LatestAttempts(record);
        var results = record.CurrentResults;
        var progress = new Dictionary<TaskId, TaskView>();
        foreach (var task in snapshot.Tasks.Keys)
        {
            if (Started(record, task, latest.TryGetValue(task, out var attempt) ? attempt : null, results.GetValueOrDefault(task), log, live, holds) is { } view)
                progress.Add(task, view);
        }
        var plan = Schedule.Of(snapshot, progress, view => view.State == TaskState.Done);
        var tasks = ImmutableSortedDictionary.CreateBuilder<TaskId, TaskView>();
        foreach (var (task, view) in progress) tasks[task] = view;
        foreach (var task in plan.Ready)
            tasks[task] = new(task, snapshot.Tasks[task].Blueprint.Work is WorkSpec.Agent ? TaskState.Ready : TaskState.Unsupported);
        foreach (var (task, holders) in plan.Blocked) tasks[task] = new(task, TaskState.Pending) { HeldBy = [.. holders.Keys] };
        var built = tasks.ToImmutable();
        var slots = live.Values.Count(stage => stage is LiveStage.Starting or LiveStage.Running);
        return new(address, record.Phase, Status(record.Phase, controlled, resumed, built, holds), controlled, resumed, slots, built);
    }

    /// <summary>Each task's newest attempt in this run, in reservation order.</summary>
    public static ImmutableDictionary<TaskId, AttemptId> LatestAttempts(RunRecord record) => record.Receipts.Values
        .Where(entry => entry.Event is RunEvent.Reserved).OrderBy(entry => entry.Sequence)
        .Select(entry => ((RunEvent.Reserved)entry.Event).Attempt)
        .Aggregate(ImmutableDictionary<TaskId, AttemptId>.Empty, (all, attempt) => all.SetItem(attempt.Task, attempt.Id));

    /// <summary>The unresolved blocks that hold <paramref name="task"/>: on no attempt, on its newest one, or on the one that
    /// published its current result, oldest first. A block on an attempt that was replaced holds nothing.</summary>
    public static IEnumerable<MaterializationBlock> Blocks(RunRecord record, TaskId task, AttemptId? latest, ResultRecord? result)
    {
        var published = (result?.Origin as ResultOrigin.Executed)?.Attempt;
        return record.Blocks.Where(pair => !pair.Value.Resolved && pair.Value.Block.Task == task &&
                (pair.Value.Block.Attempt is null || pair.Value.Block.Attempt == latest || pair.Value.Block.Attempt == published))
            .OrderBy(pair => record.Receipts.GetValueOrDefault(pair.Key)?.Sequence ?? 0).Select(pair => pair.Value.Block);
    }

    /// <summary>The attempt's newest claimed launch, or null before its first claim.</summary>
    public static LaunchKey? LastLaunch(RunRecord record, AttemptId attempt) =>
        record.Claims.Keys.Where(key => key.Attempt == attempt).OrderBy(key => key.Turn).Select(key => (LaunchKey?)key).LastOrDefault();

    /// <summary>Whether an attempt rests between turns for the person: waiting, in review, or stopped with queued text.</summary>
    public static bool Rests(AttemptRecord? attempt) => attempt is { BetweenTurns: true } &&
        (attempt.Status is AttemptStatus.WaitingForInput or AttemptStatus.InReview || attempt.Status == AttemptStatus.Running && !attempt.Queued.IsEmpty);

    private static TaskView? Started(RunRecord record, TaskId task, AttemptId? attempt, ResultRecord? result,
        Func<RunAttempt, AttemptRecord?> log, IReadOnlyDictionary<TaskId, LiveStage> live, IReadOnlyDictionary<TaskId, TaskHold> holds)
    {
        if (live.TryGetValue(task, out var stage))
        {
            return new(task, stage switch
            {
                LiveStage.Starting => TaskState.Starting,
                LiveStage.Running => TaskState.Running,
                LiveStage.Settling => TaskState.Settling,
                _ => throw new InvalidOperationException(),
            }) { Attempt = attempt };
        }
        var hold = holds.GetValueOrDefault(task);
        if (hold is TaskHold.Unresolved unresolved)
        {
            return new(task, unresolved.Transient ? TaskState.Settling : TaskState.Uncertain)
            { Attempt = attempt, Unresolved = unresolved.Reason, Refusal = unresolved.Rejection };
        }
        var block = Blocks(record, task, attempt, result).FirstOrDefault();
        if (result is not null)
        {
            return new(task, block is null ? TaskState.Done : TaskState.Blocked) { Attempt = attempt, Result = result.Id, Block = block };
        }
        switch (hold)
        {
            case TaskHold.Blocked blocked: return new(task, TaskState.Blocked) { Attempt = attempt, Block = blocked.Block };
            case TaskHold.Refused refused: return new(task, TaskState.Refused) { Attempt = attempt, Refusal = refused.Reason, Problem = refused.Problem };
        }
        if (block is not null) return new(task, TaskState.Blocked) { Attempt = attempt, Block = block };
        if (attempt is not { } id) return null;
        if (record.Closures.TryGetValue(id, out var end))
        {
            // A successful closure without a result waits for its publication or report acceptance.
            return new(task, end is AttemptEnd.Logged { Outcome: TerminalAttemptOutcome.Succeeded } ? TaskState.Settling : TaskState.Failed)
            { Attempt = id, End = end };
        }
        if (LastLaunch(record, id) is not { } last) return new(task, TaskState.Ready) { Attempt = id };
        if (!record.TurnClosures.ContainsKey(last))
            return new(task, record.RootExits.ContainsKey(last) ? TaskState.Settling : TaskState.Uncertain) { Attempt = id };
        var resting = log(record.Attempts[id]);
        return Rests(resting) ? new(task, TaskState.Waiting) { Attempt = id, Status = resting!.Status } : new(task, TaskState.Settling) { Attempt = id };
    }

    private static RunStatus Status(RunPhase phase, bool controlled, bool resumed, ImmutableSortedDictionary<TaskId, TaskView> tasks,
        IReadOnlyDictionary<TaskId, TaskHold> holds)
    {
        switch (phase)
        {
            case RunPhase.Completed: return RunStatus.Completed;
            case RunPhase.Stopped: return RunStatus.Stopped;
            case RunPhase.Failed: return RunStatus.Failed;
            case RunPhase.Abandoned: return RunStatus.Abandoned;
            case RunPhase.StopRequested: return RunStatus.Stopping;
        }
        if (!controlled) return RunStatus.Elsewhere;
        if (!resumed) return RunStatus.Paused;
        var states = tasks.Values.Select(view => view.State).ToArray();
        if (states.Any(state => state is TaskState.Ready or TaskState.Starting or TaskState.Running or TaskState.Settling) ||
            holds.Values.Any(hold => hold.Retried) || states.All(state => state == TaskState.Done))
            return RunStatus.Running;
        if (states.Contains(TaskState.Waiting) &&
            !states.Any(state => state is TaskState.Failed or TaskState.Blocked or TaskState.Uncertain or TaskState.Refused or TaskState.Unsupported))
            return RunStatus.Waiting;
        return RunStatus.NeedsAttention;
    }
}
