using System.Collections.Immutable;
using System.Text.Json;
using IDevelop.Workflows;

namespace IDevelop.Execution;

internal sealed partial class Materializer
{
    public Publication Publish(RunLease lease, OperationId operation, AttemptId attempt)
    {
        using var authority = lease.Use();
        if (authority is null) return new Publication.Rejected(new(RunProblem.TaskBusy));
        if (LeaseProblem(lease) is { } problem) return new Publication.Rejected(new(problem));
        var permit = lease.Permit;
        var workflow = permit.Workflow;
        var run = permit.Run;
        var task = lease.Task;
        InputId? inputs = null;
        var planId = operation;
        var step = "preconditions";
        ImmutableArray<EvidenceFile> evidence = [];
        try
        {
            var record = Read(workflow, run);
            if (!record.Attempts.TryGetValue(attempt, out var writer)) return new Publication.Rejected(new(RunProblem.UnknownAttempt));
            if (LeaseProblem(lease, writer.Task) is { } mismatch) return new Publication.Rejected(new(mismatch));
            var definition = record.Revisions[writer.Revision].Snapshot.Tasks[task];
            if (definition.Blueprint.Work is not WorkSpec.Agent { Access: AgentAccess.Edit })
                return new Publication.Rejected(new(RunProblem.UnsupportedResult));
            if (record.Closures.GetValueOrDefault(attempt) is not AttemptEnd.Logged { Outcome: TerminalAttemptOutcome.Succeeded } closure)
                return new Publication.Rejected(new(RunProblem.OutcomeMismatch));
            if (!record.Preparations.TryGetValue(new(attempt, 1), out var prepared))
                return new Publication.Rejected(new(RunProblem.InvalidClaim));
            inputs = prepared.Inputs;
            var logged = AttemptEvidence.Read(_store.AttemptFolder(workflow, run, task, attempt), closure.Evidence);
            if (!AttemptEvidence.Matches(logged, TerminalAttemptOutcome.Succeeded) || logged.Record?.Result is null)
                return new Publication.Rejected(new(RunProblem.OutcomeMismatch));
            var launch = new LaunchKey(attempt, logged.Record.Turns.Count);
            var captureId = PublicationCapture(record, launch, closure.Evidence, logged.Record.Result);
            var root = record.RootExits[launch];
            var existing = record.Plans.SingleOrDefault(pair => pair.Value is MaterializationPlan.Publication p && p.Attempt == attempt);
            if (existing.Value is MaterializationPlan.Publication) planId = existing.Key;
            if (record.Results.FirstOrDefault(result => result.Origin is ResultOrigin.Executed e && e.Attempt == attempt) is { } accepted)
            {
                ResolvePublicationBlocks(permit, operation, planId);
                return new Publication.Accepted(accepted);
            }
            if (PublicationDrift(record, attempt) is { } drift) return new Publication.Blocked(drift);
            if (record.Phase is not (RunPhase.Approved or RunPhase.StopRequested)) return new Publication.Rejected(new(RunProblem.RunStopped));
            step = "settlement";
            VerifyPublicationDisposition(record, captureId, ref evidence);
            var frozen = record.Captures[captureId][0];
            step = "repository";
            var repository = OpenRepository();
            using var mutation = repository.TakeMutationLock();
            if (mutation is null) return new Publication.Rejected(new(RunProblem.JournalBusy));
            record = Read(workflow, run);
            captureId = PublicationCapture(record, launch, closure.Evidence, logged.Record.Result);
            root = record.RootExits[launch];
            existing = record.Plans.SingleOrDefault(pair => pair.Value is MaterializationPlan.Publication p && p.Attempt == attempt);
            if (existing.Value is MaterializationPlan.Publication) planId = existing.Key;
            if (record.Results.FirstOrDefault(result => result.Origin is ResultOrigin.Executed e && e.Attempt == attempt) is { } concurrent)
            {
                ResolvePublicationBlocks(permit, operation, planId);
                return new Publication.Accepted(concurrent);
            }
            if (PublicationDrift(record, attempt) is { } concurrentDrift) return new Publication.Blocked(concurrentDrift);
            step = "settlement";
            VerifyPublicationDisposition(record, captureId, ref evidence);
            frozen = record.Captures[captureId][0];
            VerifyRepository(record, repository);
            var checkout = Checkout(repository, prepared.Location.Owner);
            MaterializationPlan.Publication plan;
            if (existing.Value is MaterializationPlan.Publication persisted)
            {
                plan = persisted;
                CopyPublicationArtifacts(workflow, run, frozen, plan.Result);
                step = "ownership";
                VerifyPublicationRefs(record, repository, prepared, operation, workflow, run, ref evidence);
                VerifyCheckout(repository, prepared.Location, keepChanges: true, record);
                VerifyPublicationParent(repository, prepared.Location.AttemptBase, plan.VerifiedTip);
                step = "commit";
                if (Value(Mutate("commit", () => repository.CreateCommit(plan.Recipe))) != plan.Commit)
                    throw Fault(MaterializationProblem.UncertainOwnership, "The persisted publication recipe produced a different commit.");
            }
            else
            {
                step = "ownership";
                VerifyPublicationRefs(record, repository, prepared, operation, workflow, run, ref evidence);
                VerifyCheckout(repository, prepared.Location, keepChanges: true, record);
                VerifyPublicationParent(repository, prepared.Location.AttemptBase, root.Tip);
                step = "capture";
                VerifyPublicationBaseline(repository, checkout, frozen.Recipe.Tree, frozen.Index?.Content, true, permit, operation, ref evidence);
                var result = new ResultId(OperationIds.Derive(operation, "result").Value);
                step = "outbox";
                var artifacts = CopyPublicationArtifacts(workflow, run, frozen, result);
                step = "commit";
                var commit = Value(Mutate("commit", () => repository.CreateCommit(frozen.Recipe)));
                if (commit != frozen.Candidate)
                    throw Fault(MaterializationProblem.UncertainOwnership, "The frozen publication recipe produced a different commit.");
                plan = new(attempt, result, record.CurrentResults.GetValueOrDefault(task)?.Id, root.Tip, frozen.Index?.Content,
                    frozen.Recipe, frozen.Candidate, frozen.Report!, artifacts) { Capture = frozen.Capture };
                step = "plan";
                var plannedOperation = OperationIds.Derive(operation, "plan");
                Journal("plan", () => _store.Record(permit, plannedOperation, new RunEvent.Planned(plan)));
                planId = plannedOperation;
            }
            evidence = [];
            step = "branch";
            var branch = new GitMutation.MoveRef(new(prepared.Location.Owner.Branch, plan.VerifiedTip, plan.Commit));
            VerifyPendingPublicationMove(branch);
            RequirePublication(_refs.Publish(permit, operation, planId, "branch", repository, branch.Change));
            step = "index";
            var indexIntent = OperationIds.Derive(operation, "index-intent");
            record = Read(workflow, run);
            if (!PublicationObserved(record, planId, new GitMutation.AlignIndex(task, plan.IndexBefore, plan.Recipe.Tree)))
            {
                VerifyPendingPublicationMove(new GitMutation.AlignIndex(task, plan.IndexBefore, plan.Recipe.Tree));
                Journal("index-intent", () => _store.Record(permit, indexIntent,
                    new RunEvent.GitIntended(planId, new GitMutation.AlignIndex(task, plan.IndexBefore, plan.Recipe.Tree))));
                var aligned = Mutate("align-index", () =>
                {
                    VerifyCheckout(repository, prepared.Location, keepChanges: true, Read(workflow, run));
                    return repository.AlignIndex(checkout, plan.IndexBefore, plan.Recipe.Tree);
                });
                if (aligned is IndexAlignment.Unexpected)
                    throw Fault(MaterializationProblem.DirtyWorktree, "The writer index changed before publication alignment.");
                if (aligned is IndexAlignment.Failed failed)
                    throw Fault(Value(repository.Capture(checkout)).Tree != plan.Recipe.Tree ? MaterializationProblem.DirtyWorktree : MaterializationProblem.GitFailed,
                        failed.Detail);
                Journal("index-observed", () => _store.Record(permit, OperationIds.Derive(operation, "index-observed"),
                    new RunEvent.GitObserved(indexIntent, new(aligned is IndexAlignment.AlreadyAligned, plan.Recipe.Tree.Hex))));
            }
            step = "result-ref";
            var resultRef = new GitMutation.MoveRef(new(RunLayout.ResultRef(record.RunKey!, record.TaskKeys[task], attempt), null, plan.Commit));
            VerifyPendingPublicationMove(resultRef);
            RequirePublication(_refs.Publish(permit, operation, planId, "result", repository, resultRef.Change));
            step = "verify";
            VerifyPublicationRefs(Read(workflow, run), repository, prepared, operation, workflow, run, ref evidence);
            VerifyPublication(repository, prepared, plan, workflow, run);
            step = "accepted";
            var decision = Journal("accepted", () => _store.AcceptPublication(permit, OperationIds.Derive(operation, "accepted"), planId));
            var resultRecord = ((RunEvent.ResultAccepted)DecisionEvent(decision)).Result;
            ResolvePublicationBlocks(permit, operation, planId);
            return new Publication.Accepted(resultRecord);

            void VerifyPendingPublicationMove(GitMutation move)
            {
                var current = Read(workflow, run);
                if (PublicationObserved(current, planId, move)) return;
                var compareIndex = !current.GitIntents.Values.Any(intent => intent.Plan == planId && intent.Mutation is GitMutation.AlignIndex);
                VerifyPublicationBaseline(repository, checkout, plan.Recipe.Tree, plan.IndexBefore, compareIndex, permit, operation, ref evidence);
            }
        }
        catch (Refusal refused) { return new Publication.Rejected(refused.Reason); }
        catch (MaterializationFailure failed)
        { return PublicationBlock(permit, operation, step, new(planId, task, attempt, failed.Problem, inputs, evidence, failed.Message)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return PublicationBlock(permit, operation, step, new(planId, task, attempt, MaterializationProblem.InputUnavailable, inputs, evidence, error.Message)); }
    }

    private static void VerifyPublicationParent(GitRepository repository, CommitId attemptBase, CommitId tip)
    {
        switch (repository.IsAncestor(attemptBase, tip))
        {
            case GitAncestry.No:
                throw Fault(MaterializationProblem.UncertainOwnership, $"The writer branch tip {tip.Hex} does not contain the attempt base {attemptBase.Hex}.");
            case GitAncestry.Failed failed:
                throw Fault(MaterializationProblem.GitFailed, failed.Detail);
        }
    }

    private static CaptureId PublicationCapture(RunRecord record, LaunchKey launch, LogCheckpoint log, string report)
    {
        if (!record.Claims.ContainsKey(launch) || !record.RootExits.ContainsKey(launch) ||
            !record.Settlements.TryGetValue(launch, out var capture) || !record.Dispositions.ContainsKey(capture) ||
            !record.Captures.TryGetValue(capture, out var observations) || observations.Any(o => o.Launch != launch || o.Log != log || o.Report != report))
            throw new Refusal(new(RunProblem.OutcomeMismatch));
        return capture;
    }

    private void VerifyPublicationDisposition(RunRecord record, CaptureId capture, ref ImmutableArray<EvidenceFile> evidence)
    {
        switch (record.Dispositions[capture].Disposition)
        {
            case CaptureDisposition.Diverged diverged:
                if (diverged.Problem == MaterializationProblem.UncertainOwnership)
                {
                    var observations = record.Captures[capture];
                    var prepared = record.Receipts.Values.Select(entry => entry.Event).OfType<RunEvent.Prepared>()
                        .Single(entry => entry.Execution.Launch == observations[0].Launch).SharedRefs;
                    evidence = [prepared, .. observations.Select(observation => observation.SharedRefs)];
                    var storage = new RunStorage(_project, record.Workflow, record.Id);
                    var snapshots = evidence.Select(file => JsonSerializer.Deserialize<SortedDictionary<string, CommitId>>(
                        RunStorage.Read(storage.Folder, file.RelativePath, file.Content, file.ByteLength), RunJournal.Options) ??
                        throw Fault(MaterializationProblem.InputUnavailable, "The shared-ref snapshot is absent.")).ToArray();
                    var detail = diverged.Refs.Select(name => $"{name}: prepared {Tip(snapshots[0], name)}, " +
                        $"observation 1 {Tip(snapshots[1], name)}, observation 2 {Tip(snapshots[2], name)}");
                    throw Fault(diverged.Problem, diverged.Detail + " Paths or refs: " + string.Join("; ", detail));
                }
                throw Fault(diverged.Problem, diverged.Detail + " Paths or refs: " + string.Join(", ", diverged.Paths.Concat(diverged.Refs)));
            case CaptureDisposition.Failed failed:
                evidence = failed.Evidence;
                throw Fault(failed.Problem, failed.Detail);
        }
    }

    private static string Tip(IReadOnlyDictionary<string, CommitId> refs, string name) =>
        refs.TryGetValue(name, out var tip) ? tip.Hex : "absent";

    private void VerifyPublicationBaseline(GitRepository repository, string checkout, TreeId tree, Digest? index,
        bool compareIndex, CoordinatorPermit permit, OperationId operation, ref ImmutableArray<EvidenceFile> evidence)
    {
        VerifyWriterIndexLock(repository, checkout, permit, operation, ref evidence);
        if (repository.UnmergedEntries(checkout) is not GitRead<ImmutableArray<StageEntry>>.Read stages)
            throw Fault(MaterializationProblem.DirtyWorktree, "Writer files or index changed after the turn-end capture.");
        if (!stages.Value.IsEmpty)
            throw Fault(MaterializationProblem.DirtyWorktree, "The writer index has unresolved stages.");
        var tracked = Value(repository.TrackedFiles(checkout, ".idp/inputs", ".idp/outbox", ".worktrees"));
        if (!tracked.IsEmpty) throw Fault(MaterializationProblem.DirtyWorktree, "Tracked execution data: " + string.Join(", ", tracked));
        var live = Value(Mutate("capture", () => repository.Capture(checkout)));
        if (live.Tree != tree || compareIndex && (live.IndexBefore != index || live.IndexAfter != index))
            throw Fault(MaterializationProblem.DirtyWorktree, "Writer files or index changed after the turn-end capture.");
    }

    private ImmutableArray<ArtifactRecord> CopyPublicationArtifacts(WorkflowId workflow, RunId run, CaptureObservation frozen,
        ResultId result)
    {
        var storage = new RunStorage(_project, workflow, run);
        return [.. frozen.Artifacts.Select(artifact =>
        {
            var destination = RunStorage.ArtifactPath(result, artifact.Name);
            _probe?.Invoke("artifact." + artifact.Name + ".before");
            try
            {
                var bytes = RunStorage.Read(storage.Folder, artifact.StoredPath, artifact.Content, artifact.ByteLength);
                RunStorage.Publish(storage.Folder, destination, bytes, artifact.Content, artifact.ByteLength);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw Fault(MaterializationProblem.InputUnavailable,
                    $"Attempt {frozen.Launch.Attempt.Value:D}, capture path {artifact.StoredPath}, result path {destination}: {error.Message}");
            }
            _probe?.Invoke("artifact." + artifact.Name + ".after");
            return artifact with { StoredPath = destination };
        })];
    }

    private static bool IsPublicationDrift(MaterializationBlock block) =>
        block.Problem is MaterializationProblem.DirtyWorktree or MaterializationProblem.UncertainOwnership;

    private static MaterializationBlock? PublicationDrift(RunRecord record, AttemptId attempt) =>
        record.Blocks.Where(pair => !pair.Value.Resolved && pair.Value.Block.Attempt == attempt && IsPublicationDrift(pair.Value.Block))
            .OrderBy(pair => record.Receipts[pair.Key].Sequence).Select(pair => pair.Value.Block).FirstOrDefault();

    private static void RequirePublication(RefPublication outcome)
    {
        if (outcome is RefPublication.Rejected rejected) throw new Refusal(rejected.Reason);
        if (outcome is RefPublication.Blocked blocked) throw Fault(blocked.Problem, blocked.Detail);
    }

    private static bool PublicationObserved(RunRecord record, OperationId plan, GitMutation mutation) =>
        record.GitIntents.Any(pair => pair.Value.Plan == plan && RunReducer.Same(pair.Value.Mutation, mutation) &&
            record.GitObservations.ContainsKey(pair.Key));

    private void VerifyPublication(GitRepository repository, PreparedExecution prepared, MaterializationPlan.Publication plan,
        WorkflowId workflow, RunId run)
    {
        var checkout = Checkout(repository, prepared.Location.Owner);
        var capture = Mutate("verify-capture", () => repository.Capture(checkout));
        var record = Read(workflow, run);
        VerifyCheckout(repository, prepared.Location, keepChanges: true, record);
        if (Value(repository.ReadRef(prepared.Location.Owner.Branch)) != plan.Commit ||
            Value(repository.ReadRef(RunLayout.ResultRef(record.RunKey!, record.TaskKeys[prepared.Location.Owner.Task], plan.Attempt))) != plan.Commit)
            throw Fault(MaterializationProblem.UncertainOwnership, "Publication branch or result ref changed.");
        if (capture is not GitRead<GitCapture>.Read captured || captured.Value.Tree != plan.Recipe.Tree ||
            Value(repository.Status(checkout)).Length != 0 ||
            repository.ReadIndexTree(checkout) is not GitRead<TreeId>.Read index || index.Value != plan.Recipe.Tree)
            throw Fault(MaterializationProblem.DirtyWorktree, "Writer files or index changed after the publication capture.");
        var storage = new RunStorage(_project, workflow, run);
        foreach (var artifact in plan.Artifacts)
        {
            try { storage.ReadArtifact(plan.Result, artifact); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { throw Fault(MaterializationProblem.InputUnavailable, $"Attempt {plan.Attempt.Value:D}, result {plan.Result.Value:D}, path {artifact.StoredPath}: {error.Message}"); }
        }
    }

    private void ResolvePublicationBlocks(CoordinatorPermit permit, OperationId operation, OperationId plan)
    {
        var workflow = permit.Workflow;
        var run = permit.Run;
        var record = Read(workflow, run);
        var accepted = record.Receipts.Values.Single(entry => entry.Event is RunEvent.ResultAccepted { Result.Origin: ResultOrigin.Executed e } &&
            record.Plans[plan] is MaterializationPlan.Publication publication && e.Attempt == publication.Attempt);
        foreach (var pair in record.Blocks.Where(pair => !pair.Value.Resolved && !IsPublicationDrift(pair.Value.Block) && record.Receipts[pair.Key].Sequence < accepted.Sequence &&
            (pair.Value.Block.Operation == plan || pair.Value.Block.Operation == operation)).OrderBy(pair => pair.Key.Value))
            Journal("resolve-" + pair.Key.Value.ToString("D"), () => _store.Record(permit,
                OperationIds.Derive(operation, "resolve-" + pair.Key.Value.ToString("D")), new RunEvent.BlockResolved(pair.Key, "Publication verified.")));
    }

    private Publication PublicationBlock(CoordinatorPermit permit, OperationId operation, string step, MaterializationBlock block)
    {
        var workflow = permit.Workflow;
        var run = permit.Run;
        try
        {
            var plan = Read(workflow, run).Plans.SingleOrDefault(pair => pair.Value is MaterializationPlan.Publication p && p.Attempt == block.Attempt);
            if (plan.Value is MaterializationPlan.Publication) block = block with { Operation = plan.Key };
        }
        catch (Refusal refused) { return new Publication.Rejected(refused.Reason); }
        return Block(permit, operation, step, block) switch
        {
            Preparation.Blocked blocked => new Publication.Blocked(blocked.Block),
            Preparation.Rejected rejected => new Publication.Rejected(rejected.Reason),
            _ => throw new InvalidOperationException(),
        };
    }
}
