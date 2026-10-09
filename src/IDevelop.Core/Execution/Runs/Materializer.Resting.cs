namespace IDevelop.Execution;

internal sealed partial class Materializer
{
    /// <summary>
    /// Before a resting attempt closes, its checkout must still match its folded baseline, and drift is recorded on the
    /// attempt. Mark done and a conclusion stop at an earlier drift block and at an unfinished Git step. A cancel accepts
    /// nothing, so it records drift and goes on, and a check that cannot run never stops it.
    /// </summary>
    public RestingCheck CheckRestingBaseline(RunLease lease, OperationId operation, AttemptId attempt, bool cancelling)
    {
        using var authority = lease.Use();
        if (authority is null) return new RestingCheck.Rejected(new(RunProblem.TaskBusy));
        if (LeaseProblem(lease) is { } problem) return new RestingCheck.Rejected(new(problem));
        var permit = lease.Permit;
        InputId? inputs = null;
        try
        {
            var repository = OpenRepository();
            using var mutation = repository.TakeMutationLock(MutationPatience);
            if (mutation is null) return new RestingCheck.Rejected(new(RunProblem.JournalBusy));
            var record = Read(permit.Workflow, permit.Run);
            if (!record.Attempts.TryGetValue(attempt, out var owner)) return new RestingCheck.Rejected(new(RunProblem.UnknownAttempt));
            if (LeaseProblem(lease, owner.Task) is { } mismatch) return new RestingCheck.Rejected(new(mismatch));
            if (!record.Preparations.TryGetValue(new(attempt, 1), out var prepared)) return new RestingCheck.Rejected(new(RunProblem.InvalidClaim));
            inputs = prepared.Inputs;
            if (RunReducer.AttemptDrift(record, id => id == attempt, operation) is { } existing)
                return cancelling ? new RestingCheck.Matched() : new RestingCheck.Drifted(existing.Value.Block);
            VerifyRepository(record, repository);
            if (VerifyOwnBaseline(repository, record, prepared.Location, attempt, refusePending: !cancelling) is { } pending)
                return new RestingCheck.Rejected(pending);
            ResolveClosureFaults(permit, operation, attempt);
            return new RestingCheck.Matched();
        }
        catch (Refusal refused) { return new RestingCheck.Rejected(refused.Reason); }
        catch (MaterializationFailure failed) when (IsCheckoutDrift(failed))
        { return RestingBlock(new(Recheck(operation), lease.Task, attempt, failed.Problem, inputs, [], failed.Message) { Scope = failed.Scope }); }
        catch (MaterializationFailure failed) when (!cancelling)
        { return RestingBlock(new(operation, lease.Task, attempt, failed.Problem, inputs, [], failed.Message) { Scope = new BlockScope.Operation() }); }
        catch (Exception error) when (!cancelling && error is IOException or UnauthorizedAccessException)
        { return RestingBlock(new(operation, lease.Task, attempt, MaterializationProblem.InputUnavailable, inputs, [], error.Message) { Scope = new BlockScope.Operation() }); }
        catch (Exception error) when (error is MaterializationFailure or IOException or UnauthorizedAccessException)
        { return new RestingCheck.Matched(); }

        RestingCheck RestingBlock(MaterializationBlock block) => Block(permit, operation, RunReducer.ClosureCheck, block) switch
        {
            Preparation.Blocked blocked => new RestingCheck.Drifted(blocked.Block),
            Preparation.Rejected rejected => new RestingCheck.Rejected(rejected.Reason),
            _ => throw new InvalidOperationException(),
        };
    }

    /// <summary>
    /// A successful baseline check of the attempt verified what its earlier failed closure checks could not, under whichever
    /// operation they ran, so it resolves their faults.
    /// </summary>
    private void ResolveClosureFaults(CoordinatorPermit permit, OperationId operation, AttemptId attempt)
    {
        var record = Read(permit.Workflow, permit.Run);
        foreach (var pair in record.Blocks.Where(pair => !pair.Value.Resolved && pair.Value.Block.Attempt == attempt &&
            RunReducer.ClosureFault(pair.Key, pair.Value.Block)).OrderBy(pair => record.Receipts[pair.Key].Sequence))
            Journal("recheck-resolve", () => _store.Record(permit, OperationIds.Derive(operation, "recheck-resolve-" + pair.Key.Value.ToString("D")),
                new RunEvent.BlockResolved(pair.Key, "Rechecked.")));
    }
}
