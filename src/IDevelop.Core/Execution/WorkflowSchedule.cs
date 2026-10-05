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
    private static readonly ImmutableSortedDictionary<TaskId, AttemptStatus?> NoHolders = ImmutableSortedDictionary<TaskId, AttemptStatus?>.Empty;

    public static WorkflowSchedule Of(Workflow workflow, IReadOnlyDictionary<TaskId, AttemptRecord> latest)
    {
        var predecessors = workflow.Connections.Where(c => c.Value.Blocks()).ToLookup(c => c.Key.To, c => c.Key.From);
        var holders = new Dictionary<TaskId, ImmutableSortedDictionary<TaskId, AttemptStatus?>>();
        var ready = ImmutableSortedSet.CreateBuilder<TaskId>();
        var blocked = ImmutableSortedDictionary.CreateBuilder<TaskId, ImmutableSortedDictionary<TaskId, AttemptStatus?>>();

        // What a node holds back its dependents with. Dependencies are acyclic, so the recursion ends.
        ImmutableSortedDictionary<TaskId, AttemptStatus?> HoldersOf(TaskId task)
        {
            if (holders.TryGetValue(task, out var known))
            {
                return known;
            }

            var attempt = latest.GetValueOrDefault(task);
            ImmutableSortedDictionary<TaskId, AttemptStatus?> held;
            if (HandedOn(attempt))
            {
                held = NoHolders;
            }
            else if (attempt is not null)
            {
                held = NoHolders.Add(task, attempt.Status);
            }
            else
            {
                var above = predecessors[task].Aggregate(NoHolders, (all, predecessor) => all.SetItems(HoldersOf(predecessor)));
                if (above.IsEmpty)
                {
                    ready.Add(task);
                    held = NoHolders.Add(task, null);
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

        return new WorkflowSchedule(ready.ToImmutable(), blocked.ToImmutable());
    }

    /// <summary>
    /// Whether a node finished with a handoff, which releases its dependents. The one place that decides it. Every work's
    /// <see cref="Nodes.INodeWork.Next"/> finishes exactly when the node's latest attempt succeeded. The switch names every
    /// status, so a new one fails the build here until it is placed.
    /// </summary>
    public static bool HandedOn(AttemptRecord? latest) => latest is not null && latest.Status switch
    {
        AttemptStatus.Succeeded => true,
        AttemptStatus.Running or AttemptStatus.WaitingForInput => false,
        AttemptStatus.Failed or AttemptStatus.Cancelled or AttemptStatus.Interrupted => false,
    };
}
