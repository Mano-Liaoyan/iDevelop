namespace IDevelop.Execution;

internal abstract record RootObservation
{
    private RootObservation() { }

    internal sealed record Observed(RunEvent.RootExitObserved Observation) : RootObservation;

    internal sealed record Fenced(LaunchKey Launch, string Detail) : RootObservation;

    internal sealed record Rejected(RunRejection Reason) : RootObservation;
}

internal sealed partial class Materializer
{
    public RootObservation ObserveRootExit(RunLease lease, OperationId operation, LaunchKey launch, RootExit exit)
    {
        using var authority = lease.Use();
        if (authority is null) return new RootObservation.Rejected(new(RunProblem.TaskBusy));
        if (LeaseProblem(lease) is { } problem) return new RootObservation.Rejected(new(problem));
        var permit = lease.Permit;
        RunRecord record;
        try { record = Read(permit.Workflow, permit.Run); }
        catch (Refusal refused) { return new RootObservation.Rejected(refused.Reason); }
        if (!record.Claims.ContainsKey(launch)) return new RootObservation.Rejected(new(RunProblem.InvalidClaim));
        if (LeaseProblem(lease, record.Attempts[launch.Attempt].Task) is { } mismatch)
            return new RootObservation.Rejected(new(mismatch));
        if (record.Fenced.Contains(launch)) return new RootObservation.Rejected(new(RunProblem.UnresolvedOwnership));
        if (record.RootExits.TryGetValue(launch, out var existing)) return new RootObservation.Observed(existing);
        string detail;
        try
        {
            var repository = OpenRepository();
            var location = record.Preparations[launch].Location;
            var tip = Value(repository.ReadRef(location.Owner.Branch)) ??
                throw Fault(MaterializationProblem.UncertainOwnership, "The task branch is absent.");
            var head = Value(repository.SymbolicHead(Checkout(repository, location.Owner)));
            var ownership = RefOwnership.Accepts(record, repository, location.Owner.Branch, tip)
                ? TipOwnership.Explained : TipOwnership.Unexplained;
            var observation = new RunEvent.RootExitObserved(launch, exit, _clock.GetUtcNow(), tip, head, ownership);
            var decision = Journal("root-exit", () => _store.Record(permit, OperationIds.Derive(operation, "root-exit"), observation));
            return new RootObservation.Observed((RunEvent.RootExitObserved)DecisionEvent(decision));
        }
        catch (Refusal refused) { detail = refused.Reason.Problem.ToString(); }
        catch (MaterializationFailure failed) { detail = failed.Message; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { detail = error.Message; }
        try
        {
            Journal("root-exit-fenced", () => _store.Record(permit, OperationIds.Derive(operation, "root-exit-fenced"),
                new RunEvent.OwnershipFenced([launch])));
            return new RootObservation.Fenced(launch, detail);
        }
        catch (Refusal refused) { return new RootObservation.Rejected(refused.Reason); }
    }
}
