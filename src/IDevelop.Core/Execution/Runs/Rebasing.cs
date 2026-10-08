using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>
/// What Review updated inputs shows for a task whose current result is stale: the inputs it consumed and the current ones,
/// the change it recorded, the report and artifacts a rebase carries forward, and the clean candidate, if there is one.
/// Approval binds <see cref="Identity"/>.
/// </summary>
/// <param name="Stale">The task's current, stale result.</param>
/// <param name="Previous">The inputs the stale result consumed.</param>
/// <param name="Current">The current input bindings a rebase records.</param>
/// <param name="Updated">The producers whose current result differs from the one the stale result consumed.</param>
/// <param name="OldBase">The stale result's effective upstream base: the code its inputs carried.</param>
/// <param name="NewBase">The current inputs' code: one result's commit, a join of several, or the run base. Null when no join could be built.</param>
/// <param name="Changes">The paths the task's recorded change touches, from <paramref name="OldBase"/> to its result commit.</param>
/// <param name="Report">The stale result's report, which the rebased result carries forward.</param>
/// <param name="Artifacts">The stale result's artifacts, which the rebased result carries forward.</param>
internal sealed record RebasePreview(TaskId Task, ResultId Stale, InputRecord Previous, ImmutableArray<InputBinding> Current,
    ImmutableArray<TaskId> Updated, CommitId OldBase, CommitId? NewBase, ImmutableArray<string> Changes, string Report,
    ImmutableArray<ArtifactRecord> Artifacts, RebaseCandidate Candidate, Digest Identity);

internal abstract record RebaseCandidate
{
    private RebaseCandidate() { }

    /// <summary>The task's change replays cleanly. <paramref name="Updates"/> are the paths the rebase changes in the task's checkout.</summary>
    internal sealed record Clean(CommitId Commit, TreeId Tree, ImmutableArray<string> Updates) : RebaseCandidate;

    /// <summary>The change does not replay cleanly, or the current inputs do not join cleanly. Nothing is rebased; Retry remains.</summary>
    internal sealed record Conflicted(ImmutableArray<string> Paths, string Detail) : RebaseCandidate;

    /// <summary>This Git cannot build the candidate.</summary>
    internal sealed record Unavailable(MaterializationProblem Problem, string Detail) : RebaseCandidate;
}

internal abstract record RebasePreviewRead
{
    private RebasePreviewRead() { }

    internal sealed record Previewed(RebasePreview Preview) : RebasePreviewRead;

    /// <summary>The task's checkout or repository does not allow a rebase now. Nothing was recorded.</summary>
    internal sealed record Refused(MaterializationProblem Problem, string Detail, BlockScope Scope) : RebasePreviewRead;

    internal sealed record Rejected(RunRejection Reason) : RebasePreviewRead;

    /// <summary>Another window controls the run.</summary>
    internal sealed record Unavailable(string Message) : RebasePreviewRead;
}

internal abstract record Rebasing
{
    private Rebasing() { }

    internal sealed record Rebased(ResultRecord Result) : Rebasing;

    internal sealed record Blocked(MaterializationBlock Block) : Rebasing;

    internal sealed record Rejected(RunRejection Reason) : Rebasing;

    /// <summary>Another window controls the run. Nothing was recorded.</summary>
    internal sealed record Unavailable(string Message) : Rebasing;
}
