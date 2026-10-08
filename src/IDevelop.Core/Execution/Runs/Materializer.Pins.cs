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
            var recoveredAbandonment = record.Phase == RunPhase.Abandoned &&
                record.Attempts.Keys.All(record.Closures.ContainsKey) &&
                record.Plans.Where(pair => pair.Value is MaterializationPlan.Salvage).All(pair => record.Salvages.ContainsKey(pair.Key)) &&
                record.Blocks.Values.All(block => block.Resolved);
            if (!record.Receipts.Values.Any(entry => entry.Event is RunEvent.Settled) && !recoveredAbandonment)
                return new PinRelease.Rejected(new(RunProblem.NotSettled));
            if (record.RunKey is null) return new PinRelease.Released(0);
            var repository = OpenRepository();
            VerifyRepository(record, repository);
            var pins = Value(repository.RefSnapshot(RunLayout.PinPrefix(record.RunKey)));
            var retained = record.Plans.Where(pair => pair.Value is MaterializationPlan.Preservation && !record.Preservations.ContainsKey(pair.Key) ||
                pair.Value is MaterializationPlan.Salvage && !record.Salvages.ContainsKey(pair.Key)).Select(pair => pair.Value switch
                {
                    MaterializationPlan.Preservation plan => (plan.Task, plan.Attempt),
                    MaterializationPlan.Salvage plan => (plan.Task, plan.Attempt),
                    _ => throw new InvalidOperationException(),
                }).Distinct().Select(pair => $"{RunLayout.PinPrefix(record.RunKey)}{record.TaskKeys[pair.Task]}/{pair.Attempt.Value:D}/preserve/")
                .ToArray();
            var count = 0;
            foreach (var (name, tip) in pins.Where(pair => !retained.Any(prefix => pair.Key.StartsWith(prefix, StringComparison.Ordinal))))
            {
                var deleted = Mutate("release-pin", () => repository.DeleteRef(name, tip));
                if (deleted.ExitCode != 0) throw Fault(MaterializationProblem.GitFailed, deleted.Stderr, new BlockScope.Refs([name]));
                count++;
            }
            return new PinRelease.Released(count);
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
        if (moved is RefMove.Failed failed) throw Fault(MaterializationProblem.GitFailed, failed.Detail, new BlockScope.Refs([name]));
        if (moved is RefMove.Conflict) throw Fault(MaterializationProblem.UncertainOwnership, "The retention pin changed while it was being updated.", new BlockScope.Refs([name]));
    }
}
