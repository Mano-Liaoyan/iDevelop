using System.Collections.Immutable;

namespace IDevelop.Execution;

/// <summary>Where the person resolves a merge conflict that waits for them, once it is open.</summary>
internal abstract record ConflictCheckout
{
    private ConflictCheckout() { }

    /// <summary>The conflict's worktree at <paramref name="Folder"/>, with the files as the person left them.</summary>
    internal sealed record Ready(string Folder) : ConflictCheckout;

    /// <summary>Git could not add the worktree, for <paramref name="Detail"/>.</summary>
    internal sealed record Failed(string Detail) : ConflictCheckout;

    internal sealed record Rejected(RunRejection Reason) : ConflictCheckout;
}

/// <summary>What the person's Mark resolved of a merge conflict came to.</summary>
internal abstract record ConflictResolution
{
    private ConflictResolution() { }

    /// <summary>The person's files are committed as the merge, <paramref name="Merge"/>, on the conflict's two parents.</summary>
    internal sealed record Resolved(CommitId Merge) : ConflictResolution;

    /// <summary>The conflict has no worktree yet, so the person has resolved nothing.</summary>
    internal sealed record Unopened : ConflictResolution;

    /// <summary><paramref name="Paths"/> still hold the conflict markers, so nothing is committed.</summary>
    internal sealed record MarkersRemain(ImmutableArray<string> Paths) : ConflictResolution;

    /// <summary>Git could not commit the person's files, for <paramref name="Detail"/>.</summary>
    internal sealed record Failed(string Detail) : ConflictResolution;

    internal sealed record Rejected(RunRejection Reason) : ConflictResolution;
}

internal sealed partial class Materializer
{
    /// <summary>
    /// The worktree where the person resolves <paramref name="conflict"/>, which no Merge node takes (#148). The first call adds
    /// it at <see cref="RunLayout.ConflictCheckout"/>, detached at the commit with the conflict markers; a repeat finds it with
    /// the person's files as they are.
    /// </summary>
    public ConflictCheckout OpenConflict(CoordinatorPermit permit, MergeConflict conflict)
    {
        try
        {
            var record = Read(permit.Workflow, permit.Run);
            if (RunReducer.ConflictResolutionProblem(record, conflict) is { } problem) return new ConflictCheckout.Rejected(new(problem));
            if (record.RunKey is not { } run) return new ConflictCheckout.Rejected(new(RunProblem.InvalidData));
            var repository = OpenRepository();
            using var held = MutationLock(repository);
            if (held is null) return new ConflictCheckout.Rejected(new(RunProblem.JournalBusy));
            VerifyRepository(record, repository);
            var relative = RunLayout.ConflictCheckout(run, conflict.Conflicted);
            var checkout = RunStorage.SafePath(repository.ProjectFolder, relative);
            if (Registered(repository, checkout)) return new ConflictCheckout.Ready(checkout);
            if (Directory.Exists(checkout) && Directory.EnumerateFileSystemEntries(checkout).Any())
                return new ConflictCheckout.Failed($"The folder {checkout} is already in use.");
            var added = Mutate("conflict-worktree", () => repository.AddDetachedWorktree(relative, conflict.Conflicted));
            if (added.ExitCode != 0) return new ConflictCheckout.Failed(added.Stderr.Trim());
            var locked = Mutate("conflict-lock", () => repository.LockWorktree(relative, $"idevelop {run}/conflict"));
            if (locked.ExitCode != 0) return new ConflictCheckout.Failed(locked.Stderr.Trim());
            return new ConflictCheckout.Ready(checkout);
        }
        catch (Refusal refused) { return new ConflictCheckout.Rejected(refused.Reason); }
        catch (MaterializationFailure failed) { return new ConflictCheckout.Failed(failed.Message); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return new ConflictCheckout.Failed(error.Message); }
    }

    /// <summary>
    /// Commits the person's files in <paramref name="conflict"/>'s worktree as its merge, on the conflict's two parents, and
    /// records it under <paramref name="operation"/>, so the run goes on as it does after a Merge node's merge (#148). Refused
    /// while a conflicting file still holds the conflict markers, and before the worktree exists.
    /// </summary>
    public ConflictResolution ResolveConflict(CoordinatorPermit permit, OperationId operation, MergeConflict conflict)
    {
        try
        {
            var record = Read(permit.Workflow, permit.Run);
            if (record.Receipts.GetValueOrDefault(operation)?.Event is RunEvent.ConflictResolved recorded && recorded.Conflict.Equals(conflict))
                return new ConflictResolution.Resolved(recorded.Merge);
            if (RunReducer.ConflictResolutionProblem(record, conflict) is { } problem) return new ConflictResolution.Rejected(new(problem));
            if (record.RunKey is not { } run) return new ConflictResolution.Rejected(new(RunProblem.InvalidData));
            var repository = OpenRepository();
            using var held = MutationLock(repository);
            if (held is null) return new ConflictResolution.Rejected(new(RunProblem.JournalBusy));
            VerifyRepository(record, repository);
            var checkout = RunStorage.SafePath(repository.ProjectFolder, RunLayout.ConflictCheckout(run, conflict.Conflicted));
            if (!Registered(repository, checkout)) return new ConflictResolution.Unopened();
            var remaining = MergeMarkers.Remaining(checkout, conflict.Paths);
            if (!remaining.IsEmpty) return new ConflictResolution.MarkersRemain(remaining);
            var tree = Value(repository.Capture(checkout)).Tree;
            var timestamp = Value(repository.CommitterTimestamps([conflict.Ours, conflict.Theirs])).Max();
            var merge = Value(repository.CreateCommit(new(tree, [conflict.Ours, conflict.Theirs],
                $"Resolve the conflicting merge\n\nIDP-Run: {record.Id.Value:D}\n", "iDevelop <idevelop@localhost>", "iDevelop <idevelop@localhost>", timestamp)));
            Pin(repository, RunLayout.ConflictPin(run, conflict.Conflicted), merge);
            Journal("conflict-resolved", () => _store.Record(permit, operation, new RunEvent.ConflictResolved(conflict, merge)));
            return new ConflictResolution.Resolved(merge);
        }
        catch (Refusal refused) { return new ConflictResolution.Rejected(refused.Reason); }
        catch (MaterializationFailure failed) { return new ConflictResolution.Failed(failed.Message); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return new ConflictResolution.Failed(error.Message); }
    }

    private static bool Registered(GitRepository repository, string checkout) =>
        Value(repository.Worktrees()).Any(worktree => SamePath(worktree.Path, checkout));
}
