using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>
/// Which Merge node a run's merge conflict goes to, as the owner decided in issue #148. A conflict between the predecessors
/// of a Merge node goes to that node. Any other conflict between predecessors goes to the Merge node that the conflicting
/// branches connect into, and the run's final merge to the Merge node with no successors. A workflow with one Merge node
/// gives it every conflict. Without a Merge node to take it, a conflict waits for the person.
/// </summary>
internal static class MergeRouting
{
    /// <summary>The Merge node that resolves <paramref name="conflict"/>, or null when none takes it.</summary>
    public static TaskId? Node(Workflow workflow, MergeConflict conflict, ImmutableArray<TaskId> branches) =>
        Node(workflow, conflict.Target, branches);

    /// <param name="branches">The tasks whose work conflicts, for a conflict between predecessors.</param>
    public static TaskId? Node(Workflow workflow, MergeTarget target, ImmutableArray<TaskId> branches)
    {
        ImmutableArray<TaskId> merges = [.. workflow.Tasks.Values.Where(task => task.Blueprint.Work is WorkSpec.Merge).Select(task => task.Id).Order()];
        if (merges.IsEmpty) return null;
        switch (target)
        {
            case MergeTarget.Join join:
                if (merges.Contains(join.Task)) return join.Task;
                var connected = merges.Where(merge => !branches.IsEmpty &&
                    branches.All(branch => workflow.Connections.Keys.Any(edge => edge.From == branch && edge.To == merge))).ToArray();
                if (connected.Length != 0) return connected[0];
                break;
            case MergeTarget.Branch:
                var last = merges.Where(merge => !workflow.Connections.Any(edge => edge.Key.From == merge && edge.Value.Blocks())).ToArray();
                if (last.Length != 0) return last[0];
                break;
        }
        return merges.Length == 1 ? merges[0] : null;
    }

    /// <summary>
    /// The tasks whose work a conflict between predecessors merges: each source whose commit the conflicting step merges,
    /// the work merged before it and the work it adds.
    /// </summary>
    public static ImmutableArray<TaskId> Branches(ConflictEvidence evidence)
    {
        ImmutableArray<CommitId> parents = [.. evidence.Sources.Select(source => source.Commit).Distinct()];
        var merged = parents.Take(Math.Min(evidence.Step + 1, parents.Length)).ToHashSet();
        return [.. evidence.Sources.Where(source => merged.Contains(source.Commit)).Select(source => source.Task).Distinct().Order()];
    }
}
