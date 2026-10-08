using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

internal sealed partial class Materializer
{
    public RetryReset ResetForRetry(RunLease lease, OperationId operation, OperationId salvagePlan, OperationId confirmation)
    {
        using var authority = lease.Use();
        if (authority is null) return new RetryReset.Rejected(new(RunProblem.TaskBusy));
        if (LeaseProblem(lease) is { } problem) return new RetryReset.Rejected(new(problem));
        var permit = lease.Permit;
        var workflow = permit.Workflow;
        var run = permit.Run;
        var task = lease.Task;
        AttemptId? attempt = null;
        InputId? inputs = null;
        var step = "retry-preconditions";
        try
        {
            if (confirmation.Value == Guid.Empty) return new RetryReset.Rejected(new(RunProblem.ConfirmationRequired));
            var record = Read(workflow, run);
            if (record.Phase != RunPhase.Approved) return new RetryReset.Rejected(new(RunProblem.RunStopped));
            if (!record.Salvages.TryGetValue(salvagePlan, out var retained) ||
                record.Plans.GetValueOrDefault(salvagePlan) is not MaterializationPlan.Salvage salvage)
                return new RetryReset.Rejected(new(RunProblem.InvalidData));
            if (LeaseProblem(lease, salvage.Task) is { } mismatch) return new RetryReset.Rejected(new(mismatch));
            attempt = salvage.Attempt;
            var prepared = record.Preparations[new(salvage.Attempt, 1)];
            inputs = prepared.Inputs;
            if (record.UnresolvedClaims.Any(key => record.Preparations[key].Location.Owner == prepared.Location.Owner))
                return new RetryReset.Rejected(new(RunProblem.UnresolvedOwnership));
            var repository = OpenRepository();
            using var mutation = repository.TakeMutationLock();
            if (mutation is null) return new RetryReset.Rejected(new(RunProblem.JournalBusy));
            record = Read(workflow, run);
            if (record.UnresolvedClaims.Any(key => record.Preparations[key].Location.Owner == prepared.Location.Owner))
                return new RetryReset.Rejected(new(RunProblem.UnresolvedOwnership));
            if (record.Phase != RunPhase.Approved) return new RetryReset.Rejected(new(RunProblem.RunStopped));
            VerifyRepository(record, repository);
            VerifyOwnedCheckout(repository, prepared.Location, record);
            if (Value(repository.ReadRef(retained.Ref)) != retained.Commit ||
                Value(repository.CreateCommit(salvage.Recipe)) != retained.Commit)
                throw Fault(MaterializationProblem.UncertainOwnership, "The retained salvage ref or commit differs from its recipe.", new BlockScope.Refs([salvage.Ref]));
            var checkout = Checkout(repository, prepared.Location.Owner);
            var planId = OperationIds.Derive(operation, "retry-plan");
            if (record.Plans.TryGetValue(planId, out var existing) &&
                (existing is not MaterializationPlan.RetryReset old || old.SalvagePlan != salvagePlan))
                return new RetryReset.Rejected(new(RunProblem.OperationConflict));
            MaterializationPlan.RetryReset plan;
            if (existing is MaterializationPlan.RetryReset persisted) plan = persisted;
            else
            {
                step = "retry-inventory";
                VerifyRetryInventory(repository, checkout, salvage, salvage.Recipe.Tree);
                var target = record.Results.LastOrDefault(result => result.Task == task && result.Code is CodeOutput.Produced)?.Code is CodeOutput.Produced produced
                    ? produced.Code.Commit
                    : record.Preparations.Values.Where(p => p.Location.Owner.Task == task).OrderBy(p => record.Receipts.Values.Single(entry =>
                        entry.Event is RunEvent.Prepared e && e.Execution.Launch == p.Launch).Sequence).First().Location.AttemptBase;
                plan = new(task, salvage.Attempt, salvagePlan, salvage.BranchTip, target, salvage.Untracked);
                step = "retry-plan";
                Journal("retry-plan", () => _store.Record(permit, planId, new RunEvent.Planned(plan)));
            }
            if (!RefOwnership.Accepts(Read(workflow, run), repository, prepared.Location.Owner.Branch,
                Value(repository.ReadRef(prepared.Location.Owner.Branch))))
                throw Fault(MaterializationProblem.UncertainOwnership, "The retry branch moved outside the recorded reset.", new BlockScope.Checkout([], Branch: true));
            var reset = new GitMutation.ResetCheckout(task, plan.To);
            if (!PublicationObserved(Read(workflow, run), planId, reset))
            {
                step = "retry-inventory";
                record = Read(workflow, run);
                var intended = OperationIds.Derive(operation, "retry-reset-intent");
                var tip = Value(repository.ReadRef(prepared.Location.Owner.Branch));
                var targetTree = Value(repository.ReadCommit(plan.To)).Tree;
                var targetPaths = Value(repository.TreeFiles(plan.To));
                var remaining = salvage.Untracked.Where(file => !targetPaths.Contains(file.RelativePath)).ToImmutableArray();
                var capture = Value(Mutate("retry-resume-capture", () => repository.Capture(checkout, [.. remaining.Select(file => file.RelativePath)])));
                var adopted = record.GitIntents.ContainsKey(intended) && tip == plan.To && capture.Tree == targetTree &&
                    RunReducer.Same(Untracked(repository, checkout), remaining) && capture.IndexBefore == capture.IndexAfter &&
                    Value(repository.ReadIndexTree(checkout)) == targetTree && Value(repository.SymbolicHead(checkout)) == prepared.Location.Owner.Branch;
                if (!adopted)
                {
                    if (tip != plan.From && !(record.GitIntents.ContainsKey(intended) && tip == plan.To))
                        throw Fault(MaterializationProblem.UncertainOwnership, "The retry branch moved outside the recorded reset.", new BlockScope.Checkout([], Branch: true));
                    VerifyRetryInventory(repository, checkout, salvage, salvage.Recipe.Tree);
                    VerifyIgnoredObstructions(repository, checkout, targetPaths);
                }
                step = "retry-reset";
                Journal("retry-reset-intent", () => _store.Record(permit, intended, new RunEvent.GitIntended(planId, reset)));
                if (!adopted)
                {
                    var attach = new GitMutation.AttachHead(task, prepared.Location.Owner.Branch);
                    if (Value(repository.Worktrees()).Any(worktree => worktree.Branch == attach.Branch && !SamePath(worktree.Path, checkout)))
                        throw Fault(MaterializationProblem.UncertainOwnership, "The task branch is registered at another checkout.", new BlockScope.Ownership());
                    var attachObserved = PublicationObserved(Read(workflow, run), planId, attach);
                    var head = Value(repository.SymbolicHead(checkout));
                    if (head != attach.Branch && (attachObserved ||
                        Value(repository.ResolveCheckoutHead(checkout)) != salvage.ObservedTip))
                        throw Fault(MaterializationProblem.UncertainOwnership, "The retry HEAD moved outside the recorded attachment.", new BlockScope.Checkout([], Head: true));
                    RequirePublication(_refs.Publish(permit, operation, planId, "retry-branch", repository,
                        new(prepared.Location.Owner.Branch, plan.From, plan.To)), new BlockScope.Checkout([], Branch: true));
                    if (!attachObserved)
                    {
                        var attachIntent = OperationIds.Derive(operation, "retry-attach-intent");
                        Journal("retry-attach-intent", () => _store.Record(permit, attachIntent, new RunEvent.GitIntended(planId, attach)));
                        var attached = Mutate("retry-attach", () =>
                        {
                            if (Value(repository.SymbolicHead(checkout)) != attach.Branch &&
                                Value(repository.ResolveCheckoutHead(checkout)) != salvage.ObservedTip)
                                throw Fault(MaterializationProblem.UncertainOwnership, "The retry HEAD moved outside the recorded attachment.", new BlockScope.Checkout([], Head: true));
                            return repository.AttachHead(checkout, attach.Branch);
                        });
                        if (attached.ExitCode != 0) throw Fault(MaterializationProblem.GitFailed, attached.Stderr, new BlockScope.Checkout([], Head: true));
                        Journal("retry-attach-observed", () => _store.Record(permit, OperationIds.Derive(operation, "retry-attach-observed"),
                            new RunEvent.GitObserved(attachIntent, new(false, attach.Branch))));
                    }
                    var result = Mutate("retry-reset", () =>
                    {
                        VerifyResetHead(repository, checkout, prepared.Location.Owner.Branch, plan.To);
                        return repository.ResetCheckout(checkout, plan.To);
                    });
                    if (result.ExitCode != 0) throw Fault(MaterializationProblem.GitFailed, result.Stderr, BlockScope.Checkout.Whole);
                }
                Journal("retry-reset-observed", () => _store.Record(permit, OperationIds.Derive(operation, "retry-reset-observed"),
                    new RunEvent.GitObserved(intended, new(adopted, plan.To.Hex))));
            }
            VerifyResetHead(repository, checkout, prepared.Location.Owner.Branch, plan.To);
            var remove = new GitMutation.RemovePaths(task, plan.Remove);
            if (!PublicationObserved(Read(workflow, run), planId, remove))
            {
                step = "retry-remove";
                var intended = OperationIds.Derive(operation, "retry-remove-intent");
                Journal("retry-remove-intent", () => _store.Record(permit, intended, new RunEvent.GitIntended(planId, remove)));
                foreach (var file in plan.Remove)
                {
                    // A path restored as tracked target content belongs to the retry base, not to cleanup.
                    if (Value(repository.TrackedFiles(checkout, file.RelativePath)).Contains(file.RelativePath)) continue;
                    var removed = Mutate("retry-remove-" + file.RelativePath, () =>
                    {
                        VerifyResetHead(repository, checkout, prepared.Location.Owner.Branch, plan.To);
                        return repository.RemovePath(checkout, file.RelativePath, file.Content, GitPathType.RegularFile);
                    });
                    if (removed == PathRemoval.Unexpected) throw Fault(MaterializationProblem.DirtyWorktree, "A salvaged path changed before removal: " + file.RelativePath, new BlockScope.Checkout([file.RelativePath]));
                }
                Journal("retry-remove-observed", () => _store.Record(permit, OperationIds.Derive(operation, "retry-remove-observed"),
                    new RunEvent.GitObserved(intended, new(false, null))));
            }
            ResolveMaintenanceBlocks(permit, operation, "Retry reset verified.");
            return new RetryReset.Reset(plan.To);
        }
        catch (Refusal refused) { return new RetryReset.Rejected(refused.Reason); }
        catch (MaterializationFailure failed) { return RetryBlock(permit, operation, step, new(operation, task, attempt, failed.Problem, inputs, [], failed.Message) { Scope = failed.Scope }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return RetryBlock(permit, operation, step, new(operation, task, attempt, MaterializationProblem.InputUnavailable, inputs, [], error.Message) { Scope = new BlockScope.Operation() }); }
    }

    private static void VerifyResetHead(GitRepository repository, string checkout, string branch, CommitId target)
    {
        if (Value(repository.SymbolicHead(checkout)) != branch || Value(repository.ReadRef(branch)) != target)
            throw Fault(MaterializationProblem.UncertainOwnership, "The reset HEAD or task branch differs from its recorded target.", new BlockScope.Checkout([], Branch: true, Head: true));
    }

    private void VerifyRetryInventory(GitRepository repository, string checkout, MaterializationPlan.Salvage salvage, TreeId tree)
    {
        var capture = Value(Mutate("retry-capture", () => repository.Capture(checkout)));
        var preserved = salvage.Preserved;
        var indexBytes = preserved is null ? null : Value(repository.IndexBytes(checkout));
        var indexTree = preserved is null ? (TreeId?)null : Value(repository.WriteTree(indexBytes is null ? [] : GitRepository.ParseIndex(indexBytes)));
        if (capture.Tree != (preserved?.Files ?? tree) || capture.IndexBefore != (preserved is null ? salvage.IndexBefore : preserved.Index?.Content) ||
            capture.IndexBefore != capture.IndexAfter || preserved is not null &&
                (indexTree != preserved.IndexTree || (indexBytes is null ? (Digest?)null : Revision.Hash(indexBytes)) != preserved.Index?.Content) ||
            !RunReducer.Same(Untracked(repository, checkout), preserved?.Untracked ?? salvage.Untracked))
            throw Fault(MaterializationProblem.DirtyWorktree, "The tracked or nonignored untracked inventory differs from retained salvage.", BlockScope.Checkout.Whole);
    }

    private static void VerifyIgnoredObstructions(GitRepository repository, string checkout, ImmutableArray<string> targetPaths)
    {
        var ignored = Value(repository.IgnoredFiles(checkout));
        var obstructed = targetPaths.Where(path => ignored.Any(file => path == file ||
            path.StartsWith(file + "/", StringComparison.Ordinal) || file.StartsWith(path + "/", StringComparison.Ordinal))).ToImmutableArray();
        if (!obstructed.IsEmpty)
            throw Fault(MaterializationProblem.DirtyWorktree, "Ignored files obstruct the reset target and must be preserved.", new BlockScope.Checkout(obstructed));
    }

    private RetryReset RetryBlock(CoordinatorPermit permit, OperationId operation, string step, MaterializationBlock block) =>
        Block(permit, operation, step, block) switch
        {
            Preparation.Blocked blocked => new RetryReset.Blocked(blocked.Block),
            Preparation.Rejected rejected => new RetryReset.Rejected(rejected.Reason),
            _ => throw new InvalidOperationException(),
        };
}
