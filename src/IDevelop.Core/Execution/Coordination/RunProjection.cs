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

    /// <summary>A start returned this block. The task is not started again until the journal shows the block resolved, or until Resume.</summary>
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
        foreach (var (task, view) in progress.ToArray())
        {
            // A review whose fix round was interrupted names it, so the person can choose how it goes on.
            if (view is { State: TaskState.Waiting, Status: AttemptStatus.InReview, Attempt: { } review } &&
                log(record.Attempts[review]) is { Status: AttemptStatus.InReview } reviewer)
                progress[task] = view with { Fix = RunReviews.Recovery(record, reviewer, log) };
        }
        var predecessors = RunScope.Predecessors(snapshot);
        // A result that went out of date runs again by itself, as a Retry does, once every task before it is done, and
        // waits for them until then (ADR 0020, #125).
        foreach (var (task, view) in progress.ToArray())
        {
            if (view is not { State: TaskState.Stale, Attempt: not null }) continue;
            ImmutableSortedSet<TaskId> waiting = [.. predecessors[task].Where(predecessor => progress.GetValueOrDefault(predecessor) is not { State: TaskState.Done })];
            progress[task] = holds.GetValueOrDefault(task) switch
            {
                TaskHold.Refused refused => view with { State = TaskState.Refused, Refusal = refused.Reason, Problem = refused.Problem },
                TaskHold.Blocked blocked => view with { State = TaskState.Blocked, Block = blocked.Block },
                _ when !waiting.IsEmpty => view with { State = TaskState.Pending, HeldBy = waiting },
                _ => view with { State = TaskState.Ready, Reruns = true },
            };
        }
        var plan = Schedule.Of(snapshot, progress, view => view.State == TaskState.Done);
        // Only the tasks the person ran and those after them start (#90); Run Workflow runs every root.
        var flow = RunScope.InFlow(record);
        var dormant = RunScope.Dormant(snapshot, flow, task => !progress.ContainsKey(task));
        // The dependency predecessors that have not handed on a result, which the task waits for.
        ImmutableSortedSet<TaskId> Holders(TaskId task) =>
            [.. predecessors[task].Where(predecessor => progress.GetValueOrDefault(predecessor) is not { State: TaskState.Done })];
        var tasks = ImmutableSortedDictionary.CreateBuilder<TaskId, TaskView>();
        foreach (var (task, view) in progress) tasks[task] = view;
        foreach (var task in plan.Ready)
            // A review without a subject has nothing to review, so it never starts.
            tasks[task] = !flow.Contains(task) ? new(task, TaskState.Unrequested) { Dormant = dormant.Contains(task) }
                : new(task, snapshot.Tasks[task].Blueprint.Work is WorkSpec.Agent or WorkSpec.Person or WorkSpec.Merge ||
                    snapshot.Tasks[task].Blueprint.Work is WorkSpec.Review && snapshot.SubjectOf(task) is not null ? TaskState.Ready : TaskState.Unsupported);
        foreach (var task in plan.Blocked.Keys)
            tasks[task] = new(task, flow.Contains(task) ? TaskState.Pending : TaskState.Unrequested) { HeldBy = Holders(task), Dormant = dormant.Contains(task) };
        // A conflict between a task's predecessors goes to a Merge node, or waits for the person when none takes it.
        foreach (var (task, view) in tasks.ToArray())
        {
            if (view.Block is { Problem: MaterializationProblem.FanInConflict, Conflict: { Merge: { } conflict } evidence })
                tasks[task] = view with { MergedBy = MergeRouting.Node(snapshot, conflict, MergeRouting.Branches(evidence)) };
        }
        var built = tasks.ToImmutable();
        var slots = live.Values.Count(stage => stage is LiveStage.Starting or LiveStage.Running);
        return new(address, record.Phase, Status(record.Phase, controlled, resumed, built, holds), controlled, resumed, slots, built) { Snapshot = snapshot };
    }

    /// <summary>
    /// The task's view in a run that settled, as the run's own projection shows a task it started, or null when the run
    /// did not start it (#90). A settled run has no live turns, holds, or resting attempts to read.
    /// </summary>
    public static TaskView? Settled(RunRecord record, TaskId task) => record.Revision.Snapshot.Tasks.ContainsKey(task)
        ? Started(record, task, LatestAttempts(record).TryGetValue(task, out var attempt) ? attempt : null, record.CurrentResults.GetValueOrDefault(task),
            _ => null, ImmutableDictionary<TaskId, LiveStage>.Empty, ImmutableDictionary<TaskId, TaskHold>.Empty)
        : null;

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
        if (record.Revision.Snapshot.Tasks[task].Blueprint.Work is WorkSpec.Person) return GateProjection.Of(record, task, result, hold);
        if (record.Revision.Snapshot.Tasks[task].Blueprint.Work is WorkSpec.Merge) return Merging(record, task, attempt, result, log, hold);
        if (hold is TaskHold.Unresolved unresolved)
        {
            return new(task, unresolved.Transient ? TaskState.Settling : TaskState.Uncertain)
            {
                Attempt = attempt, Unresolved = unresolved.Reason, Refusal = unresolved.Rejection,
                RootExited = attempt is { } id && LastLaunch(record, id) is { } launch && record.RootExits.ContainsKey(launch),
            };
        }
        // An attempt reserved after the current result was accepted, such as a retry or a fix, decides the task's state.
        var current = result is not null && !Replaced(record, result, attempt) ? result : null;
        var block = Blocks(record, task, attempt, current).FirstOrDefault();
        if (current is not null)
        {
            var state = block is not null ? TaskState.Blocked : record.StaleResults.Contains(current.Id) ? TaskState.Stale : TaskState.Done;
            return new(task, state)
            {
                Attempt = attempt, Result = current.Id, Block = block,
                Carried = current.Origin is ResultOrigin.Carried,
            };
        }
        return Attempted(record, task, attempt, log, hold, block) is { } view ? view with { Result = result?.Id } : null;
    }

    private static TaskView? Attempted(RunRecord record, TaskId task, AttemptId? attempt, Func<RunAttempt, AttemptRecord?> log, TaskHold? hold,
        MaterializationBlock? block)
    {
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
            { Attempt = id, End = end, Fixes = record.ReviewOf(id) is not null };
        }
        if (LastLaunch(record, id) is not { } last)
        {
            // A reserved start whose producer's current result, the one it took or a newer one, went stale waits for that
            // producer, as a start not yet reserved does, because its first claim would be refused (#85). Once a producer has
            // a newer current result, the start prepares again by itself with the new inputs (#125).
            var provided = record.Inputs[record.Attempts[id].InitialInputs].Bindings.OfType<InputBinding.Provided>().ToArray();
            var (results, current) = provided.Length == 0
                ? (ImmutableHashSet<ResultId>.Empty, ImmutableDictionary<TaskId, ResultRecord>.Empty) : (record.StaleResults, record.CurrentResults);
            ImmutableSortedSet<TaskId> stale = [.. provided.Where(binding => binding.Kind == ConnectionKind.Dependency &&
                current.GetValueOrDefault(binding.Edge.From) is { } newest && results.Contains(newest.Id)).Select(binding => binding.Edge.From)];
            var changed = provided.Any(binding => current.GetValueOrDefault(binding.Edge.From)?.Id != binding.Result) && record.ReviewOf(id) is null;
            return stale.IsEmpty ? new(task, TaskState.Ready) { Attempt = id, Reruns = changed } : new(task, TaskState.Pending) { Attempt = id, HeldBy = stale };
        }
        if (!record.TurnClosures.ContainsKey(last))
            return new(task, record.RootExits.ContainsKey(last) ? TaskState.Settling : TaskState.Uncertain) { Attempt = id };
        var resting = log(record.Attempts[id]);
        return Rests(resting) ? new(task, TaskState.Waiting) { Attempt = id, Status = resting!.Status } : new(task, TaskState.Settling) { Attempt = id };
    }

    /// <summary>
    /// A Merge node. Its newest attempt, which resolves a conflict that went to it, decides while it goes on, and after it
    /// ended without resolving that conflict while the conflict waits: the node then fails until the person retries it.
    /// Otherwise its own step decides, which merges its predecessors' code and hands it on at once, as an approval that
    /// needs no person.
    /// </summary>
    private static TaskView? Merging(RunRecord record, TaskId task, AttemptId? attempt, ResultRecord? result,
        Func<RunAttempt, AttemptRecord?> log, TaskHold? hold)
    {
        if (attempt is { } id && RunReducer.MergeOf(record, id) is { } conflict)
        {
            if (!record.Closures.TryGetValue(id, out var end))
                return Attempted(record, task, id, log, hold, Blocks(record, task, id, null).FirstOrDefault()) is { } going
                    ? going with { Merge = conflict, Result = result?.Id } : null;
            if (RunReducer.MergeResult(record, id) is null && Waits(record, conflict))
                return new(task, TaskState.Failed) { Attempt = id, End = end, Merge = conflict, Result = result?.Id };
        }
        // A Merge node's request hands on at once, so it never waits for the person, and the person answers none of them.
        return GateProjection.Of(record, task, result, hold) switch
        {
            null => null,
            { State: TaskState.Waiting } own => own with { State = TaskState.Settling, Gate = null },
            var own => own with { Gate = null },
        };
    }

    /// <summary>
    /// Whether <paramref name="conflict"/> still waits for a merge: the task whose predecessors conflict is still blocked by
    /// it, or the run's final merge has not settled.
    /// </summary>
    private static bool Waits(RunRecord record, MergeConflict conflict) => conflict.Target switch
    {
        MergeTarget.Join join => record.Blocks.Values.Any(state => !state.Resolved && state.Block.Task == join.Task &&
            state.Block.Conflict?.Merge is { } blocking && blocking.Merges(conflict.Ours, conflict.Theirs)),
        _ => record.Phase == RunPhase.Approved,
    };

    /// <summary>Whether <paramref name="attempt"/> was reserved after <paramref name="result"/> was accepted, without publishing it.</summary>
    internal static bool Replaced(RunRecord record, ResultRecord result, AttemptId? attempt)
    {
        if (attempt is not { } id || result.Origin is ResultOrigin.Executed executed && executed.Attempt == id) return false;
        long Sequence(Func<RunEvent, bool> match) => record.Receipts.Values.Where(entry => match(entry.Event)).Select(entry => entry.Sequence).DefaultIfEmpty(0).Max();
        return Sequence(e => e is RunEvent.Reserved reserved && reserved.Attempt.Id == id) >
            Sequence(e => e is RunEvent.ResultAccepted accepted && accepted.Result.Id == result.Id);
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
            holds.Values.Any(hold => hold.Retried) || tasks.Values.All(view => view.State == TaskState.Done || view.Dormant))
            return RunStatus.Running;
        if (states.Contains(TaskState.Waiting) &&
            !states.Any(state => state is TaskState.Failed or TaskState.Stale or TaskState.Blocked or TaskState.Uncertain or TaskState.Refused or
                TaskState.Unsupported or TaskState.SentBack))
            return RunStatus.Waiting;
        return RunStatus.NeedsAttention;
    }
}
