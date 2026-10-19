using System.Collections.Immutable;

namespace IDevelop.Execution;

/// <summary>What the person's step on a merge conflict that waits for them did.</summary>
internal abstract record ConflictReply
{
    private ConflictReply() { }

    /// <summary>The conflict's worktree is at <paramref name="Folder"/>, for the person to fix its files.</summary>
    internal sealed record Opened(string Folder) : ConflictReply;

    /// <summary>The person's files are committed as the merge, and the run goes on from it.</summary>
    internal sealed record Resolved(CommitId Merge) : ConflictReply;

    /// <summary>The conflict's worktree was never opened, so there is nothing of the person's to commit.</summary>
    internal sealed record Unopened : ConflictReply;

    /// <summary><paramref name="Paths"/> still hold the conflict markers, so nothing is committed.</summary>
    internal sealed record MarkersRemain(ImmutableArray<string> Paths) : ConflictReply;

    /// <summary>Git could not do it, for <paramref name="Detail"/>.</summary>
    internal sealed record Failed(string Detail) : ConflictReply;

    internal sealed record Refused(RunRejection Reason) : ConflictReply;

    /// <summary>Another window controls the run. Nothing was done.</summary>
    internal sealed record Unavailable(string Message) : ConflictReply;
}

/// <summary>
/// The person resolves a merge conflict that no Merge node takes (#148): they open its worktree, fix its files, and mark it
/// resolved. Their files then serve as a Merge node's merge does, between predecessors and in the final merge.
/// </summary>
internal sealed partial class WorkflowRunCoordinator
{
    /// <summary>
    /// The worktree of <paramref name="conflict"/>, which waits for the person, detached at the merge with its conflict markers.
    /// A repeat finds the same worktree with the person's files as they are.
    /// </summary>
    internal Task<ConflictReply> OpenConflict(RunAddress address, MergeConflict conflict, CancellationToken wait = default) =>
        Converse<ConflictReply>(address, (record, complete) =>
        {
            if (AsksPerson(record, conflict) is { } problem)
            {
                complete(new ConflictReply.Refused(new(problem)));
                return;
            }
            Offload(() => Commanded().OpenConflict(_permit!, conflict), opened => complete(opened switch
            {
                ConflictCheckout.Ready ready => new ConflictReply.Opened(ready.Folder),
                ConflictCheckout.Failed failed => new ConflictReply.Failed(failed.Detail),
                ConflictCheckout.Rejected rejected => new ConflictReply.Refused(rejected.Reason),
                _ => throw new InvalidOperationException(),
            }), error => complete(new ConflictReply.Failed(error.Message)));
        }, ConflictRefusal, wait);

    /// <summary>
    /// Commits the person's files in <paramref name="conflict"/>'s worktree as its merge, as they confirmed under
    /// <paramref name="confirmation"/>. Refused while a conflicting file holds the conflict markers. The run then goes on as
    /// it does after a Merge node's merge: the task whose predecessors conflicted starts from it, or the final merge uses it.
    /// </summary>
    internal Task<ConflictReply> MarkResolved(RunAddress address, MergeConflict conflict, OperationId confirmation, CancellationToken wait = default) =>
        Converse<ConflictReply>(address, (record, complete) =>
        {
            if (confirmation.Value == Guid.Empty)
            {
                complete(new ConflictReply.Refused(new(RunProblem.ConfirmationRequired)));
                return;
            }
            if (AsksPerson(record, conflict) is { } problem)
            {
                complete(new ConflictReply.Refused(new(problem)));
                return;
            }
            Offload(() => Commanded().ResolveConflict(_permit!, OperationIds.Derive(confirmation, "resolve"), conflict), resolved =>
            {
                // The final merge looks again at once, and finds the person's merge.
                if (resolved is ConflictResolution.Resolved && conflict.Target is MergeTarget.Branch) _mergeLater = false;
                complete(resolved switch
                {
                    ConflictResolution.Resolved done => new ConflictReply.Resolved(done.Merge),
                    ConflictResolution.Unopened => new ConflictReply.Unopened(),
                    ConflictResolution.MarkersRemain markers => new ConflictReply.MarkersRemain(markers.Paths),
                    ConflictResolution.Failed failed => new ConflictReply.Failed(failed.Detail),
                    ConflictResolution.Rejected rejected => new ConflictReply.Refused(rejected.Reason),
                    _ => throw new InvalidOperationException(),
                });
            }, error => complete(new ConflictReply.Failed(error.Message)));
        }, ConflictRefusal, wait);

    /// <summary>The conflict between a blocked task's predecessors that no Merge node takes, so it waits for the person, or null.</summary>
    internal static MergeConflict? PersonsConflict(TaskView? task) =>
        task is { State: TaskState.Blocked, MergedBy: null, Block: { Problem: MaterializationProblem.FanInConflict, Conflict.Merge: { } conflict } }
            ? conflict : null;

    /// <summary>The conflict of the run's final merge that no Merge node takes, so it waits for the person, or null.</summary>
    internal static MergeConflict? PersonsConflict(RunView view) =>
        view.Phase == RunPhase.Approved && view.Merge is BranchMergeWait.Conflict { Node: null, Merge: { } conflict } ? conflict : null;

    /// <summary>Why <paramref name="conflict"/> does not wait for the person now, or null when it does.</summary>
    private RunProblem? AsksPerson(RunRecord record, MergeConflict conflict)
    {
        if (record.Phase != RunPhase.Approved) return RunProblem.RunStopped;
        var waits = conflict.Target switch
        {
            MergeTarget.Join => Project(record).Tasks.Values.Any(task => PersonsConflict(task) is { } waiting && waiting.Equals(conflict)),
            MergeTarget.Branch => _branchMerge is BranchMergeWait.Conflict { Node: null, Merge: { } waiting } && waiting.Equals(conflict),
            _ => false,
        };
        return waits ? null : RunProblem.StaleInput;
    }

    private static ConflictReply ConflictRefusal(RunCommand command) => command switch
    {
        RunCommand.Unavailable unavailable => new ConflictReply.Unavailable(unavailable.Message),
        RunCommand.Refused refused => new ConflictReply.Refused(refused.Reason),
        _ => throw new InvalidOperationException(),
    };
}
