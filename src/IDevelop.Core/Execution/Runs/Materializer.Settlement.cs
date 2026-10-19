using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

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
            var tip = CheckoutTip(repository, location) ??
                throw Fault(MaterializationProblem.UncertainOwnership, location.Owner.Branch is null ? "The worktree's HEAD is absent." : "The task branch is absent.",
                    new BlockScope.Checkout([], Branch: true));
            var head = Value(repository.SymbolicHead(Checkout(repository, location.Owner)));
            // A read-only worktree commits nothing, so its HEAD stays at the merge of its predecessors.
            var ownership = (location.Owner.Branch is { } branch ? RefOwnership.Accepts(record, repository, branch, tip) : tip == location.AttemptBase)
                ? TipOwnership.Explained : TipOwnership.Unexplained;
            var pin = RunLayout.RootPin(record.RunKey!, record.TaskKeys[location.Owner.Task], launch);
            Pin(repository, pin, tip);
            var observation = new RunEvent.RootExitObserved(launch, exit, _clock.GetUtcNow(), tip, head, ownership);
            RunDecision decision;
            try { decision = JournalRetrying("root-exit", retryFor, () => _store.Record(permit, OperationIds.Derive(operation, "root-exit"), observation)); }
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

    public bool FenceLaunch(RunLease lease, OperationId operation, LaunchKey launch, TimeSpan retryFor)
    {
        var permit = lease.Permit;
        using var authority = lease.Use();
        try
        {
            if (Read(permit.Workflow, permit.Run).Fenced.Contains(launch)) return true;
            if (authority is null) return false;
            JournalRetrying("close-fence", retryFor, () => _store.Record(permit, OperationIds.Derive(operation, "close-fence"),
                new RunEvent.OwnershipFenced([launch])));
            return true;
        }
        catch (Refusal) { return false; }
    }

    private RunDecision JournalRetrying(string step, TimeSpan retryFor, Func<RunDecision> action)
    {
        var started = _clock.GetTimestamp();
        while (true)
        {
            try { return Journal(step, action); }
            catch (Refusal busy) when (busy.Reason.Problem == RunProblem.JournalBusy && retryFor > TimeSpan.Zero)
            {
                if (_clock.GetElapsedTime(started) > retryFor) throw;
                _probe?.Invoke("journal." + step + ".retry");
                Thread.Sleep(100);
                if (_clock.GetElapsedTime(started) > retryFor) throw;
            }
        }
    }

    public ValueTask<Settlement> Settle(RunLease lease, OperationId operation, LaunchKey launch, LogCheckpoint log,
        CancellationToken cancellation = default) => SettleCore(lease, operation, launch, log, false, cancellation);

    public ValueTask<Settlement> RecoverSettlement(RunLease lease, OperationId operation, LaunchKey launch,
        CancellationToken cancellation = default) => SettleCore(lease, operation, launch, null, true, cancellation);

    private ValueTask<Settlement> SettleCore(RunLease lease, OperationId operation, LaunchKey launch, LogCheckpoint? log,
        bool recovery, CancellationToken cancellation) => ValueTask.FromResult(SettleNow(lease, operation, launch, log, recovery, cancellation));

    private Settlement SettleNow(RunLease lease, OperationId operation, LaunchKey launch, LogCheckpoint? log,
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
                    // A turn ends with one capture. A journal written before ADR 0032 may already hold the first of two.
                    if (observations.Count == 0)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        ImmutableArray<EvidenceFile> evidence = [];
                        try
                        {
                            var storage = new RunStorage(_project, permit.Workflow, permit.Run);
                            var folder = RunStorage.SafePath(storage.Folder, $"captures/{capture.Value:D}/1");
                            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                            var repository = OpenRepository();
                            record = Read(permit.Workflow, permit.Run);
                            var observation = Mutate("capture-1", () => ObserveCapture(repository, record, capture, launch, log!, ref evidence));
                            record = DecisionRecord(Journal("capture-1", () => _store.Record(permit,
                                OperationIds.Derive(operation, "capture-1"), new RunEvent.TurnCaptured(observation))));
                            observations = record.Captures.GetValueOrDefault(capture, []);
                        }
                        catch (MaterializationFailure failed)
                        {
                            disposition = new CaptureDisposition.Failed(failed.Problem, failed.Message, evidence);
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                        {
                            disposition = new CaptureDisposition.Failed(MaterializationProblem.InputUnavailable, error.Message, evidence);
                        }
                    }
                    disposition ??= DisposeCapture(record, observations[^1]);
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

    /// <summary>
    /// Captures the worktree once its turn's client exited. An editing task's capture is the one commit its turn ends with,
    /// on the turn's branch tip. A read-only task's worktree has no branch, and its capture records the files without a commit.
    /// </summary>
    private CaptureObservation ObserveCapture(GitRepository repository, RunRecord record, CaptureId id, LaunchKey launch,
        LogCheckpoint log, ref ImmutableArray<EvidenceFile> evidence)
    {
        const int ordinal = 1;
        var started = _clock.GetUtcNow();
        var prepared = record.Preparations[launch];
        try { VerifyOwnedCheckout(repository, prepared.Location, record); }
        catch (MaterializationFailure failed) { throw Fault(MaterializationProblem.UncertainOwnership, failed.Message, new BlockScope.Ownership()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw Fault(MaterializationProblem.UncertainOwnership, error.Message, new BlockScope.Ownership()); }
        var checkout = Checkout(repository, prepared.Location.Owner);
        if (!Value(repository.UnmergedEntries(checkout)).IsEmpty)
            throw Fault(MaterializationProblem.DirtyWorktree, "The writer index has unresolved stages.", BlockScope.Checkout.Whole);
        var tracked = Value(repository.TrackedFiles(checkout, ".idp/inputs", ".idp/outbox", ".worktrees"));
        if (!tracked.IsEmpty) throw Fault(MaterializationProblem.DirtyWorktree, "Tracked execution data: " + string.Join(", ", tracked), new BlockScope.Checkout(tracked));
        var tip = CheckoutTip(repository, prepared.Location);
        var head = Value(repository.SymbolicHead(checkout));
        var storage = new RunStorage(_project, record.Workflow, record.Id);
        EvidenceFile? index = null;
        var indexBytes = Value(repository.IndexBytes(checkout));
        if (indexBytes is { } bytes)
        {
            var relative = RunStorage.CapturePath(id, ordinal, "index");
            index = new(relative, Revision.Hash(bytes), bytes.LongLength);
            RunStorage.Publish(storage.Folder, relative, bytes, index.Content, index.ByteLength);
        }
        var captured = Value(repository.Capture(checkout));
        if (captured.IndexBefore != captured.IndexAfter || captured.IndexBefore != index?.Content)
            throw Fault(MaterializationProblem.DirtyWorktree, "The writer index changed during capture.", BlockScope.Checkout.Whole);
        var task = record.Attempts[launch.Attempt].Task;
        var logged = AttemptEvidence.Read(_store.AttemptFolder(record.Workflow, record.Id, task, launch.Attempt), log);
        if (logged.Rejection is { } rejection) throw Fault(MaterializationProblem.InputUnavailable, rejection.Problem.ToString(), new BlockScope.Operation());
        var artifacts = prepared.OutboxPath.Length == 0 ? [] : FreezeOutbox(record.Workflow, record.Id,
            OperationIds.Derive(new OperationId(id.Value), "capture-" + ordinal), launch.Attempt,
            RunStorage.CapturePath(id, ordinal, "artifacts"), checkout, ref evidence);
        var unexplained = UnexplainedRefs(record, repository, prepared, out var sharedRefs);
        var refBytes = Encoding.UTF8.GetBytes(RunJournal.Canonical(sharedRefs));
        var refs = new EvidenceFile(RunStorage.CapturePath(id, ordinal, "refs.json"), Revision.Hash(refBytes), refBytes.LongLength);
        RunStorage.Publish(storage.Folder, refs.RelativePath, refBytes, refs.Content, refs.ByteLength);
        evidence = evidence.Add(refs);
        var root = record.RootExits[launch];
        var definition = record.Revisions[record.Attempts[launch.Attempt].Revision].Snapshot.Tasks[task];
        var recipe = new CommitRecipe(captured.Tree, [root.Tip],
            $"{definition.Title}\n\nIDP-Run: {record.Id.Value:D}\nIDP-Task: {task.Value:D}\nIDP-Attempt: {launch.Attempt.Value:D}\n",
            "iDevelop <idevelop@localhost>", "iDevelop <idevelop@localhost>", DateTimeOffset.FromUnixTimeSeconds(root.At.ToUnixTimeSeconds()));
        TreeId? indexTree = null;
        CommitId? candidate = null;
        if (index is not null) indexTree = Value(repository.WriteTree(GitRepository.ParseIndex(indexBytes!)));
        if (prepared.Location.Owner.Branch is not null)
        {
            if (indexTree is { } staged)
            {
                var indexCommit = Value(repository.CreateCommit(recipe with { Tree = staged, Parents = [], Message = "Index of " + recipe.Message }));
                Pin(repository, RunLayout.CaptureIndexPin(record.RunKey!, record.TaskKeys[task], launch, ordinal), indexCommit);
            }
            candidate = Value(repository.CreateCommit(recipe));
            Pin(repository, RunLayout.CapturePin(record.RunKey!, record.TaskKeys[task], launch, ordinal), candidate.Value);
        }
        return new(id, ordinal, launch, log, started, _clock.GetUtcNow(), recipe, candidate, tip, head, index,
            logged.Record!.Result, artifacts, refs, unexplained) { IndexTree = indexTree };
    }

    /// <summary>
    /// The run's shared refs the journal does not explain at the capture. <paramref name="after"/> is the live snapshot of
    /// those refs that the capture records as its evidence. A turn prepared since ADR 0032 keeps no shared-ref snapshot, so
    /// each run ref is judged by the journal alone; an earlier journal's prepared snapshot still judges refs/stash.
    /// </summary>
    private ImmutableArray<string> UnexplainedRefs(RunRecord record, GitRepository repository, PreparedExecution prepared,
        out SortedDictionary<string, CommitId> after)
    {
        var snapshot = record.Receipts.Values.Select(entry => entry.Event).OfType<RunEvent.Prepared>()
            .Single(e => e.Execution.Launch == prepared.Launch).SharedRefs;
        var storage = new RunStorage(_project, record.Workflow, record.Id);
        var bytes = snapshot is null ? Encoding.UTF8.GetBytes(RunJournal.Canonical(new SortedDictionary<string, CommitId>()))
            : RunStorage.Read(storage.Folder, snapshot.RelativePath, snapshot.Content, snapshot.ByteLength);
        var before = JsonSerializer.Deserialize<SortedDictionary<string, CommitId>>(bytes, RunJournal.Options) ??
            throw Fault(MaterializationProblem.InputUnavailable, "The prepared shared-ref snapshot is absent.", new BlockScope.Operation());
        // The budget runs on the materializer's clock, as the journal's retries do.
        var started = _clock.GetTimestamp();
        for (var cap = 4; ; cap = Math.Min(cap * 2, 64))
        {
            var basis = Read(record.Workflow, record.Id);
            after = Value(repository.RefSnapshot(["refs/stash", $"refs/heads/idp/{basis.RunKey}/", $"refs/idp/{basis.RunKey}/"],
                RunLayout.PinPrefix(basis.RunKey!)));
            _probe?.Invoke("refs.snapshot.after");
            record = Read(record.Workflow, record.Id);
            if (RefOwnership.Basis(basis) == RefOwnership.Basis(record)) break;
            if (_clock.GetElapsedTime(started) >= TimeSpan.FromSeconds(1)) throw new Refusal(new(RunProblem.JournalBusy));
            Thread.Sleep(Random.Shared.Next(1, cap + 1));
        }
        var owned = record.GitIntents.Values.Select(intent => intent.Mutation switch
        {
            GitMutation.CreateWorktree create => create.Owner.Branch,
            GitMutation.MoveRef move => move.Change.Ref,
            GitMutation.RestoreRef restore => restore.Change.Ref,
            _ => null,
        }).OfType<string>();
        var unexplained = ImmutableArray.CreateBuilder<string>();
        foreach (var name in before.Keys.Union(after.Keys, StringComparer.Ordinal).Union(owned, StringComparer.Ordinal))
        {
            CommitId? previous = before.TryGetValue(name, out var old) ? old : null;
            CommitId? current = after.TryGetValue(name, out var tip) ? tip : null;
            if (name == "refs/stash")
            {
                if (snapshot is null || previous == current) continue;
            }
            else if (RefOwnership.Accepts(record, repository, name, current)) continue;
            unexplained.Add(name);
        }
        return unexplained.ToImmutable();
    }

    /// <summary>The commit a worktree is on: its branch's tip, or a read-only worktree's detached HEAD.</summary>
    private static CommitId? CheckoutTip(GitRepository repository, ExecutionLocation location) => location.Owner.Branch is { } branch
        ? Value(repository.ReadRef(branch))
        : Value(repository.ResolveCheckoutHead(Checkout(repository, location.Owner)));

    /// <summary>A capture matches its turn when the worktree is still on the commit and HEAD the client's exit left.</summary>
    private static CaptureDisposition DisposeCapture(RunRecord record, CaptureObservation capture)
    {
        var root = record.RootExits[capture.Launch];
        ImmutableArray<string> refs = [];
        if (capture.Tip != root.Tip)
            refs = refs.Add(record.Preparations[capture.Launch].Location.Owner.Branch ?? "HEAD");
        if (capture.Head != root.Head && !refs.Contains("HEAD")) refs = refs.Add("HEAD");
        if (!refs.IsEmpty) return new CaptureDisposition.Diverged(MaterializationProblem.UncertainOwnership, [],
            [.. refs.Order(StringComparer.Ordinal)], "The branch tip or HEAD differs from the root observation.");
        refs = [.. capture.UnexplainedRefs.Order(StringComparer.Ordinal)];
        if (!refs.IsEmpty) return new CaptureDisposition.Diverged(MaterializationProblem.UncertainOwnership, [], refs,
            "The capture contains unexplained shared refs.");
        return new CaptureDisposition.Matched();
    }
}
