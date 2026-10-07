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
            var existing = record.Plans.SingleOrDefault(pair => pair.Value is MaterializationPlan.Publication p && p.Attempt == attempt);
            if (existing.Value is MaterializationPlan.Publication) planId = existing.Key;
            if (record.Results.FirstOrDefault(result => result.Origin is ResultOrigin.Executed e && e.Attempt == attempt) is { } accepted)
            {
                ResolvePublicationBlocks(permit, operation, planId);
                return new Publication.Accepted(accepted);
            }
            if (record.Phase is not (RunPhase.Approved or RunPhase.StopRequested)) return new Publication.Rejected(new(RunProblem.RunStopped));
            step = "quiescence";
            VerifyQuiescence(attempt);
            step = "repository";
            var repository = OpenRepository();
            using var mutation = repository.TakeMutationLock();
            if (mutation is null) return new Publication.Rejected(new(RunProblem.JournalBusy));
            record = Read(workflow, run);
            existing = record.Plans.SingleOrDefault(pair => pair.Value is MaterializationPlan.Publication p && p.Attempt == attempt);
            if (existing.Value is MaterializationPlan.Publication) planId = existing.Key;
            if (record.Results.FirstOrDefault(result => result.Origin is ResultOrigin.Executed e && e.Attempt == attempt) is { } concurrent)
            {
                ResolvePublicationBlocks(permit, operation, planId);
                return new Publication.Accepted(concurrent);
            }
            VerifyRepository(record, repository);
            var checkout = Checkout(repository, prepared.Location.Owner);
            var tracked = Value(repository.TrackedFiles(checkout, ".idp/inputs", ".idp/outbox", ".worktrees"));
            if (!tracked.IsEmpty) throw Fault(MaterializationProblem.DirtyWorktree, "Tracked execution data: " + string.Join(", ", tracked));
            MaterializationPlan.Publication plan;
            if (existing.Value is MaterializationPlan.Publication persisted)
            {
                plan = persisted;
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
                if (!Value(repository.UnmergedEntries(checkout)).IsEmpty)
                    throw Fault(MaterializationProblem.DirtyWorktree, "The writer index has unresolved stages.");
                var tip = Value(repository.ReadRef(prepared.Location.Owner.Branch))!.Value;
                VerifyPublicationParent(repository, prepared.Location.AttemptBase, tip);
                step = "capture";
                var capture = Value(Mutate("capture", () => repository.Capture(checkout)));
                if (capture.IndexBefore != capture.IndexAfter)
                    throw Fault(MaterializationProblem.DirtyWorktree, "The writer index changed during capture.");
                step = "report";
                var logged = AttemptEvidence.Read(_store.AttemptFolder(workflow, run, task, attempt), closure.Evidence);
                if (logged.Rejection is { } refused) return new Publication.Rejected(refused);
                if (!AttemptEvidence.Matches(logged, TerminalAttemptOutcome.Succeeded) || logged.Record!.Result is null)
                    return new Publication.Rejected(new(RunProblem.OutcomeMismatch));
                var result = new ResultId(OperationIds.Derive(operation, "result").Value);
                step = "outbox";
                var artifacts = FreezeOutbox(workflow, run, operation, attempt, result, checkout, ref evidence);
                var timestamp = DateTimeOffset.FromUnixTimeSeconds(_clock.GetUtcNow().ToUnixTimeSeconds());
                var recipe = new CommitRecipe(capture.Tree, [tip],
                    $"{definition.Title}\n\nIDP-Run: {run.Value:D}\nIDP-Task: {task.Value:D}\nIDP-Attempt: {attempt.Value:D}\n",
                    "iDevelop <idevelop@localhost>", "iDevelop <idevelop@localhost>", timestamp);
                step = "commit";
                var commit = Value(Mutate("commit", () => repository.CreateCommit(recipe)));
                plan = new(attempt, result, record.CurrentResults.GetValueOrDefault(task)?.Id, tip, capture.IndexBefore, recipe, commit,
                    logged.Record.Result, artifacts);
                step = "plan";
                var plannedOperation = OperationIds.Derive(operation, "plan");
                Journal("plan", () => _store.Record(permit, plannedOperation, new RunEvent.Planned(plan)));
                planId = plannedOperation;
            }
            evidence = [];
            step = "branch";
            RequirePublication(_refs.Publish(permit, operation, planId, "branch", repository,
                new(prepared.Location.Owner.Branch, plan.VerifiedTip, plan.Commit)));
            step = "index";
            var indexIntent = OperationIds.Derive(operation, "index-intent");
            record = Read(workflow, run);
            if (!PublicationObserved(record, planId, new GitMutation.AlignIndex(task, plan.IndexBefore, plan.Recipe.Tree)))
            {
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
            RequirePublication(_refs.Publish(permit, operation, planId, "result", repository,
                new(RunLayout.ResultRef(record.RunKey!, record.TaskKeys[task], attempt), null, plan.Commit)));
            step = "verify";
            VerifyPublicationRefs(Read(workflow, run), repository, prepared, operation, workflow, run, ref evidence);
            VerifyPublication(repository, prepared, plan, workflow, run);
            VerifyQuiescence(attempt);
            step = "accepted";
            var decision = Journal("accepted", () => _store.AcceptPublication(permit, OperationIds.Derive(operation, "accepted"), planId));
            var resultRecord = ((RunEvent.ResultAccepted)DecisionEvent(decision)).Result;
            ResolvePublicationBlocks(permit, operation, planId);
            return new Publication.Accepted(resultRecord);
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

    private void VerifyQuiescence(AttemptId attempt)
    {
        if (_boundary.Inspect(attempt) is not WriterState.Quiescent { Attempt: var owner } || owner != attempt)
            throw Fault(MaterializationProblem.LiveWriter, $"Attempt {attempt.Value:D} has no verified process-tree quiescence.");
    }

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
        foreach (var pair in Read(workflow, run).Blocks.Where(pair => !pair.Value.Resolved &&
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
