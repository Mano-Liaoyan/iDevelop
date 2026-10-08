namespace IDevelop.Execution;

internal abstract record PinRelease
{
    private PinRelease() { }

    internal sealed record Released(int Count) : PinRelease;

    internal sealed record Rejected(RunRejection Reason) : PinRelease;

    internal sealed record Failed(MaterializationProblem Problem, string Detail) : PinRelease;
}

internal sealed partial class Materializer
{
    public PinRelease ReleasePins(CoordinatorPermit permit, OperationId operation)
    {
        using var authority = permit.Use();
        if (authority is null) return new PinRelease.Rejected(new(RunProblem.RunBusy));
        if (!SamePath(permit.Project, _project)) return new PinRelease.Rejected(new(RunProblem.IdentityMismatch));
        if (operation.Value == Guid.Empty) return new PinRelease.Rejected(new(RunProblem.InvalidData));
        try
        {
            var record = Read(permit.Workflow, permit.Run);
            if (!record.Receipts.Values.Any(entry => entry.Event is RunEvent.Settled))
                return new PinRelease.Rejected(new(RunProblem.NotSettled));
            if (record.RunKey is null) return new PinRelease.Released(0);
            var repository = OpenRepository();
            VerifyRepository(record, repository);
            var pins = Value(repository.RefSnapshot(RunLayout.PinPrefix(record.RunKey)));
            foreach (var (name, tip) in pins)
            {
                var deleted = Mutate("release-pin", () => repository.DeleteRef(name, tip));
                if (deleted.ExitCode != 0) throw Fault(MaterializationProblem.GitFailed, deleted.Stderr);
            }
            return new PinRelease.Released(pins.Count);
        }
        catch (Refusal refused) { return new PinRelease.Rejected(refused.Reason); }
        catch (MaterializationFailure failed) { return new PinRelease.Failed(failed.Problem, failed.Message); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new PinRelease.Rejected(new(RunProblem.StorageUnavailable)); }
    }

    private void Pin(GitRepository repository, string name, CommitId tip)
    {
        var current = Value(repository.ReadRef(name));
        var moved = Mutate("pin", () => repository.MoveRef(new(name, current, tip)));
        if (moved is RefMove.Failed failed) throw Fault(MaterializationProblem.GitFailed, failed.Detail);
        if (moved is RefMove.Conflict) throw Fault(MaterializationProblem.UncertainOwnership, "The retention pin changed while it was being updated.");
    }
}
