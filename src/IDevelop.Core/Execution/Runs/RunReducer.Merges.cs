using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>
/// The merge conflicts a run's Merge nodes resolve. Each conflict starts an attempt of the Merge node it goes to, and the
/// attempt's last capture, made of its agent's files on the conflict's two parents, is the merge once the attempt closes
/// as succeeded. A Retry or a Continue of such an attempt resolves the same conflict.
/// </summary>
internal static partial class RunReducer
{
    /// <summary>The conflict <paramref name="attempt"/> resolves, or null for an attempt that resolves none.</summary>
    internal static MergeConflict? MergeOf(RunRecord record, AttemptId attempt) =>
        record.Attempts.TryGetValue(attempt, out var owner) ? MergeOf(record, owner.Cause) : null;

    /// <summary>The conflict an attempt with <paramref name="cause"/> resolves, following a Retry or a Continue to the attempt it replaces.</summary>
    internal static MergeConflict? MergeOf(RunRecord record, AttemptCause cause)
    {
        for (var depth = 0; depth <= record.Attempts.Count; depth++)
        {
            switch (cause)
            {
                case AttemptCause.Merge merge:
                    return merge.Conflict;
                case AttemptCause.Retry or AttemptCause.Continue:
                    if (Previous(cause) is not { } previous || !record.Attempts.TryGetValue(previous, out var earlier)) return null;
                    cause = earlier.Cause;
                    break;
                default:
                    return null;
            }
        }
        return null;
    }

    /// <summary>
    /// The merge <paramref name="attempt"/> made: the commit its last turn's capture took of its agent's files on the conflict's
    /// two parents, once the attempt closed as succeeded. Null otherwise, as when conflict markers remained.
    /// </summary>
    internal static CommitId? MergeResult(RunRecord record, AttemptId attempt)
    {
        if (MergeOf(record, attempt) is not { } conflict ||
            record.Closures.GetValueOrDefault(attempt) is not AttemptEnd.Logged { Outcome: TerminalAttemptOutcome.Succeeded } ||
            record.Claims.Keys.Where(key => key.Attempt == attempt).OrderBy(key => key.Turn).Select(key => (LaunchKey?)key).LastOrDefault() is not { } last ||
            MatchedCapture(record, last) is not { Candidate: { } commit } capture ||
            !capture.Recipe.Parents.SequenceEqual([conflict.Ours, conflict.Theirs]))
            return null;
        return commit;
    }

    /// <summary>
    /// The merge that resolved merging <paramref name="theirs"/> into <paramref name="ours"/>, the newest one, or null: a
    /// Merge node's agent made it, or the person did and marked it resolved.
    /// </summary>
    internal static CommitId? Resolution(RunRecord record, CommitId ours, CommitId theirs) => record.Receipts.Values
        .Where(entry => entry.Event is RunEvent.AttemptClosed or RunEvent.ConflictResolved).OrderByDescending(entry => entry.Sequence)
        .Select(entry => entry.Event switch
        {
            RunEvent.ConflictResolved resolved => resolved.Conflict.Merges(ours, theirs) ? resolved.Merge : null,
            RunEvent.AttemptClosed { Attempt: var attempt } => MergeOf(record, attempt) is { } conflict && conflict.Merges(ours, theirs)
                ? MergeResult(record, attempt) : null,
            _ => null,
        }).FirstOrDefault(commit => commit is not null);

    /// <summary>
    /// Why the person cannot mark <paramref name="conflict"/> resolved, or null when they can: it must wait for them, as a
    /// block on the task whose predecessors conflict or as the run's final merge, and have no merge yet.
    /// </summary>
    internal static RunProblem? ConflictResolutionProblem(RunRecord record, MergeConflict conflict)
    {
        if (record.Phase != RunPhase.Approved) return RunProblem.RunStopped;
        var waits = conflict.Target switch
        {
            MergeTarget.Join join => record.Blocks.Values.Any(state => !state.Resolved && state.Block.Task == join.Task &&
                state.Block.Conflict?.Merge is { } blocking && blocking.Equals(conflict)),
            MergeTarget.Branch branch => branch.Name == record.Base.Branch,
            _ => false,
        };
        if (!waits) return RunProblem.StaleInput;
        return Resolution(record, conflict.Ours, conflict.Theirs) is null ? null : RunProblem.StartConflict;
    }

    /// <summary>Whether <paramref name="task"/>'s attempt <paramref name="attempt"/> resolves a merge conflict, which a Merge node's every attempt does.</summary>
    internal static bool Merges(RunRecord record, AttemptId attempt) => MergeOf(record, attempt) is not null;

    /// <summary>Why a Merge node's attempt cannot take <paramref name="conflict"/>, or null when it can.</summary>
    private static RunProblem? MergeProblem(RunRecord record, TaskId task, MergeConflict conflict)
    {
        if (record.Revision.Snapshot.Tasks.GetValueOrDefault(task)?.Blueprint.Work is not WorkSpec.Merge) return RunProblem.UnsupportedWork;
        if (conflict.Paths.IsDefault || conflict.Description.Length == 0) return RunProblem.InvalidData;
        if (conflict.Target is MergeTarget.Join join && !KnownTask(record, join.Task)) return RunProblem.IdentityMismatch;
        if (conflict.Target is MergeTarget.Branch branch && branch.Name != record.Base.Branch) return RunProblem.IdentityMismatch;
        return Resolution(record, conflict.Ours, conflict.Theirs) is null ? null : RunProblem.StartConflict;
    }

    /// <summary>The inputs of a Merge node's attempt: the conflict it resolves, on no result of another task.</summary>
    internal static bool MergeInput(RunRecord record, InputRecord input) => input.Conflict is not null &&
        input.Bindings.IsEmpty && input.Review is null && input.Files.IsEmpty && input.Text.Length == 0 &&
        input.Code == new CodeSelection.Root(input.Conflict.Conflicted) &&
        record.Revisions.TryGetValue(input.Revision, out var revision) &&
        revision.Snapshot.Tasks.GetValueOrDefault(input.Task)?.Blueprint.Work is WorkSpec.Merge;

    /// <summary>
    /// Whether <paramref name="task"/> is a Merge node with no dependency predecessor, which no run starts on its own: it only
    /// resolves the conflicts that go to it.
    /// </summary>
    internal static bool OnCall(Workflow workflow, TaskId task) =>
        workflow.Tasks.GetValueOrDefault(task)?.Blueprint.Work is WorkSpec.Merge &&
        !workflow.Connections.Any(connection => connection.Key.To == task && connection.Value.Blocks());
}
