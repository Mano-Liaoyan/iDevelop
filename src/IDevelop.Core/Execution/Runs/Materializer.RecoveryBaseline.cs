namespace IDevelop.Execution;

internal sealed partial class Materializer
{
    public RecoveryBaselining RecordRecoveryBaseline(RunLease lease, OperationId operation, AttemptId previous,
        OperationId confirmation, OperationId preservation)
    {
        using var authority = lease.Use();
        if (authority is null) return new RecoveryBaselining.Rejected(new(RunProblem.TaskBusy));
        if (LeaseProblem(lease) is { } problem) return new RecoveryBaselining.Rejected(new(problem));
        var permit = lease.Permit;
        var step = "baseline-preconditions";
        InputId? inputs = null;
        try
        {
            if (confirmation.Value == Guid.Empty) return new RecoveryBaselining.Rejected(new(RunProblem.ConfirmationRequired));
            var record = Read(permit.Workflow, permit.Run);
            if (!record.Attempts.TryGetValue(previous, out var attempt)) return new RecoveryBaselining.Rejected(new(RunProblem.UnknownAttempt));
            if (LeaseProblem(lease, attempt.Task) is { } mismatch) return new RecoveryBaselining.Rejected(new(mismatch));
            if (!record.Closures.TryGetValue(previous, out var end)) return new RecoveryBaselining.Rejected(new(RunProblem.UnclosedAttempts));
            if (end is AttemptEnd.Recovered { Outcome: RecoveryOutcome.NotStarted })
                return new RecoveryBaselining.Rejected(new(RunProblem.OutcomeMismatch));
            if (record.Baselines.TryGetValue((previous, confirmation), out var receipt)) return new RecoveryBaselining.Recorded(receipt);
            var revision = RunReducer.Slot(record, attempt.Task, new AttemptCause.Continue(previous, confirmation))?.Revision ?? record.Revision.Id;
            var log = AttemptEvidence.Read(_store.AttemptFolder(permit.Workflow, permit.Run, attempt.Task, previous)).Record;
            if (log?.SessionId is not { Length: > 0 } session ||
                log.Requested.Client != record.Revisions[revision].Snapshot.Tasks[attempt.Task].Execution?.Client)
                return new RecoveryBaselining.Rejected(new(RunProblem.SessionUnavailable));
            var preservationId = OperationIds.Derive(preservation, "preserve-plan");
            if (!record.Preservations.ContainsKey(preservationId) ||
                record.Plans.GetValueOrDefault(preservationId) is not MaterializationPlan.Preservation plan ||
                !record.Preparations.TryGetValue(new(previous, 1), out var prepared) ||
                !record.Preparations.TryGetValue(new(plan.Attempt, 1), out var preservedPreparation) ||
                preservedPreparation.Location.Owner != prepared.Location.Owner)
                return new RecoveryBaselining.Rejected(new(RunProblem.RecoveryEvidenceInsufficient));
            inputs = prepared.Inputs;
            var repository = OpenRepository();
            VerifyRepository(record, repository);
            VerifyOwnedCheckout(repository, prepared.Location, record);
            using var mutation = repository.TakeMutationLock();
            if (mutation is null) return new RecoveryBaselining.Rejected(new(RunProblem.JournalBusy));
            record = Read(permit.Workflow, permit.Run);
            if (record.Baselines.TryGetValue((previous, confirmation), out receipt)) return new RecoveryBaselining.Recorded(receipt);
            if (plan.Preserved.IndexLock is not null)
                throw Fault(MaterializationProblem.DirtyWorktree, "Remove index.lock with Restore first.", new([], [], true));
            var fold = CheckoutBaseline.Fold(record, prepared.Location.Owner, commit => Value(repository.ReadCommit(commit)).Tree);
            var unfinished = fold.Components.Values.Select(component => component switch
            {
                ComponentBaseline.Pending pending => (OperationId?)pending.Owner,
                ComponentBaseline.Unfinished pending => pending.Owner,
                _ => null,
            }).FirstOrDefault(owner => owner is not null);
            if (unfinished is { } owner)
                throw Fault(MaterializationProblem.UncertainOwnership, UnfinishedStepDetail(record, owner), new([], [], false));
            step = "baseline-observe";
            var current = Mutate(step, () => ReadRecoveryCheckout(repository, record, prepared.Location, previous));
            if (CheckoutDifference(repository, prepared.Location.Owner, plan.Preserved, current) is { } difference)
                throw Fault(MaterializationProblem.DirtyWorktree, PreservationChanged, difference);
            step = "baseline";
            var baselineId = OperationIds.Derive(operation, "baseline");
            receipt = (RunEvent.RecoveryBaselined)DecisionEvent(Journal(step, () => _store.Record(permit, baselineId,
                new RunEvent.RecoveryBaselined(new(previous, confirmation, session, preservation)))));
            var driftId = OperationIds.Derive(preservation, "preserve-drift");
            if (record.Blocks.TryGetValue(driftId, out var block) && !block.Resolved)
            {
                step = "baseline-resolve-" + driftId.Value.ToString("D");
                Journal(step, () => _store.Record(permit, OperationIds.Derive(baselineId, step),
                    new RunEvent.BlockResolved(driftId, $"Adopted as the recovery baseline after confirmation {confirmation.Value:D}")));
            }
            return new RecoveryBaselining.Recorded(receipt);
        }
        catch (Refusal refused) { return new RecoveryBaselining.Rejected(refused.Reason); }
        catch (MaterializationFailure failed)
        { return RecoveryBaselineBlock(permit, operation, step, new(operation, lease.Task, previous, failed.Problem, inputs, [], failed.Message) { Scope = failed.Scope }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return RecoveryBaselineBlock(permit, operation, step, new(operation, lease.Task, previous, MaterializationProblem.InputUnavailable, inputs, [], error.Message)); }
    }

    private static CheckoutState ReadRecoveryCheckout(GitRepository repository, RunRecord record, ExecutionLocation location, AttemptId previous) =>
        ReadPreservationCheckout(repository, record, location, previous,
            (name, bytes) => new(name, Revision.Hash(bytes), bytes.LongLength)).State;

    private static void VerifyRecoveryBaseline(GitRepository repository, RunRecord record, ExecutionLocation location, AttemptCause cause)
    {
        if (cause is not AttemptCause.Continue continued ||
            !record.Baselines.TryGetValue((continued.Previous, continued.Confirmation), out var receipt)) return;
        var plan = (MaterializationPlan.Preservation)record.Plans[OperationIds.Derive(receipt.Baseline.Preservation, "preserve-plan")];
        var current = ReadRecoveryCheckout(repository, record, location, continued.Previous);
        if (CheckoutDifference(repository, location.Owner, plan.Preserved, current) is { } difference)
            throw Fault(difference.Refs.IsEmpty ? MaterializationProblem.DirtyWorktree : MaterializationProblem.UncertainOwnership,
                PreservationChanged, difference);
    }

    private RecoveryBaselining RecoveryBaselineBlock(CoordinatorPermit permit, OperationId operation, string step, MaterializationBlock block) =>
        Block(permit, operation, step, block) switch
        {
            Preparation.Blocked blocked => new RecoveryBaselining.Blocked(blocked.Block),
            Preparation.Rejected rejected => new RecoveryBaselining.Rejected(rejected.Reason),
            _ => throw new InvalidOperationException(),
        };

    private static string UnfinishedStepDetail(RunRecord record, OperationId owner)
    {
        var kind = record.Plans[owner] switch
        {
            MaterializationPlan.RetryReset => "Retry reset",
            MaterializationPlan.Refresh => "Refresh",
            MaterializationPlan.Publication => "Publication",
            _ => "Preparation",
        };
        return $"{kind} {owner.Value:D} has an unfinished Git step on this checkout. Run it again, or salvage and retry.";
    }
}
