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
            var folder = _store.AttemptFolder(workflow, run, task, attempt);
            var logged = AttemptEvidence.Read(folder, closure.Evidence);
            if (!AttemptEvidence.Matches(logged, TerminalAttemptOutcome.Succeeded) || logged.Record?.Result is null)
                return new Publication.Rejected(new(RunProblem.OutcomeMismatch));
            var launch = new LaunchKey(attempt, logged.Record.Turns.Count);
            var captureId = PublicationCapture(record, launch, closure.Evidence, logged.Record.Result, folder);
            var root = record.RootExits[launch];
            var existing = record.Plans.SingleOrDefault(pair => pair.Value is MaterializationPlan.Publication p && p.Attempt == attempt);
            if (existing.Value is MaterializationPlan.Publication) planId = existing.Key;
            if (record.Results.FirstOrDefault(result => result.Origin is ResultOrigin.Executed e && e.Attempt == attempt) is { } accepted)
            {
                ResolvePublicationBlocks(permit, operation, planId);
                return new Publication.Accepted(accepted);
            }
            if (PublicationDrift(record, attempt, operation, planId) is { } drift) return new Publication.Blocked(drift);
            if (record.Phase is not (RunPhase.Approved or RunPhase.StopRequested)) return new Publication.Rejected(new(RunProblem.RunStopped));
            step = "settlement";
            VerifyPublicationDisposition(record, captureId, ref evidence);
            var frozen = record.Captures[captureId][0];
            step = "repository";
            var repository = OpenRepository();
            _probe?.Invoke("publish.lock.before");
            using var mutation = repository.TakeMutationLock();
            if (mutation is null) return new Publication.Rejected(new(RunProblem.JournalBusy));
            record = Read(workflow, run);
            captureId = PublicationCapture(record, launch, closure.Evidence, logged.Record.Result, folder);
            root = record.RootExits[launch];
            existing = record.Plans.SingleOrDefault(pair => pair.Value is MaterializationPlan.Publication p && p.Attempt == attempt);
            if (existing.Value is MaterializationPlan.Publication) planId = existing.Key;
            if (record.Results.FirstOrDefault(result => result.Origin is ResultOrigin.Executed e && e.Attempt == attempt) is { } concurrent)
            {
                ResolvePublicationBlocks(permit, operation, planId);
                return new Publication.Accepted(concurrent);
            }
            if (PublicationDrift(record, attempt, operation, planId) is { } concurrentDrift) return new Publication.Blocked(concurrentDrift);
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
                CopyCarriedArtifacts(workflow, run, record, attempt, frozen, plan.Result);
                step = "ownership";
                VerifyPublicationRefs(record, repository, prepared, operation, workflow, run, ref evidence);
                VerifyCheckout(repository, prepared.Location, keepChanges: true, record);
                VerifyPublicationParent(repository, prepared.Location.AttemptBase, plan.VerifiedTip);
                step = "commit";
                if (Value(Mutate("commit", () => repository.CreateCommit(plan.Recipe))) != plan.Commit)
                    throw Fault(MaterializationProblem.UncertainOwnership, "The persisted publication recipe produced a different commit.", new BlockScope.Operation());
            }
            else
            {
                step = "ownership";
                VerifyPublicationRefs(record, repository, prepared, operation, workflow, run, ref evidence);
                VerifyCheckout(repository, prepared.Location, keepChanges: true, record);
                VerifyPublicationParent(repository, prepared.Location.AttemptBase, root.Tip);
                step = "capture";
                VerifyPublicationBaseline(repository, checkout, frozen.Recipe.Tree, frozen.Index?.Content, true, permit, operation, ref evidence, frozen.IndexTree);
                var result = new ResultId(OperationIds.Derive(operation, "result").Value);
                step = "outbox";
                var artifacts = CopyPublicationArtifacts(workflow, run, frozen, result).AddRange(CopyCarriedArtifacts(workflow, run, record, attempt, frozen, result));
                step = "commit";
                var commit = Value(Mutate("commit", () => repository.CreateCommit(frozen.Recipe)));
                if (commit != frozen.Candidate)
                    throw Fault(MaterializationProblem.UncertainOwnership, "The frozen publication recipe produced a different commit.", new BlockScope.Operation());
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
            RequirePublication(_refs.Publish(permit, operation, planId, "branch", repository, branch.Change), new BlockScope.Checkout([], Branch: true));
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
                if (aligned is IndexAlignment.Unexpected or IndexAlignment.Failed)
                    VerifyWriterIndexLock(repository, checkout, permit, operation, ref evidence);
                if (aligned is IndexAlignment.Unexpected)
                    throw Fault(MaterializationProblem.DirtyWorktree, "The writer index changed before publication alignment.", BlockScope.Checkout.Whole);
                if (aligned is IndexAlignment.Failed failed)
                    throw Fault(Value(repository.Capture(checkout)).Tree != plan.Recipe.Tree ? MaterializationProblem.DirtyWorktree : MaterializationProblem.GitFailed,
                        failed.Detail, BlockScope.Checkout.Whole);
                Journal("index-observed", () => _store.Record(permit, OperationIds.Derive(operation, "index-observed"),
                    new RunEvent.GitObserved(indexIntent, new(aligned is IndexAlignment.AlreadyAligned, plan.Recipe.Tree.Hex))));
            }
            step = "result-ref";
            var resultRef = new GitMutation.MoveRef(new(RunLayout.ResultRef(record.RunKey!, record.TaskKeys[task], attempt), null, plan.Commit));
            VerifyPendingPublicationMove(resultRef);
            RequirePublication(_refs.Publish(permit, operation, planId, "result", repository, resultRef.Change), new BlockScope.Refs([resultRef.Change.Ref]));
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
                VerifyPublicationBaseline(repository, checkout, plan.Recipe.Tree, plan.IndexBefore, compareIndex, permit, operation, ref evidence, frozen.IndexTree);
                if (PublicationObserved(current, planId, new GitMutation.AlignIndex(task, plan.IndexBefore, plan.Recipe.Tree)) &&
                    (repository.ReadIndexTree(checkout) is not GitRead<TreeId>.Read index || index.Value != plan.Recipe.Tree))
                    throw Fault(MaterializationProblem.DirtyWorktree, "Writer files or index changed after the turn-end capture.",
                        new BlockScope.Checkout(repository.ReadIndexTree(checkout) is GitRead<TreeId>.Read actual ? Value(repository.DiffTreePaths(actual.Value, plan.Recipe.Tree)) : []));
            }
        }
        catch (Refusal refused) { return new Publication.Rejected(refused.Reason); }
        catch (MaterializationFailure failed)
        { return PublicationBlock(permit, operation, step, new(planId, task, attempt, failed.Problem, inputs, evidence, failed.Message) { Scope = failed.Scope }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return PublicationBlock(permit, operation, step, new(planId, task, attempt, MaterializationProblem.InputUnavailable, inputs, evidence, error.Message) { Scope = new BlockScope.Operation() }); }
    }

    private static void VerifyPublicationParent(GitRepository repository, CommitId attemptBase, CommitId tip)
    {
        switch (repository.IsAncestor(attemptBase, tip))
        {
            case GitAncestry.No:
                throw Fault(MaterializationProblem.UncertainOwnership, $"The writer branch tip {tip.Hex} does not contain the attempt base {attemptBase.Hex}.", new BlockScope.Checkout([], Branch: true));
            case GitAncestry.Failed failed:
                throw Fault(MaterializationProblem.GitFailed, failed.Detail, new BlockScope.Operation());
        }
    }

    // The captures froze the turn's log. The closure may add exactly one Mark done line, which uses that final capture.
    private static CaptureId PublicationCapture(RunRecord record, LaunchKey launch, LogCheckpoint closure, string report, string folder)
    {
        if (!record.Claims.ContainsKey(launch) || !record.RootExits.ContainsKey(launch) || !record.TurnClosures.TryGetValue(launch, out var turn) ||
            !AttemptEvidence.Extends(folder, turn, closure) ||
            !record.Settlements.TryGetValue(launch, out var capture) || !record.Dispositions.ContainsKey(capture) ||
            !record.Captures.TryGetValue(capture, out var observations) || observations.Any(o => o.Launch != launch || o.Log != turn || o.Report != report))
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
                        throw Fault(MaterializationProblem.InputUnavailable, "The shared-ref snapshot is absent.", new BlockScope.Operation())).ToArray();
                    var detail = diverged.Refs.Select(name => $"{name}: prepared {Tip(snapshots[0], name)}, " +
                        $"observation 1 {Tip(snapshots[1], name)}, observation 2 {Tip(snapshots[2], name)}");
                    throw Fault(diverged.Problem, diverged.Detail + " Paths or refs: " + string.Join("; ", detail), DivergedScope(record, observations[0].Launch, diverged));
                }
                throw Fault(diverged.Problem, diverged.Detail + " Paths or refs: " + string.Join(", ", diverged.Paths.Concat(diverged.Refs)),
                    DivergedScope(record, record.Captures[capture][0].Launch, diverged));
            case CaptureDisposition.Failed failed:
                evidence = failed.Evidence;
                throw Fault(failed.Problem, failed.Detail, new BlockScope.Operation());
        }
    }

    private static BlockScope DivergedScope(RunRecord record, LaunchKey launch, CaptureDisposition.Diverged diverged)
    {
        var branch = record.Preparations[launch].Location.Owner.Branch;
        return diverged.Refs.All(name => name == branch || name == "HEAD")
            ? new BlockScope.Checkout(diverged.Paths, diverged.Refs.Contains(branch), diverged.Refs.Contains("HEAD"))
            : new BlockScope.Refs(diverged.Refs);
    }

    private static string Tip(IReadOnlyDictionary<string, CommitId> refs, string name) =>
        refs.TryGetValue(name, out var tip) ? tip.Hex : "absent";

    private void VerifyPublicationBaseline(GitRepository repository, string checkout, TreeId tree, Digest? index,
        bool compareIndex, CoordinatorPermit permit, OperationId operation, ref ImmutableArray<EvidenceFile> evidence, TreeId? indexTree = null)
    {
        VerifyWriterIndexLock(repository, checkout, permit, operation, ref evidence);
        if (repository.UnmergedEntries(checkout) is not GitRead<ImmutableArray<StageEntry>>.Read stages)
            throw Fault(MaterializationProblem.DirtyWorktree, "Writer files or index changed after the turn-end capture.", BlockScope.Checkout.Whole);
        if (!stages.Value.IsEmpty)
            throw Fault(MaterializationProblem.DirtyWorktree, "The writer index has unresolved stages.", new BlockScope.Checkout([.. stages.Value.Select(s => s.Path).Distinct().Order(StringComparer.Ordinal)]));
        var tracked = Value(repository.TrackedFiles(checkout, ".idp/inputs", ".idp/outbox", ".worktrees"));
        if (!tracked.IsEmpty) throw Fault(MaterializationProblem.DirtyWorktree, "Tracked execution data: " + string.Join(", ", tracked), new BlockScope.Checkout(tracked));
        var live = Value(Mutate("capture", () => repository.Capture(checkout)));
        if (live.Tree != tree || compareIndex && (live.IndexBefore != index || live.IndexAfter != index))
        {
            var paths = new SortedSet<string>(Value(repository.DiffTreePaths(live.Tree, tree)), StringComparer.Ordinal);
            if (compareIndex && indexTree is { } expectedIndex && repository.ReadIndexTree(checkout) is GitRead<TreeId>.Read actualIndex)
                paths.UnionWith(Value(repository.DiffTreePaths(actualIndex.Value, expectedIndex)));
            throw Fault(MaterializationProblem.DirtyWorktree, "Writer files or index changed after the turn-end capture.", new BlockScope.Checkout([.. paths]));
        }
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
                    $"Attempt {frozen.Launch.Attempt.Value:D}, capture path {artifact.StoredPath}, result path {destination}: {error.Message}", new BlockScope.Operation());
            }
            _probe?.Invoke("artifact." + artifact.Name + ".after");
            return artifact with { StoredPath = destination };
        })];
    }

    /// <summary>Copies the prior result's artifacts that a review fix keeps to <paramref name="result"/>, and returns them.</summary>
    private ImmutableArray<ArtifactRecord> CopyCarriedArtifacts(WorkflowId workflow, RunId run, RunRecord record, AttemptId attempt,
        CaptureObservation frozen, ResultId result)
    {
        var storage = new RunStorage(_project, workflow, run);
        var prior = RunReducer.PriorResult(record, attempt);
        var carried = RunReducer.CarriedArtifacts(record, attempt, frozen.Artifacts, result);
        foreach (var artifact in carried)
        {
            var source = prior!.Artifacts.First(stored => stored.Name == artifact.Name);
            try { RunStorage.Publish(storage.Folder, artifact.StoredPath, storage.ReadArtifact(prior.Id, source), artifact.Content, artifact.ByteLength); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw Fault(MaterializationProblem.InputUnavailable,
                    $"Attempt {attempt.Value:D}, kept artifact {source.StoredPath}, result path {artifact.StoredPath}: {error.Message}", new BlockScope.Operation());
            }
        }
        return carried;
    }

    // Publish reruns its own evidence and repository checks, so only drift elsewhere, or another operation's fault there, stops it.
    private static bool IsPublicationDrift(MaterializationBlock block, OperationId operation, OperationId plan) =>
        block.Problem is MaterializationProblem.DirtyWorktree or MaterializationProblem.UncertainOwnership &&
        (block.Scope is not (BlockScope.Operation or BlockScope.Repository) || block.Operation != operation && block.Operation != plan);

    private static MaterializationBlock? PublicationDrift(RunRecord record, AttemptId attempt, OperationId operation, OperationId plan) =>
        record.Blocks.Where(pair => !pair.Value.Resolved && pair.Value.Block.Attempt == attempt && IsPublicationDrift(pair.Value.Block, operation, plan))
            .OrderBy(pair => record.Receipts[pair.Key].Sequence).Select(pair => pair.Value.Block).FirstOrDefault();

    private static void RequirePublication(RefPublication outcome, BlockScope scope)
    {
        if (outcome is RefPublication.Rejected rejected) throw new Refusal(rejected.Reason);
        if (outcome is RefPublication.Blocked blocked) throw Fault(blocked.Problem, blocked.Detail, scope);
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
        if (Value(repository.ReadRef(prepared.Location.Owner.Branch)) != plan.Commit)
            throw Fault(MaterializationProblem.UncertainOwnership, "Publication branch or result ref changed.", new BlockScope.Checkout([], Branch: true));
        var resultRef = RunLayout.ResultRef(record.RunKey!, record.TaskKeys[prepared.Location.Owner.Task], plan.Attempt);
        if (Value(repository.ReadRef(resultRef)) != plan.Commit)
            throw Fault(MaterializationProblem.UncertainOwnership, "Publication branch or result ref changed.", new BlockScope.Refs([resultRef]));
        if (capture is not GitRead<GitCapture>.Read captured || captured.Value.Tree != plan.Recipe.Tree ||
            Value(repository.Status(checkout)).Length != 0 ||
            repository.ReadIndexTree(checkout) is not GitRead<TreeId>.Read index || index.Value != plan.Recipe.Tree)
            throw Fault(MaterializationProblem.DirtyWorktree, "Writer files or index changed after the publication capture.", BlockScope.Checkout.Whole);
        var storage = new RunStorage(_project, workflow, run);
        foreach (var artifact in plan.Artifacts)
        {
            try { storage.ReadArtifact(plan.Result, artifact); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { throw Fault(MaterializationProblem.InputUnavailable, $"Attempt {plan.Attempt.Value:D}, result {plan.Result.Value:D}, path {artifact.StoredPath}: {error.Message}", new BlockScope.Operation()); }
        }
    }

    private void ResolvePublicationBlocks(CoordinatorPermit permit, OperationId operation, OperationId plan)
    {
        var workflow = permit.Workflow;
        var run = permit.Run;
        var record = Read(workflow, run);
        var accepted = record.Receipts.Values.Single(entry => entry.Event is RunEvent.ResultAccepted { Result.Origin: ResultOrigin.Executed e } &&
            record.Plans[plan] is MaterializationPlan.Publication publication && e.Attempt == publication.Attempt);
        foreach (var pair in record.Blocks.Where(pair => !pair.Value.Resolved && !IsPublicationDrift(pair.Value.Block, operation, plan) && record.Receipts[pair.Key].Sequence < accepted.Sequence &&
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
