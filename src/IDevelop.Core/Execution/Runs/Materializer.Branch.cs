using System.Collections.Immutable;

namespace IDevelop.Execution;

/// <summary>What the run's final merge into the branch it started from came to.</summary>
internal abstract record BranchMerge
{
    private BranchMerge() { }

    /// <summary>The branch holds the run's work at <paramref name="Commit"/>, merged now or before.</summary>
    internal sealed record Merged(CommitId Commit) : BranchMerge;

    /// <summary>The folder that has the branch checked out has uncommitted changes, so the merge waits and touches nothing.</summary>
    internal sealed record Uncommitted(string Folder, ImmutableArray<string> Paths) : BranchMerge;

    /// <summary>Merging the run's work into the branch conflicts. A Merge node's agent resolves it, or the person does.</summary>
    internal sealed record Conflict(MergeConflict Merge) : BranchMerge;

    /// <summary>Git could not merge, or the branch moved meanwhile. The merge tries again.</summary>
    internal sealed record Problem(string Detail) : BranchMerge;

    internal sealed record Rejected(RunRejection Reason) : BranchMerge;
}

internal sealed partial class Materializer
{
    /// <summary>
    /// Merges the run's finished work into the branch it started from (#148). The work is the code of the tasks' current
    /// results that no other result's code holds. Each merges into the branch's tip in turn: as a fast-forward when the
    /// branch has not moved, as a merge commit when the merge is clean, and as a Merge node's merge when it conflicted and a
    /// Merge node's agent resolved it. The branch then moves to the result. When a folder has the branch checked out, its
    /// files move with it, and only while it has no uncommitted change: otherwise nothing moves and the merge waits.
    /// A repeat converges, since work the branch already holds merges as nothing.
    /// </summary>
    public BranchMerge MergeIntoBranch(CoordinatorPermit permit)
    {
        try
        {
            var record = Read(permit.Workflow, permit.Run);
            if (record.Base.Branch is not { } branch) return new BranchMerge.Rejected(new(RunProblem.InvalidData));
            var repository = OpenRepository();
            using var held = MutationLock(repository);
            if (held is null) return new BranchMerge.Rejected(new(RunProblem.JournalBusy));
            if (Value(repository.ReadRef(branch)) is not { } tip) return new BranchMerge.Problem($"The branch {MergeMarkers.BranchName(branch)} no longer exists.");
            var merged = tip;
            foreach (var head in Heads(record, repository))
            {
                if (Ancestor(repository, head, merged)) continue;
                if (Ancestor(repository, merged, head))
                {
                    merged = head;
                    continue;
                }
                var timestamps = Value(repository.CommitterTimestamps([merged, head]));
                var timestamp = timestamps.Max();
                switch (Mutate("branch-merge", () => repository.MergeTrees(merged, head, record.Base.Commit)))
                {
                    case TreeMerge.Clean clean:
                        merged = Value(repository.CreateCommit(BranchRecipe(record, branch, clean.Tree, [merged, head], timestamp)));
                        break;
                    case TreeMerge.Conflicted conflict:
                        if (RunReducer.Resolution(record, merged, head) is { } resolved)
                        {
                            merged = resolved;
                            break;
                        }
                        ImmutableArray<string> paths = [.. conflict.Stages.Select(entry => entry.Path).Concat(conflict.Messages
                            .Where(message => message.Type.StartsWith("CONFLICT", StringComparison.Ordinal)).SelectMany(message => message.Paths))
                            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
                        var conflicted = Value(repository.CreateCommit(BranchRecipe(record, branch, conflict.Tree, [merged, head], timestamp) with
                        {
                            Message = $"Conflicting merge into {MergeMarkers.BranchName(branch)}\n\nIDP-Run: {record.Id.Value:D}\n",
                        }));
                        return new BranchMerge.Conflict(new(new MergeTarget.Branch(branch), merged, head, conflicted, paths,
                            MergeMarkers.DescribeBranch(branch, paths)));
                    case TreeMerge.Failed failed:
                        return new BranchMerge.Problem(failed.Detail);
                }
            }
            if (merged == tip) return new BranchMerge.Merged(tip);
            // The branch moves with the files of the folder that has it checked out, and only while that folder has no change
            // of the person's. The run's own data is no change of theirs, and Git refuses to overwrite it anyway.
            if (Value(repository.Worktrees()).FirstOrDefault(worktree => worktree.Branch == branch) is { } checkedOut)
            {
                ImmutableArray<string> changed = [.. Value(repository.TrackedChanges(checkedOut.Path))
                    .Where(path => path != ".idp" && !path.StartsWith(".idp/", StringComparison.Ordinal))];
                if (!changed.IsEmpty) return new BranchMerge.Uncommitted(checkedOut.Path, changed);
                var moved = Mutate("branch-fast-forward", () => repository.FastForward(checkedOut.Path, merged));
                if (moved.ExitCode != 0) return new BranchMerge.Problem(moved.Stderr.Trim());
            }
            else
            {
                switch (Mutate("branch-move", () => repository.MoveRef(new(branch, tip, merged))))
                {
                    case RefMove.Moved or RefMove.AlreadyAtTarget: break;
                    case RefMove.Conflict: return new BranchMerge.Problem($"The branch {MergeMarkers.BranchName(branch)} moved while the run's work merged into it.");
                    case RefMove.Failed failed: return new BranchMerge.Problem(failed.Detail);
                }
            }
            return new BranchMerge.Merged(merged);
        }
        catch (Refusal refused) { return new BranchMerge.Rejected(refused.Reason); }
        catch (MaterializationFailure failed) { return new BranchMerge.Problem(failed.Message); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return new BranchMerge.Problem(error.Message); }
    }

    /// <summary>
    /// The run's finished work: the commit of each task's current result that hands on code, without one that another of
    /// them already holds, in commit order so a repeat merges them the same way.
    /// </summary>
    private static ImmutableArray<CommitId> Heads(RunRecord record, GitRepository repository)
    {
        var stale = record.StaleResults;
        ImmutableArray<CommitId> commits = [.. record.CurrentResults.Values.Where(result => !stale.Contains(result.Id)).Select(result => result.Code switch
            {
                CodeOutput.Produced produced => produced.Code.Commit,
                CodeOutput.Forwarded forwarded => record.Inputs[forwarded.Inputs].CodeBase,
                _ => (CommitId?)null,
            }).OfType<CommitId>().Where(commit => commit != record.Base.Commit).Distinct().OrderBy(commit => commit.Hex, StringComparer.Ordinal)];
        return [.. commits.Where(commit => !commits.Any(other => other != commit && Ancestor(repository, commit, other)))];
    }

    private static bool Ancestor(GitRepository repository, CommitId ancestor, CommitId descendant) => repository.IsAncestor(ancestor, descendant) switch
    {
        GitAncestry.Yes => true,
        GitAncestry.No => false,
        GitAncestry.Failed failed => throw Fault(MaterializationProblem.GitFailed, failed.Detail, new BlockScope.Operation()),
        _ => throw new InvalidOperationException(),
    };

    private static CommitRecipe BranchRecipe(RunRecord record, string branch, TreeId tree, ImmutableArray<CommitId> parents, DateTimeOffset timestamp) =>
        new(tree, parents, $"Merge the run of {record.Revision.Snapshot.DisplayName} into {MergeMarkers.BranchName(branch)}\n\nIDP-Run: {record.Id.Value:D}\n",
            "iDevelop <idevelop@localhost>", "iDevelop <idevelop@localhost>", timestamp);
}
