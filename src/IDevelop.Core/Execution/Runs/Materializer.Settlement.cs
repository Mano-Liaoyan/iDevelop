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

    /// <summary>
    /// The turn closed with its capture and how it differs from the turn's root observation, null when it matched, or with
    /// the block that says why its capture could not be taken.
    /// </summary>
    internal sealed record Closed(CaptureObservation? Capture, CaptureDivergence? Divergence, MaterializationBlock? Failure) : Settlement;

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
            var observed = record.Captures.GetValueOrDefault(launch);
            if (record.TurnClosures.ContainsKey(launch))
            {
                if (!RunReducer.CaptureSettled(record, launch) || !recovery && observed is not null && observed.Capture != capture)
                    return new Settlement.Rejected(new(RunProblem.EvidenceMismatch));
                return Closed(record, observed, RunReducer.CaptureFailure(record, launch));
            }
            if (!recovery && observed is not null && observed.Capture != capture)
                return new Settlement.Rejected(new(RunProblem.OperationConflict));
            var captureOperation = new OperationId((observed?.Capture ?? capture).Value);
            if (recovery)
                log = observed?.Log ?? AttemptEvidence.Read(_store.AttemptFolder(record.Workflow, record.Id,
                    record.Attempts[launch.Attempt].Task, launch.Attempt)).Checkpoint;
            if (observed is not null && observed.Log != log)
                return new Settlement.Rejected(new(RunProblem.EvidenceMismatch));
            var missingLog = recovery && observed is null &&
                (log is null || _store.TurnEvidenceProblem(record, launch, log) is not null);
            var failure = RunReducer.CaptureFailure(record, launch);
            // A turn takes one capture. Recovery takes none: a turn whose capture was not recorded closes with a block.
            if (observed is null && failure is null)
            {
                (MaterializationProblem Problem, string Detail, ImmutableArray<EvidenceFile> Evidence)? fault = null;
                if (recovery)
                {
                    fault = (MaterializationProblem.InputUnavailable, missingLog ? "Missing turn-end log evidence." : "Missing turn-end capture evidence.", []);
                }
                else
                {
                    if (_store.TurnEvidenceProblem(record, launch, log!) is { } rejection)
                        return new Settlement.Rejected(rejection);
                    cancellation.ThrowIfCancellationRequested();
                    ImmutableArray<EvidenceFile> evidence = [];
                    try
                    {
                        var storage = new RunStorage(_project, permit.Workflow, permit.Run);
                        var folder = RunStorage.SafePath(storage.Folder, $"captures/{capture.Value:D}");
                        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                        var repository = OpenRepository();
                        record = Read(permit.Workflow, permit.Run);
                        var observation = Mutate("capture", () => ObserveCapture(repository, record, capture, launch, log!, ref evidence));
                        record = DecisionRecord(Journal("capture", () => _store.Record(permit,
                            OperationIds.Derive(operation, "capture-1"), new RunEvent.TurnCaptured(observation))));
                        observed = record.Captures[launch];
                    }
                    catch (MaterializationFailure failed) { fault = (failed.Problem, failed.Message, evidence); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { fault = (MaterializationProblem.InputUnavailable, error.Message, evidence); }
                }
                if (fault is { } found)
                {
                    failure = new MaterializationBlock(RunReducer.CaptureBlock(launch), record.Attempts[launch.Attempt].Task, launch.Attempt,
                        found.Problem, record.Preparations[launch].Inputs, found.Evidence, found.Detail) { Scope = new BlockScope.Operation() };
                    Journal("capture-blocked", () => _store.Record(permit, OperationIds.Derive(captureOperation, "capture-blocked"),
                        new RunEvent.Blocked(failure)));
                }
            }
            if (log is null || missingLog)
                return new Settlement.Rejected(new(RunProblem.RecoveryEvidenceInsufficient));
            Journal("close-turn", () => _store.CloseTurn(permit, OperationIds.Derive(captureOperation, "close-turn"), launch, log, settles: true));
            return Closed(record, observed, failure);
        }
        catch (Refusal refused) { return new Settlement.Rejected(refused.Reason); }
    }

    private static Settlement.Closed Closed(RunRecord record, CaptureObservation? capture, MaterializationBlock? failure) =>
        new(capture, capture is null ? null : RunReducer.Divergence(record, capture), failure);

    /// <summary>How the closed turn <paramref name="launch"/> settled, as the journal records it.</summary>
    internal static Settlement.Closed Settled(RunRecord record, LaunchKey launch) =>
        Closed(record, record.Captures.GetValueOrDefault(launch), RunReducer.CaptureFailure(record, launch));

    /// <summary>
    /// Captures the worktree once its turn's client exited. An editing task's capture is the one commit its turn ends with,
    /// on the turn's branch tip. A read-only task's worktree has no branch, and its capture records the files without a commit.
    /// </summary>
    private CaptureObservation ObserveCapture(GitRepository repository, RunRecord record, CaptureId id, LaunchKey launch,
        LogCheckpoint log, ref ImmutableArray<EvidenceFile> evidence)
    {
        var started = _clock.GetUtcNow();
        var prepared = record.Preparations[launch];
        try { VerifyOwnedCheckout(repository, prepared.Location, record); }
        catch (MaterializationFailure failed) { throw Fault(MaterializationProblem.UncertainOwnership, failed.Message, new BlockScope.Ownership()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw Fault(MaterializationProblem.UncertainOwnership, error.Message, new BlockScope.Ownership()); }
        var checkout = Checkout(repository, prepared.Location.Owner);
        if (!Value(repository.UnmergedEntries(checkout)).IsEmpty)
            throw Fault(MaterializationProblem.DirtyWorktree, "The writer index has unresolved stages.", BlockScope.Checkout.Whole);
        var tracked = Value(repository.TrackedFiles(repository.ProjectIn(checkout), ".idp/inputs", ".idp/outbox", ".worktrees"));
        if (!tracked.IsEmpty) throw Fault(MaterializationProblem.DirtyWorktree, "Tracked execution data: " + string.Join(", ", tracked), new BlockScope.Checkout(tracked));
        var tip = CheckoutTip(repository, prepared.Location);
        var head = Value(repository.SymbolicHead(checkout));
        var storage = new RunStorage(_project, record.Workflow, record.Id);
        EvidenceFile? index = null;
        var indexBytes = Value(repository.IndexBytes(checkout));
        if (indexBytes is { } bytes)
        {
            var relative = RunStorage.CapturePath(id, "index");
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
            OperationIds.Derive(new OperationId(id.Value), "capture-1"), launch.Attempt,
            RunStorage.CapturePath(id, "artifacts"), repository.ProjectIn(checkout), ref evidence);
        var unexplained = UnexplainedRefs(record, repository, out var sharedRefs);
        var refBytes = Encoding.UTF8.GetBytes(RunJournal.Canonical(sharedRefs));
        var refs = new EvidenceFile(RunStorage.CapturePath(id, "refs.json"), Revision.Hash(refBytes), refBytes.LongLength);
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
        if (RunReducer.MergeOf(record, launch.Attempt) is { } conflict)
        {
            // A Merge node's agent resolved the conflict once no conflicting file keeps its markers. Its files are the merge, on
            // the conflict's two parents; with markers left, its turn takes no commit and the conflict stays open.
            recipe = recipe with
            {
                Parents = [conflict.Ours, conflict.Theirs],
                Message = $"{definition.Title}: merge\n\nIDP-Run: {record.Id.Value:D}\nIDP-Task: {task.Value:D}\nIDP-Attempt: {launch.Attempt.Value:D}\n",
            };
            if (!MergeMarkers.Remain(checkout, conflict.Paths))
            {
                candidate = Value(repository.CreateCommit(recipe));
                Pin(repository, RunLayout.CapturePin(record.RunKey!, record.TaskKeys[task], launch), candidate.Value);
            }
        }
        else if (prepared.Location.Owner.Branch is not null)
        {
            if (indexTree is { } staged)
            {
                var indexCommit = Value(repository.CreateCommit(recipe with { Tree = staged, Parents = [], Message = "Index of " + recipe.Message }));
                Pin(repository, RunLayout.CaptureIndexPin(record.RunKey!, record.TaskKeys[task], launch), indexCommit);
            }
            candidate = Value(repository.CreateCommit(recipe));
            Pin(repository, RunLayout.CapturePin(record.RunKey!, record.TaskKeys[task], launch), candidate.Value);
        }
        return new(id, launch, log, started, _clock.GetUtcNow(), recipe, candidate, tip, head, index,
            logged.Record!.Result, artifacts, refs, unexplained) { IndexTree = indexTree };
    }

    /// <summary>
    /// The run's shared refs the journal does not explain at the capture. <paramref name="after"/> is the live snapshot of
    /// those refs that the capture records as its evidence. Each run ref is judged by the journal alone.
    /// </summary>
    private ImmutableArray<string> UnexplainedRefs(RunRecord record, GitRepository repository, out SortedDictionary<string, CommitId> after)
    {
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
            _ => null,
        }).OfType<string>();
        var unexplained = ImmutableArray.CreateBuilder<string>();
        foreach (var name in after.Keys.Union(owned, StringComparer.Ordinal))
        {
            CommitId? current = after.TryGetValue(name, out var tip) ? tip : null;
            if (name == "refs/stash" || RefOwnership.Accepts(record, repository, name, current)) continue;
            unexplained.Add(name);
        }
        return unexplained.ToImmutable();
    }

    /// <summary>The commit a worktree is on: its branch's tip, or a read-only worktree's detached HEAD.</summary>
    private static CommitId? CheckoutTip(GitRepository repository, ExecutionLocation location) => location.Owner.Branch is { } branch
        ? Value(repository.ReadRef(branch))
        : Value(repository.ResolveCheckoutHead(Checkout(repository, location.Owner)));
}
