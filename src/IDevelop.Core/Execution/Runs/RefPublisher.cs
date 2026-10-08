namespace IDevelop.Execution;

internal abstract record RefPublication
{
    private RefPublication() { }
    internal sealed record Completed(GitObservation Observation) : RefPublication;
    internal sealed record Blocked(MaterializationProblem Problem, string Detail) : RefPublication;
    internal sealed record Rejected(RunRejection Reason) : RefPublication;
}

internal sealed class RefPublisher(RunStore store, Action<string>? probe = null)
{
    public RefPublication Publish(CoordinatorPermit permit, OperationId operation, OperationId plan, string step,
        GitRepository repository, RefChange change)
    {
        var workflow = permit.Workflow;
        var run = permit.Run;
        var read = store.Read(workflow, run);
        if (read is RunRead.Rejected rejected) return new RefPublication.Rejected(rejected.Reason);
        var record = ((RunRead.Loaded)read).Record;
        var live = repository.ReadRef(change.Ref);
        if (live is GitRead<CommitId?>.Failed failedRead)
            return new RefPublication.Blocked(failedRead.Problem, failedRead.Detail);
        var value = ((GitRead<CommitId?>.Read)live).Value;
        var mutation = new GitMutation.MoveRef(change);
        var prior = record.GitIntents.FirstOrDefault(pair => pair.Value.Plan == plan && RunReducer.Same(pair.Value.Mutation, mutation) &&
            record.GitObservations.ContainsKey(pair.Key));
        if (!RefOwnership.Accepts(record, repository, change.Ref, value, prior.Value is null ? change : null))
            return new RefPublication.Blocked(MaterializationProblem.UncertainOwnership,
                $"Publication ref {change.Ref} has unexpected value {value?.Hex ?? "absent"}.");
        if (prior.Value is not null) return new RefPublication.Completed(record.GitObservations[prior.Key]);
        var intended = OperationIds.Derive(operation, step + "-intent");
        var intent = Journal(step + "-intent", intended, new RunEvent.GitIntended(plan, mutation));
        if (intent is RunDecision.Rejected refused) return new RefPublication.Rejected(refused.Reason);
        probe?.Invoke("git." + step + ".before");
        var moved = repository.MoveRef(change);
        probe?.Invoke("git." + step + ".after");
        if (moved is RefMove.Conflict conflict)
            return new RefPublication.Blocked(MaterializationProblem.UncertainOwnership,
                $"Publication ref {change.Ref} has unexpected value {conflict.Observed?.Hex ?? "absent"}.");
        if (moved is RefMove.Failed failed) return new RefPublication.Blocked(MaterializationProblem.GitFailed, failed.Detail);
        var observation = new GitObservation(moved is RefMove.AlreadyAtTarget, change.Target.Hex);
        var observed = Journal(step + "-observed", OperationIds.Derive(operation, step + "-observed"),
            new RunEvent.GitObserved(intended, observation));
        return observed is RunDecision.Rejected refusal ? new RefPublication.Rejected(refusal.Reason) : new RefPublication.Completed(observation);

        RunDecision Journal(string label, OperationId id, RunEvent entry)
        {
            probe?.Invoke("journal." + label + ".before");
            var decision = store.Record(permit, id, entry);
            probe?.Invoke("journal." + label + ".after");
            return decision;
        }
    }
}
