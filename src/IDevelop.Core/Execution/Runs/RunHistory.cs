using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>A task's result as the run that recorded it holds it.</summary>
internal sealed record EarlierResult(RunRecord Run, ResultRecord Result)
{
    /// <summary>When the run recorded the result: its acceptance, or the approval or request that included or carried it.</summary>
    public DateTimeOffset At => Run.Receipts.Values.Where(entry => entry.Event switch
    {
        RunEvent.ResultAccepted accepted => accepted.Result.Id == Result.Id,
        RunEvent.Approved approved => (approved.Included ?? []).Concat(approved.Carried ?? []).Any(item => item.Result.Id == Result.Id),
        RunEvent.Requested requested => (requested.Carried ?? []).Any(item => item.Result.Id == Result.Id),
        _ => false,
    }).Select(entry => entry.At).DefaultIfEmpty(DateTimeOffset.MinValue).First();
}

/// <summary>Why a task's result from an earlier run no longer counts as complete (#90).</summary>
internal enum OutOfDateReason
{
    /// <summary>The task's definition, or the connections that lead into it, changed since it ran.</summary>
    Changed,

    /// <summary>A task it took a result from has another result now, or none.</summary>
    InputReplaced,

    /// <summary>A task it took a result from has a result that no longer counts itself.</summary>
    InputOutOfDate,
}

/// <summary>What a task has from the runs of its workflow that have settled (#90).</summary>
internal abstract record TaskHistory
{
    private TaskHistory() { }

    /// <summary>No settled run left the task a result: none ran it, or its newest run that did ended without one.</summary>
    internal sealed record None : TaskHistory;

    /// <summary>
    /// The task's result from its newest run that ran it still counts as complete: it was that run's current result and
    /// not stale, the task and the connections into it are unchanged, and each result it took counts the same way.
    /// </summary>
    internal sealed record Current(EarlierResult Result) : TaskHistory;

    /// <summary>The task's newest result no longer counts, for <paramref name="Reason"/>; <paramref name="Input"/> names the task it took a result from, when that is why.</summary>
    internal sealed record OutOfDate(EarlierResult Result, OutOfDateReason Reason, TaskId? Input) : TaskHistory;
}

/// <summary>
/// The results that a workflow's settled runs left its tasks, judged against the workflow as it is now (#90). A result from
/// an earlier run counts as complete when it was its run's current, non-stale result, no later run ran the task, the task's
/// definition and the connections into it are unchanged, and each result it took counts by the same rule. Whether its code
/// still applies to the run's base is the one condition this does not check: carrying it replays it there.
/// </summary>
internal sealed class RunHistory
{
    private readonly ImmutableArray<RunRecord> _runs;
    private readonly Workflow _workflow;
    private readonly Dictionary<TaskId, TaskHistory> _known = [];
    private readonly HashSet<TaskId> _judging = [];

    private RunHistory(ImmutableArray<RunRecord> runs, Workflow workflow)
    {
        _runs = runs;
        _workflow = workflow;
    }

    /// <summary>
    /// The history of <paramref name="workflow"/>'s tasks from <paramref name="records"/>. Only E3 runs that have settled
    /// count, in the order they were approved: a workflow has one active run at a time, so its runs never overlap.
    /// </summary>
    public static RunHistory Of(IEnumerable<RunRecord> records, Workflow workflow) => new([.. records
        .Where(record => record.Workflow == workflow.Id && record.Schema == 3 && record.Phase is RunPhase.Completed or RunPhase.Stopped or RunPhase.Failed)
        .OrderBy(Approval).ThenBy(record => record.Id.Value.ToString("D"), StringComparer.Ordinal)], workflow);

    /// <summary>The settled runs this history reads, oldest first.</summary>
    public ImmutableArray<RunRecord> Runs => _runs;

    public TaskHistory this[TaskId task] => Judge(task);

    /// <summary>The task's result that counts as complete, or null.</summary>
    public EarlierResult? CurrentOf(TaskId task) => Judge(task) is TaskHistory.Current current ? current.Result : null;

    /// <summary>
    /// What <paramref name="task"/> waits for between runs, from its newest settled run that could start it, when that run
    /// did not: the dependency predecessors in the workflow that had no current result in that run and have none from a
    /// later run since. Empty when the task has a result of its own or waits for nothing.
    /// </summary>
    public ImmutableSortedSet<TaskId> WaitsFor(TaskId task)
    {
        if (Judge(task) is not TaskHistory.None) return [];
        for (var index = _runs.Length - 1; index >= 0; index--)
        {
            var record = _runs[index];
            if (!RunScope.InFlow(record).Contains(task)) continue;
            var results = record.CurrentResults;
            var stale = record.StaleResults;
            ImmutableSortedSet<TaskId> holders = [.. RunScope.Predecessors(_workflow)[task].Where(predecessor =>
                !(results.TryGetValue(predecessor, out var result) && !stale.Contains(result.Id)) &&
                !(CurrentOf(predecessor) is { } later && Position(later.Run.Id) > index))];
            return holders;
        }
        return [];
    }

    /// <summary>The result <paramref name="result"/> stands for: an earlier run's result for one that was carried from it, itself otherwise.</summary>
    public (RunId Run, ResultId Result) Root(RunRecord run, ResultId result)
    {
        for (var depth = 0; depth < 64; depth++)
        {
            if (run.Results.FirstOrDefault(item => item.Id == result)?.Origin is not ResultOrigin.Carried carried) return (run.Id, result);
            if (_runs.FirstOrDefault(item => item.Id == carried.Run) is not { } source) return (carried.Run, carried.Result);
            (run, result) = (source, carried.Result);
        }
        return (run.Id, result);
    }

    private int Position(RunId run)
    {
        for (var index = 0; index < _runs.Length; index++)
            if (_runs[index].Id == run) return index;
        return -1;
    }

    private static DateTimeOffset Approval(RunRecord record) =>
        record.Receipts.Values.Where(entry => entry.Sequence == 1).Select(entry => entry.At).DefaultIfEmpty(DateTimeOffset.MinValue).First();

    private TaskHistory Judge(TaskId task)
    {
        if (_known.TryGetValue(task, out var known)) return known;
        // Context connections may form cycles. A result that would need itself to count does not.
        if (!_judging.Add(task)) return new TaskHistory.None();
        try
        {
            var judged = Newest(task) is { } newest ? Counts(task, newest) : new TaskHistory.None();
            _known[task] = judged;
            return judged;
        }
        finally { _judging.Remove(task); }
    }

    /// <summary>The task's result in its newest run that ran it, if that run left it one that was done, else null.</summary>
    private EarlierResult? Newest(TaskId task)
    {
        for (var index = _runs.Length - 1; index >= 0; index--)
        {
            var record = _runs[index];
            if (!Touches(record, task)) continue;
            return Done(record, task) is { } result ? new EarlierResult(record, result) : null;
        }
        return null;
    }

    private static bool Touches(RunRecord record, TaskId task) => record.Attempts.Values.Any(attempt => attempt.Task == task) ||
        record.Gates.Values.Any(gate => gate.Request.Task == task) || record.Results.Any(result => result.Task == task);

    /// <summary>
    /// The task's current result, as the run's own projection would show it done, or null: an attempt reserved after it,
    /// such as a failed retry, replaced it, or an unresolved block on it holds it. A stale result is returned, so it can say
    /// which input it is out of date for.
    /// </summary>
    private static ResultRecord? Done(RunRecord record, TaskId task)
    {
        if (record.CurrentResults.GetValueOrDefault(task) is not { } result) return null;
        if (!record.Revisions.TryGetValue(result.Revision, out var revision) || !revision.Snapshot.Tasks.TryGetValue(task, out var definition)) return null;
        if (definition.Blueprint.Work is WorkSpec.Person) return result;
        AttemptId? attempt = RunProjection.LatestAttempts(record).TryGetValue(task, out var latest) ? latest : null;
        if (RunProjection.Replaced(record, result, attempt)) return null;
        return RunProjection.Blocks(record, task, attempt, result).Any() ? null : result;
    }

    private TaskHistory Counts(TaskId task, EarlierResult earlier)
    {
        var (record, result) = (earlier.Run, earlier.Result);
        if (!RunReducer.SameTask(record.Revisions[result.Revision].Snapshot, _workflow, task))
            return new TaskHistory.OutOfDate(earlier, OutOfDateReason.Changed, null);
        // A result that was stale in its own run took a result its producer replaced, or one that is stale in turn, so
        // the same check covers it.
        foreach (var binding in record.Inputs[result.Inputs].Bindings.OfType<InputBinding.Provided>())
        {
            var producer = binding.Edge.From;
            switch (Judge(producer))
            {
                case TaskHistory.Current current when Root(current.Result.Run, current.Result.Result.Id) == Root(record, binding.Result):
                    continue;
                case TaskHistory.OutOfDate other when Root(other.Result.Run, other.Result.Result.Id) == Root(record, binding.Result):
                    return new TaskHistory.OutOfDate(earlier, OutOfDateReason.InputOutOfDate, producer);
                default:
                    return new TaskHistory.OutOfDate(earlier, OutOfDateReason.InputReplaced, producer);
            }
        }
        return new TaskHistory.Current(earlier);
    }
}
