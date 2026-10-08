using System.Collections.Immutable;
using System.Text;

namespace IDevelop.Execution;

internal abstract record RootObservation
{
    private RootObservation() { }

    internal sealed record Observed(RunEvent.RootExitObserved Observation) : RootObservation;

    internal sealed record Fenced(LaunchKey Launch, string Detail) : RootObservation;

    internal sealed record Rejected(RunRejection Reason) : RootObservation;
}

internal abstract record Settlement
{
    private Settlement() { }

    internal sealed record Closed(CaptureId Capture, CaptureDisposition Disposition) : Settlement;

    internal sealed record Rejected(RunRejection Reason) : Settlement;
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
        string detail;
        try
        {
            if (record.RootExits.TryGetValue(launch, out var existing))
            {
                if (existing.Exit == exit) return new RootObservation.Observed(existing);
                throw new Refusal(new(RunProblem.InvalidClaim));
            }
            var repository = OpenRepository();
            var location = record.Preparations[launch].Location;
            var tip = Value(repository.ReadRef(location.Owner.Branch)) ??
                throw Fault(MaterializationProblem.UncertainOwnership, "The task branch is absent.");
            var head = Value(repository.SymbolicHead(Checkout(repository, location.Owner)));
            var ownership = RefOwnership.Accepts(record, repository, location.Owner.Branch, tip)
                ? TipOwnership.Explained : TipOwnership.Unexplained;
            var pin = RunLayout.RootPin(record.RunKey!, record.TaskKeys[location.Owner.Task], launch);
            Pin(repository, pin, tip);
            var observation = new RunEvent.RootExitObserved(launch, exit, _clock.GetUtcNow(), tip, head, ownership);
            RunDecision decision;
            try { decision = Journal("root-exit", () => _store.Record(permit, OperationIds.Derive(operation, "root-exit"), observation)); }
            catch (Refusal refused)
            {
                if (Read(permit.Workflow, permit.Run).RootExits.TryGetValue(launch, out var recorded))
                {
                    if (recorded.Exit == observation.Exit && recorded.Tip == observation.Tip &&
                        recorded.Head == observation.Head && recorded.Ownership == observation.Ownership)
                        return new RootObservation.Observed(recorded);
                    throw;
                }
                if (refused.Reason.Problem == RunProblem.JournalBusy) return new RootObservation.Rejected(refused.Reason);
                throw;
            }
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

    public async ValueTask<Settlement> Settle(RunLease lease, OperationId operation, LaunchKey launch, LogCheckpoint log,
        CancellationToken cancellation = default)
    {
        using var authority = lease.Use();
        if (authority is null) return new Settlement.Rejected(new(RunProblem.TaskBusy));
        if (LeaseProblem(lease) is { } problem) return new Settlement.Rejected(new(problem));
        var permit = lease.Permit;
        var capture = new CaptureId(OperationIds.Derive(operation, "capture").Value);
        try
        {
            var record = Read(permit.Workflow, permit.Run);
            if (!record.Claims.ContainsKey(launch)) return new Settlement.Rejected(new(RunProblem.InvalidClaim));
            if (LeaseProblem(lease, record.Attempts[launch.Attempt].Task) is { } mismatch)
                return new Settlement.Rejected(new(mismatch));
            if (record.Fenced.Contains(launch) || !record.RootExits.ContainsKey(launch))
                return new Settlement.Rejected(new(RunProblem.UnresolvedOwnership));
            if (record.TurnClosures.ContainsKey(launch))
                return record.Settlements.GetValueOrDefault(launch) == capture
                    ? new Settlement.Closed(capture, record.Dispositions[capture].Disposition)
                    : new Settlement.Rejected(new(RunProblem.EvidenceMismatch));
            if (RunReducer.CaptureForAnotherId(record, launch, capture))
                return new Settlement.Rejected(new(RunProblem.OperationConflict));
            var observations = record.Captures.GetValueOrDefault(capture, []);
            if (observations.Any(observation => observation.Launch != launch || observation.Log != log))
                return new Settlement.Rejected(new(RunProblem.EvidenceMismatch));
            if (!record.Dispositions.TryGetValue(capture, out var disposed))
            {
                if (observations.Count == 1) return new Settlement.Rejected(new(RunProblem.RecoveryEvidenceInsufficient));
                CaptureDisposition? disposition = null;
                GitRepository? repository = null;
                if (observations.Count == 0)
                {
                    if (_store.TurnEvidenceProblem(record, launch, log) is { } rejection)
                        return new Settlement.Rejected(rejection);
                    for (var ordinal = 1; ordinal <= 2; ordinal++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        ImmutableArray<EvidenceFile> evidence = [];
                        CaptureObservation observation;
                        try
                        {
                            var storage = new RunStorage(_project, permit.Workflow, permit.Run);
                            var folder = RunStorage.SafePath(storage.Folder, $"captures/{capture.Value:D}/{ordinal}");
                            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                            repository ??= OpenRepository();
                            record = Read(permit.Workflow, permit.Run);
                            observation = Mutate("capture-" + ordinal, () => ObserveCapture(repository, record, operation,
                                capture, ordinal, launch, log, ref evidence));
                        }
                        catch (MaterializationFailure failed)
                        {
                            disposition = new CaptureDisposition.Failed(failed.Problem, failed.Message, evidence);
                            break;
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                        {
                            disposition = new CaptureDisposition.Failed(MaterializationProblem.InputUnavailable, error.Message, evidence);
                            break;
                        }
                        var number = ordinal;
                        Task? interval = null;
                        record = DecisionRecord(Journal("capture-" + ordinal, () =>
                        {
                            var decision = _store.Record(permit, OperationIds.Derive(operation, "capture-" + number),
                                new RunEvent.TurnCaptured(observation));
                            if (number == 1 && decision is RunDecision.Recorded)
                                interval = Task.Delay(TimeSpan.FromMilliseconds(250), _clock, cancellation);
                            return decision;
                        }));
                        if (interval is not null) await interval;
                    }
                    observations = record.Captures.GetValueOrDefault(capture, []);
                }
                disposition ??= DisposeCapture(record, observations[0], observations[1]);
                disposed = new(capture, launch, disposition);
                var decision = Journal("capture-disposition", () => _store.Record(permit,
                    OperationIds.Derive(operation, "capture-disposition"), disposed));
                disposed = (RunEvent.CaptureDisposed)DecisionEvent(decision);
            }
            if (disposed.Launch != launch) return new Settlement.Rejected(new(RunProblem.EvidenceMismatch));
            Journal("close-turn", () => _store.CloseTurn(permit, OperationIds.Derive(operation, "close-turn"), launch, log, capture));
            return new Settlement.Closed(capture, disposed.Disposition);
        }
        catch (Refusal refused) { return new Settlement.Rejected(refused.Reason); }
    }

    private CaptureObservation ObserveCapture(GitRepository repository, RunRecord record, OperationId operation, CaptureId id,
        int ordinal, LaunchKey launch, LogCheckpoint log, ref ImmutableArray<EvidenceFile> evidence)
    {
        var started = _clock.GetUtcNow();
        var prepared = record.Preparations[launch];
        try { VerifyOwnedCheckout(repository, prepared.Location, record); }
        catch (MaterializationFailure failed) { throw Fault(MaterializationProblem.UncertainOwnership, failed.Message); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw Fault(MaterializationProblem.UncertainOwnership, error.Message); }
        var checkout = Checkout(repository, prepared.Location.Owner);
        if (!Value(repository.UnmergedEntries(checkout)).IsEmpty)
            throw Fault(MaterializationProblem.DirtyWorktree, "The writer index has unresolved stages.");
        var tracked = Value(repository.TrackedFiles(checkout, ".idp/inputs", ".idp/outbox", ".worktrees"));
        if (!tracked.IsEmpty) throw Fault(MaterializationProblem.DirtyWorktree, "Tracked execution data: " + string.Join(", ", tracked));
        var tip = Value(repository.ReadRef(prepared.Location.Owner.Branch));
        var head = Value(repository.SymbolicHead(checkout));
        var storage = new RunStorage(_project, record.Workflow, record.Id);
        EvidenceFile? index = null;
        if (Value(repository.IndexBytes(checkout)) is { } bytes)
        {
            var relative = RunStorage.CapturePath(id, ordinal, "index");
            index = new(relative, Revision.Hash(bytes), bytes.LongLength);
            RunStorage.Publish(storage.Folder, relative, bytes, index.Content, index.ByteLength);
        }
        var captured = Value(repository.Capture(checkout));
        if (captured.IndexBefore != captured.IndexAfter || captured.IndexBefore != index?.Content)
            throw Fault(MaterializationProblem.DirtyWorktree, "The writer index changed during capture.");
        var task = record.Attempts[launch.Attempt].Task;
        var logged = AttemptEvidence.Read(_store.AttemptFolder(record.Workflow, record.Id, task, launch.Attempt), log);
        if (logged.Rejection is { } rejection) throw Fault(MaterializationProblem.InputUnavailable, rejection.Problem.ToString());
        var artifacts = prepared.OutboxPath.Length == 0 ? [] : FreezeOutbox(record.Workflow, record.Id,
            OperationIds.Derive(operation, "capture-" + ordinal), launch.Attempt,
            RunStorage.CapturePath(id, ordinal, "artifacts"), checkout, ref evidence);
        var unexplained = UnexplainedPublicationRefs(record, repository, prepared, out _, out _, out var sharedRefs);
        var refBytes = Encoding.UTF8.GetBytes(RunJournal.Canonical(sharedRefs));
        var refs = new EvidenceFile(RunStorage.CapturePath(id, ordinal, "refs.json"), Revision.Hash(refBytes), refBytes.LongLength);
        RunStorage.Publish(storage.Folder, refs.RelativePath, refBytes, refs.Content, refs.ByteLength);
        evidence = evidence.Add(refs);
        var root = record.RootExits[launch];
        var definition = record.Revisions[record.Attempts[launch.Attempt].Revision].Snapshot.Tasks[task];
        var recipe = new CommitRecipe(captured.Tree, [root.Tip],
            $"{definition.Title}\n\nIDP-Run: {record.Id.Value:D}\nIDP-Task: {task.Value:D}\nIDP-Attempt: {launch.Attempt.Value:D}\n",
            "iDevelop <idevelop@localhost>", "iDevelop <idevelop@localhost>", DateTimeOffset.FromUnixTimeSeconds(root.At.ToUnixTimeSeconds()));
        var candidate = Value(repository.CreateCommit(recipe));
        Pin(repository, RunLayout.CapturePin(record.RunKey!, record.TaskKeys[task], launch, ordinal), candidate);
        return new(id, ordinal, launch, log, started, _clock.GetUtcNow(), recipe, candidate, tip, head, index,
            logged.Record!.Result, artifacts, refs, unexplained);
    }

    private CaptureDisposition DisposeCapture(RunRecord record, CaptureObservation first, CaptureObservation second)
    {
        var root = record.RootExits[first.Launch];
        ImmutableArray<string> refs = [];
        if (first.Tip != root.Tip || second.Tip != root.Tip)
            refs = refs.Add(record.Preparations[first.Launch].Location.Owner.Branch);
        if (first.Head != root.Head || second.Head != root.Head) refs = refs.Add("HEAD");
        if (!refs.IsEmpty) return new CaptureDisposition.Diverged(MaterializationProblem.UncertainOwnership, [],
            [.. refs.Order(StringComparer.Ordinal)], "The branch tip or HEAD differs from the root observation.");
        refs = [.. first.UnexplainedRefs.Union(second.UnexplainedRefs, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        if (!refs.IsEmpty) return new CaptureDisposition.Diverged(MaterializationProblem.UncertainOwnership, [], refs,
            "The capture contains unexplained shared refs.");
        if (!CaptureComparison.Content(first, second))
        {
            var differences = new List<string>();
            if (!RunReducer.Same(first.Recipe, second.Recipe)) differences.Add("recipe");
            if (first.Candidate != second.Candidate) differences.Add("candidate");
            if (!CaptureComparison.Index(first, second)) differences.Add("index");
            if (first.Report != second.Report) differences.Add("report");
            if (!CaptureComparison.Artifacts(first, second)) differences.Add("artifacts");
            return new CaptureDisposition.Diverged(MaterializationProblem.DirtyWorktree,
                Value(OpenRepository().DiffTreePaths(first.Recipe.Tree, second.Recipe.Tree)), [],
                "The captures differ in " + string.Join(", ", differences) + ".");
        }
        return new CaptureDisposition.Matched();
    }
}
