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
            using var mutation = MutationLock(repository);
            if (mutation is null) return new ClaimCheck.Rejected(new(RunProblem.JournalBusy));
            var record = Read(permit.Workflow, permit.Run);
            if (!record.Preparations.TryGetValue(launch, out var prepared)) return new ClaimCheck.Rejected(new(RunProblem.InvalidClaim));
            var attempt = record.Attempts[launch.Attempt];
            if (LeaseProblem(lease, attempt.Task) is { } mismatch) return new ClaimCheck.Rejected(new(mismatch));
            inputs = prepared.Inputs;
            VerifyRepository(record, repository);
            VerifyWorktree(repository, prepared.Location, record);
            // A turn that keeps the attempt's changes waits on an earlier block on its own checkout.
            if ((attempt.Cause is AttemptCause.Continue || launch.Turn > 1) && RunReducer.AttemptDrift(record, id => id == attempt.Id, operation) is { } held)
                return new ClaimCheck.Blocked(held.Value.Block);
            if (launch.Turn == 1 && ProducerStepPending(record, record.Inputs[prepared.Inputs]) is { } pending) return new ClaimCheck.Rejected(pending);
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

    /// <summary>
    /// A consumer's first claim waits while a Git step on the checkout of a task whose code it receives, such as a
    /// publication's branch move, is unfinished, so it never starts from code that is about to change.
    /// </summary>
    private static RunRejection? ProducerStepPending(RunRecord record, InputRecord inputs)
    {
        foreach (var owner in RunReducer.CodeOwners(inputs))
        {
            // A result carried from an earlier run has no checkout in this run, only its kept commit (#90).
            if (record.CurrentResults.GetValueOrDefault(owner) is { Origin: ResultOrigin.Carried }) continue;
            if (RunReducer.PublishingAttempt(record, owner) is not { } published || !record.Preparations.TryGetValue(new(published, 1), out var prepared) ||
                UnfinishedGitStep(record, prepared.Location.Owner))
                return new(RunProblem.UnresolvedOwnership);
        }
        return null;
    }

    /// <summary>Whether a Git step on <paramref name="owner"/>'s checkout or branch was intended and never observed.</summary>
    private static bool UnfinishedGitStep(RunRecord record, WorktreeOwner owner) =>
        record.GitIntents.Any(pair => !record.GitObservations.ContainsKey(pair.Key) && pair.Value.Mutation switch
        {
            GitMutation.CreateWorktree create => create.Owner == owner,
            GitMutation.MoveRef move => move.Change.Ref == owner.Branch,
            GitMutation.ResetCheckout reset => reset.Task == owner.Task,
            GitMutation.AlignIndex align => align.Task == owner.Task,
            GitMutation.RemovePaths remove => remove.Task == owner.Task,
            _ => false,
        });

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
                    attempt.Id, basePrompt, RunPlanning.Context(record, attempt), RunReducer.FixArtifacts(record, attempt.Id)) != prepared.Execution.Prompt)
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
