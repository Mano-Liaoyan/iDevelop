using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>
/// Which tasks of a run may start (#90). Run Workflow runs every root, so every task may start once its dependency
/// predecessors have results. A run that a node's Run started runs that node, and any node the person runs while it is
/// active, and each task after one of them once all of that task's dependency predecessors have results. Nothing starts
/// merely because it comes after another task: a task waits for every predecessor, however that predecessor was started.
/// </summary>
internal static class RunScope
{
    /// <summary>The tasks that may start in <paramref name="record"/> once their dependency predecessors have results.</summary>
    public static ImmutableHashSet<TaskId> InFlow(RunRecord record) => InFlow(record.Revision.Snapshot, record.Requested);

    /// <summary>
    /// Every task when <paramref name="requested"/> is null. Otherwise each requested task and each task that a dependency
    /// leads to from one of them, directly or through other tasks.
    /// </summary>
    public static ImmutableHashSet<TaskId> InFlow(Workflow workflow, IReadOnlySet<TaskId>? requested)
    {
        if (requested is null) return [.. workflow.Tasks.Keys];
        var successors = workflow.Connections.Where(connection => connection.Value.Blocks()).ToLookup(connection => connection.Key.From, connection => connection.Key.To);
        var flow = ImmutableHashSet.CreateBuilder<TaskId>();
        var queue = new Queue<TaskId>(requested.Where(workflow.Tasks.ContainsKey));
        while (queue.TryDequeue(out var task))
        {
            if (!flow.Add(task)) continue;
            foreach (var next in successors[task]) queue.Enqueue(next);
        }
        return flow.ToImmutable();
    }

    /// <summary>
    /// The tasks that can no longer start in the run: each idle task outside <paramref name="flow"/>, and each idle task
    /// with a dependency predecessor that can no longer start. A run completes once every other task has a current result.
    /// </summary>
    /// <param name="idle">Whether the task has nothing in the run yet: no attempt, no request for approval, and no result.</param>
    public static ImmutableHashSet<TaskId> Dormant(Workflow workflow, IReadOnlySet<TaskId> flow, Func<TaskId, bool> idle)
    {
        var predecessors = Predecessors(workflow);
        var known = new Dictionary<TaskId, bool>();

        // Dependencies are acyclic, so the recursion ends.
        bool Dormant(TaskId task)
        {
            if (known.TryGetValue(task, out var dormant)) return dormant;
            dormant = idle(task) && (!flow.Contains(task) || predecessors[task].Any(Dormant));
            known.Add(task, dormant);
            return dormant;
        }

        return [.. workflow.Tasks.Keys.Where(Dormant)];
    }

    /// <summary>The dependency predecessors of each task.</summary>
    public static ILookup<TaskId, TaskId> Predecessors(Workflow workflow) =>
        workflow.Connections.Where(connection => connection.Value.Blocks()).ToLookup(connection => connection.Key.To, connection => connection.Key.From);

    /// <summary>
    /// The tasks a run of <paramref name="record"/> can no longer start, judged from the journal alone: a task is idle while
    /// it has no attempt, no request for approval, and no result in the run.
    /// </summary>
    public static ImmutableHashSet<TaskId> Dormant(RunRecord record) => Dormant(record.Revision.Snapshot, InFlow(record), task =>
        !record.Attempts.Values.Any(attempt => attempt.Task == task) && !record.Gates.Values.Any(gate => gate.Request.Task == task) &&
        !record.Results.Any(result => result.Task == task));

    /// <summary>
    /// The first dependency predecessor of <paramref name="task"/> without a current result that hands on, in task order,
    /// or null when each has one, so the task can start.
    /// </summary>
    public static TaskId? Missing(RunRecord record, TaskId task)
    {
        var results = record.CurrentResults;
        var stale = record.StaleResults;
        return Predecessors(record.Revision.Snapshot)[task].Order()
            .Select(predecessor => (TaskId?)predecessor)
            .FirstOrDefault(predecessor => !results.TryGetValue(predecessor!.Value, out var result) || stale.Contains(result.Id));
    }
}
