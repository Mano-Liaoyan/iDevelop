using IDevelop.Workflows;

namespace IDevelop.Execution;

internal sealed partial class Materializer
{
    public ClaimCheck CheckInputsAndClaim(RunLease lease, OperationId operation, LaunchKey launch)
    {
        using var authority = lease.Use();
        if (authority is null) return new ClaimCheck.Rejected(new(RunProblem.TaskBusy));
        if (LeaseProblem(lease) is { } problem) return new ClaimCheck.Rejected(new(problem));
        var permit = lease.Permit;
        InputId? inputs = null;
        try
        {
            var repository = OpenRepository();
            using var mutation = repository.TakeMutationLock();
            if (mutation is null) return new ClaimCheck.Rejected(new(RunProblem.JournalBusy));
            var record = Read(permit.Workflow, permit.Run);
            if (!record.Preparations.TryGetValue(launch, out var prepared)) return new ClaimCheck.Rejected(new(RunProblem.InvalidClaim));
            var attempt = record.Attempts[launch.Attempt];
            if (LeaseProblem(lease, attempt.Task) is { } mismatch) return new ClaimCheck.Rejected(new(mismatch));
            inputs = prepared.Inputs;
            VerifyRepository(record, repository);
            var keepChanges = attempt.Cause is AttemptCause.Continue || launch.Turn > 1;
            VerifyCheckout(repository, prepared.Location, keepChanges, record);
            VerifyDelivery(record, prepared, repository);
            if (keepChanges && OwnBaselineHold(permit, operation, repository, record, prepared, attempt) is { } own) return own;
            if (launch.Turn == 1 && ProducerRecheck(permit, operation, repository, record, record.Inputs[prepared.Inputs]) is { } held) return held;
            return Journal("claim", () => _store.Claim(lease, OperationIds.Derive(operation, "claim"), launch,
                record.Inputs[prepared.Inputs], prepared.PromptHash)) switch
            {
                RunDecision.Granted granted => new ClaimCheck.Granted(granted.Claim),
                RunDecision.Existing existing => new ClaimCheck.Existing((RunEvent.TurnClaimed)existing.Event),
                _ => throw new InvalidOperationException(),
            };
        }
        catch (Refusal refused) { return new ClaimCheck.Rejected(refused.Reason); }
        catch (MaterializationFailure failed)
        { return ClaimBlock(new(operation, lease.Task, launch.Attempt, failed.Problem, inputs, [], failed.Message) { Scope = failed.Scope }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return ClaimBlock(new(operation, lease.Task, launch.Attempt, MaterializationProblem.InputUnavailable, inputs, [], error.Message) { Scope = new BlockScope.Operation() }); }

        ClaimCheck ClaimBlock(MaterializationBlock block) => Block(permit, operation, "claim", block) switch
        {
            Preparation.Blocked blocked => new ClaimCheck.Blocked(blocked.Block),
            Preparation.Rejected rejected => new ClaimCheck.Rejected(rejected.Reason),
            _ => throw new InvalidOperationException(),
        };
    }

    public ClaimCheck? ClaimedLaunch(CoordinatorPermit permit, OperationId operation, TaskId task, AttemptCause cause, string? basePrompt)
    {
        try
        {
            var record = Read(permit.Workflow, permit.Run);
            if (record.Receipts.GetValueOrDefault(OperationIds.Derive(operation, "prepared"))?.Event is not RunEvent.Prepared prepared ||
                !record.Claims.TryGetValue(prepared.Execution.Launch, out var claim)) return null;
            var attempt = record.Attempts[claim.Key.Attempt];
            if (attempt.Task != task || !RunReducer.Same(attempt.Cause, cause) ||
                Prompt(record.Revisions[attempt.Revision].Snapshot.Tasks[task], record.Inputs[prepared.Execution.Inputs],
                    attempt.Id, basePrompt) != prepared.Execution.Prompt)
                return new ClaimCheck.Rejected(new(RunProblem.OperationConflict));
            return new ClaimCheck.Existing(claim);
        }
        catch (Refusal refused) { return new ClaimCheck.Rejected(refused.Reason); }
    }

    public ClaimCheck? ClaimedLaunch(CoordinatorPermit permit, OperationId operation, LaunchKey launch, string prompt)
    {
        try
        {
            var record = Read(permit.Workflow, permit.Run);
            if (!record.Claims.TryGetValue(launch, out var claim)) return null;
            var attempt = record.Attempts[launch.Attempt];
            var prepared = record.Preparations[launch];
            var previous = record.Preparations[new(launch.Attempt, launch.Turn - 1)];
            var expected = prepared.Inputs == previous.Inputs ? prompt : Prompt(record.Revisions[attempt.Revision].Snapshot.Tasks[attempt.Task],
                record.Inputs[prepared.Inputs], attempt.Id, prompt);
            if (prepared.Prompt != expected) return new ClaimCheck.Rejected(new(RunProblem.OperationConflict));
            return new ClaimCheck.Existing(claim);
        }
        catch (Refusal refused) { return new ClaimCheck.Rejected(refused.Reason); }
    }
}
