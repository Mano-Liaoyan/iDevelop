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
    public RootObservation ObserveRootExit(RunLease lease, OperationId operation, LaunchKey launch, RootExit exit, TimeSpan retryFor = default)
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
            try
            {
                var started = retryFor > TimeSpan.Zero ? _clock.GetTimestamp() : 0;
                while (true)
                {
                    try
                    {
                        decision = Journal("root-exit", () => _store.Record(permit, OperationIds.Derive(operation, "root-exit"), observation));
                        break;
                    }
                    catch (Refusal busy) when (busy.Reason.Problem == RunProblem.JournalBusy && retryFor > TimeSpan.Zero)
                    {
                        if (_clock.GetElapsedTime(started) > retryFor) throw;
                        _probe?.Invoke("journal.root-exit.retry");
                        Thread.Sleep(100);
                        if (_clock.GetElapsedTime(started) > retryFor) throw;
                    }
                }
            }
            catch (Refusal refused)
            {
                if (Read(permit.Workflow, permit.Run).RootExits.TryGetValue(launch, out var recorded))
                {
                    if (recorded.Exit == observation.Exit && recorded.Tip == observation.Tip &&
                        recorded.Head == observation.Head && recorded.Ownership == observation.Ownership)
                        return new RootObservation.Observed(recorded);
                    throw;
                }
                if (refused.Reason.Problem == RunProblem.JournalBusy && retryFor == default) return new RootObservation.Rejected(refused.Reason);
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

    public ValueTask<Settlement> Settle(RunLease lease, OperationId operation, LaunchKey launch, LogCheckpoint log,
        CancellationToken cancellation = default) => SettleCore(lease, operation, launch, log, false, cancellation);

    public ValueTask<Settlement> RecoverSettlement(RunLease lease, OperationId operation, LaunchKey launch,
        CancellationToken cancellation = default) => SettleCore(lease, operation, launch, null, true, cancellation);

    private async ValueTask<Settlement> SettleCore(RunLease lease, OperationId operation, LaunchKey launch, LogCheckpoint? log,
        bool recovery, CancellationToken cancellation)
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
            if (!record.RootExits.ContainsKey(launch) || !recovery && record.Fenced.Contains(launch))
                return new Settlement.Rejected(new(RunProblem.UnresolvedOwnership));
            if (record.TurnClosures.ContainsKey(launch))
            {
                if (!record.Settlements.TryGetValue(launch, out var linked) || !recovery && linked != capture)
                    return new Settlement.Rejected(new(RunProblem.EvidenceMismatch));
                return new Settlement.Closed(linked, record.Dispositions[linked].Disposition);
            }
            if (recovery)
            {
                capture = record.Captures.FirstOrDefault(pair => pair.Value.Any(observation => observation.Launch == launch)).Key;
                if (capture.Value == Guid.Empty)
                    capture = record.Dispositions.Values.FirstOrDefault(disposed => disposed.Launch == launch)?.Capture ??
                        new CaptureId(OperationIds.Derive(operation, "capture").Value);
            }
            if (RunReducer.CaptureForAnotherId(record, launch, capture))
                return new Settlement.Rejected(new(RunProblem.OperationConflict));
            var captureOperation = new OperationId(capture.Value);
            var observations = record.Captures.GetValueOrDefault(capture, []);
            if (recovery)
                log = observations.Count != 0 ? observations[0].Log :
                    AttemptEvidence.Read(_store.AttemptFolder(record.Workflow, record.Id,
                        record.Attempts[launch.Attempt].Task, launch.Attempt)).Checkpoint;
            if (observations.Any(observation => observation.Launch != launch || observation.Log != log))
                return new Settlement.Rejected(new(RunProblem.EvidenceMismatch));
            var missingLog = recovery && observations.Count == 0 &&
                (log is null || _store.TurnEvidenceProblem(record, launch, log) is not null);
            if (!record.Dispositions.TryGetValue(capture, out var disposed))
            {
                CaptureDisposition? disposition = null;
                if (recovery && observations.Count == 0)
                {
                    disposition = new CaptureDisposition.Failed(MaterializationProblem.InputUnavailable,
                        missingLog ? "Missing turn-end log evidence." : "Missing turn-end capture evidence.", []);
                }
                else
                {
                    if (observations.Count == 0 && _store.TurnEvidenceProblem(record, launch, log!) is { } rejection)
                        return new Settlement.Rejected(rejection);
                    var recoveringPair = observations.Count == 1;
                    GitRepository? repository = null;
                    for (var ordinal = observations.Count + 1; ordinal <= 2; ordinal++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (ordinal == 2)
                        {
                            var remaining = observations[0].Completed + TimeSpan.FromMilliseconds(250) - _clock.GetUtcNow();
                            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, _clock, cancellation);
                        }
                        ImmutableArray<EvidenceFile> evidence = [];
                        CaptureObservation observation;
                        try
                        {
                            var storage = new RunStorage(_project, permit.Workflow, permit.Run);
                            var folder = RunStorage.SafePath(storage.Folder, $"captures/{capture.Value:D}/{ordinal}");
                            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                            repository ??= OpenRepository();
                            record = Read(permit.Workflow, permit.Run);
                            observation = Mutate("capture-" + ordinal, () => ObserveCapture(repository, record,
                                capture, ordinal, launch, log!, ref evidence)) with { Recovery = ordinal == 2 && recoveringPair };
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
                        record = DecisionRecord(Journal("capture-" + ordinal, () => _store.Record(permit,
                            OperationIds.Derive(number == 1 ? operation : captureOperation, "capture-" + number),
                            new RunEvent.TurnCaptured(observation))));
                        observations = record.Captures.GetValueOrDefault(capture, []);
                    }
                    disposition ??= DisposeCapture(record, observations[0], observations[1]);
                }
                disposed = new(capture, launch, disposition);
                var decision = Journal("capture-disposition", () => _store.Record(permit,
                    OperationIds.Derive(captureOperation, "capture-disposition"), disposed));
                disposed = (RunEvent.CaptureDisposed)DecisionEvent(decision);
            }
            if (disposed.Launch != launch) return new Settlement.Rejected(new(RunProblem.EvidenceMismatch));
            if (log is null || missingLog)
                return new Settlement.Rejected(new(RunProblem.RecoveryEvidenceInsufficient));
            Journal("close-turn", () => _store.CloseTurn(permit, OperationIds.Derive(captureOperation, "close-turn"), launch, log, capture));
            return new Settlement.Closed(capture, disposed.Disposition);
        }
        catch (Refusal refused) { return new Settlement.Rejected(refused.Reason); }
    }

    private CaptureObservation ObserveCapture(GitRepository repository, RunRecord record, CaptureId id,
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
            OperationIds.Derive(new OperationId(id.Value), "capture-" + ordinal), launch.Attempt,
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
