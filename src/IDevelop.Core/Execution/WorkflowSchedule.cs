using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>
/// Which nodes of a workflow can start, and what holds back each node that cannot. Pure: it reads the workflow and each
/// node's latest attempt, and starts nothing.
/// <para>
/// A node that has no attempt is ready when every dependency predecessor <see cref="HandedOn">handed on</see> its
/// result. A context connection never holds a node back. A node with an attempt is neither ready nor blocked: it runs,
/// waits, handed on, or ended without a handoff, and then it holds back its dependents.
/// </para>
/// </summary>
/// <param name="Blocked">
/// For each node that has no attempt and cannot start, the nodes that hold it back, each with the status of its latest
/// attempt, or null when that node is ready but has not started. A node that only waits for predecessors that have not
/// started is blocked too, with null statuses. A predecessor that is blocked itself passes on its own holders, so a
/// failure is named by every node below it.
/// </param>
public sealed record WorkflowSchedule(
    ImmutableSortedSet<TaskId> Ready,
    ImmutableSortedDictionary<TaskId, ImmutableSortedDictionary<TaskId, AttemptStatus?>> Blocked)
{
    public static WorkflowSchedule Of(Workflow workflow, IReadOnlyDictionary<TaskId, AttemptRecord> latest)
    {
        var schedule = Schedule.Of(workflow, latest, HandedOn);
        return new WorkflowSchedule(schedule.Ready, schedule.Blocked.ToImmutableSortedDictionary(pair => pair.Key,
            pair => pair.Value.ToImmutableSortedDictionary(holder => holder.Key, holder => holder.Value?.Status)));
    }

    /// <summary>
    /// Whether a node finished with a handoff, which releases its dependents. The one place that decides it. Every work's
    /// <see cref="Nodes.INodeWork.Next"/> finishes when the node's latest attempt succeeded. A review whose reviewer
    /// approved finishes while it still rests in review, and it hands on once <see cref="ProjectRuns"/> takes that step and
    /// records it as succeeded. The switch names every status, so a new one fails the build here until it is placed.
    /// </summary>
    public static bool HandedOn(AttemptRecord? latest) => latest is not null && latest.Status switch
    {
        AttemptStatus.Succeeded => true,
        AttemptStatus.Running or AttemptStatus.WaitingForInput or AttemptStatus.InReview => false,
        AttemptStatus.Failed or AttemptStatus.Cancelled or AttemptStatus.Interrupted => false,
    };
}

/// <summary>
/// The traversal behind <see cref="WorkflowSchedule"/>, over any typed progress: the standalone schedule passes each node's
/// latest attempt, and a workflow run passes its own projection, so an accepted result never has to pose as an attempt.
/// </summary>
internal static class Schedule
{
    /// <param name="Ready">Each node without progress whose dependency predecessors all handed on, in task order.</param>
    /// <param name="Blocked">
    /// Each other node without progress, with the nodes that hold it back: a started holder with its progress, and a ready
    /// holder with null. A holder that is blocked itself passes on its own holders.
    /// </param>
    internal sealed record Plan<T>(ImmutableSortedSet<TaskId> Ready,
        ImmutableSortedDictionary<TaskId, ImmutableSortedDictionary<TaskId, T?>> Blocked) where T : class;

    public static Plan<T> Of<T>(Workflow workflow, IReadOnlyDictionary<TaskId, T> progress, Func<T, bool> handedOn) where T : class
    {
        var none = ImmutableSortedDictionary<TaskId, T?>.Empty;
        var predecessors = workflow.Connections.Where(c => c.Value.Blocks()).ToLookup(c => c.Key.To, c => c.Key.From);
        var holders = new Dictionary<TaskId, ImmutableSortedDictionary<TaskId, T?>>();
        var ready = ImmutableSortedSet.CreateBuilder<TaskId>();
        var blocked = ImmutableSortedDictionary.CreateBuilder<TaskId, ImmutableSortedDictionary<TaskId, T?>>();

        // What a node holds back its dependents with. Dependencies are acyclic, so the recursion ends.
        ImmutableSortedDictionary<TaskId, T?> HoldersOf(TaskId task)
        {
            if (holders.TryGetValue(task, out var known))
            {
                return known;
            }

            var current = progress.GetValueOrDefault(task);
            ImmutableSortedDictionary<TaskId, T?> held;
            if (current is not null && handedOn(current))
            {
                held = none;
            }
            else if (current is not null)
            {
                held = none.Add(task, current);
            }
            else
            {
                var above = predecessors[task].Aggregate(none, (all, predecessor) => all.SetItems(HoldersOf(predecessor)));
                if (above.IsEmpty)
                {
                    ready.Add(task);
                    held = none.Add(task, null);
                }
                else
                {
                    blocked.Add(task, above);
                    held = above;
                }
            }

            holders.Add(task, held);
            return held;
        }

        foreach (var task in workflow.Tasks.Keys)
        {
            HoldersOf(task);
        }

        return new Plan<T>(ready.ToImmutable(), blocked.ToImmutable());
    }
}
