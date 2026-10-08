using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

internal sealed partial class Materializer
{
    public Salvage Salvage(RunLease lease, OperationId operation, AttemptId attempt)
    {
        using var authority = lease.Use();
        if (authority is null) return new Salvage.Rejected(new(RunProblem.TaskBusy));
        if (LeaseProblem(lease) is { } problem) return new Salvage.Rejected(new(problem));
        var permit = lease.Permit;
        var workflow = permit.Workflow;
        var run = permit.Run;
        var task = lease.Task;
        InputId? inputs = null;
        var step = "salvage-preconditions";
        ImmutableArray<EvidenceFile> evidence = [];
        try
        {
            var record = Read(workflow, run);
            if (!record.Attempts.TryGetValue(attempt, out var writer)) return new Salvage.Rejected(new(RunProblem.UnknownAttempt));
            if (LeaseProblem(lease, writer.Task) is { } mismatch) return new Salvage.Rejected(new(mismatch));
            if (!record.Preparations.TryGetValue(new(attempt, 1), out var prepared)) return new Salvage.Rejected(new(RunProblem.InvalidClaim));
            inputs = prepared.Inputs;
            if (SalvageOwnershipUnresolved(record, attempt)) return new Salvage.Rejected(new(RunProblem.UnresolvedOwnership));
            var repository = OpenRepository();
            using var mutation = repository.TakeMutationLock();
            if (mutation is null) return new Salvage.Rejected(new(RunProblem.JournalBusy));
            record = Read(workflow, run);
            if (SalvageOwnershipUnresolved(record, attempt)) return new Salvage.Rejected(new(RunProblem.UnresolvedOwnership));
            VerifyRepository(record, repository);
            VerifyOwnedCheckout(repository, prepared.Location, record);
            var planId = OperationIds.Derive(operation, "salvage-plan");
            if (record.Plans.TryGetValue(planId, out var existing) && (existing is not MaterializationPlan.Salvage old || old.Attempt != attempt))
                return new Salvage.Rejected(new(RunProblem.OperationConflict));
            if (record.Salvages.TryGetValue(planId, out var receipt))
            {
                ResolveMaintenanceBlocks(permit, operation, "Salvage retained.");
                return new Salvage.Retained(receipt, receipt.Commit);
            }
            var checkout = Checkout(repository, prepared.Location.Owner);
            step = "salvage-index-lock";
            VerifyWriterIndexLock(repository, checkout, permit, operation, ref evidence);
            MaterializationPlan.Salvage plan;
            if (existing is MaterializationPlan.Salvage persisted)
            {
                plan = persisted;
                step = "salvage-commit";
                if (Value(Mutate("salvage-commit", () => repository.CreateCommit(plan.Recipe))) != plan.Commit)
                    throw Fault(MaterializationProblem.UncertainOwnership, "The persisted salvage recipe produced a different commit.");
            }
            else
            {
                var tip = Value(repository.Worktrees()).Single(worktree => SamePath(worktree.Path, checkout)).Head ??
                    throw Fault(MaterializationProblem.UncertainOwnership, "The checkout HEAD is absent.");
                step = "salvage-capture";
                var capture = Value(Mutate("salvage-capture", () => repository.Capture(checkout)));
                if (capture.IndexBefore != capture.IndexAfter) throw Fault(MaterializationProblem.DirtyWorktree, "The index changed during salvage capture.");
                var untracked = Untracked(repository, checkout);
                var title = record.Revisions[writer.Revision].Snapshot.Tasks[task].Title;
                var branchTip = Value(repository.ReadRef(prepared.Location.Owner.Branch));
                var parents = (branchTip is { } branch ? repository.IsAncestor(branch, tip) : null) switch
                {
                    null or GitAncestry.Yes => ImmutableArray.Create(tip),
                    GitAncestry.No => ImmutableArray.Create(tip, branchTip!.Value),
                    GitAncestry.Failed failed => throw Fault(MaterializationProblem.GitFailed, failed.Detail),
                    _ => throw new InvalidOperationException(),
                };
                var recipe = new CommitRecipe(capture.Tree, parents,
                    $"Salvage {title}\n\nIDP-Run: {run.Value:D}\nIDP-Task: {task.Value:D}\nIDP-Attempt: {attempt.Value:D}\n",
                    "iDevelop <idevelop@localhost>", "iDevelop <idevelop@localhost>",
                    DateTimeOffset.FromUnixTimeSeconds(_clock.GetUtcNow().ToUnixTimeSeconds()));
                step = "salvage-commit";
                var commit = Value(Mutate("salvage-commit", () => repository.CreateCommit(recipe)));
                var reference = RunLayout.SalvageRef(record.RunKey!, record.TaskKeys[task], attempt);
                if (Value(repository.ReadRef(reference)) is not null)
                    reference = RunLayout.ResalvageRef(record.RunKey!, record.TaskKeys[task], attempt, operation);
                plan = new(task, attempt, tip, branchTip, capture.IndexBefore, recipe, commit, untracked, reference);
                step = "salvage-plan";
                Journal("salvage-plan", () => _store.Record(permit, planId, new RunEvent.Planned(plan)));
            }
            step = "salvage-ref";
            RequirePublication(_refs.Publish(permit, operation, planId, "salvage-ref", repository, new(plan.Ref, null, plan.Commit)));
            if (Value(repository.ReadRef(plan.Ref)) != plan.Commit)
                throw Fault(MaterializationProblem.UncertainOwnership, "The salvage retention ref changed.");
            step = "salvage-retained";
            var retained = (RunEvent.SalvageRetained)DecisionEvent(Journal("salvage-retained", () => _store.Record(permit,
                OperationIds.Derive(operation, "salvage-retained"), new RunEvent.SalvageRetained(planId, plan.Ref, plan.Commit))));
            ResolveMaintenanceBlocks(permit, operation, "Salvage retained.");
            return new Salvage.Retained(retained, retained.Commit);
        }
        catch (Refusal refused) { return new Salvage.Rejected(refused.Reason); }
        catch (MaterializationFailure failed) { return SalvageBlock(permit, operation, step, new(operation, task, attempt, failed.Problem, inputs, evidence, failed.Message)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return SalvageBlock(permit, operation, step, new(operation, task, attempt, MaterializationProblem.InputUnavailable, inputs, evidence, error.Message)); }
    }

    private static bool SalvageOwnershipUnresolved(RunRecord record, AttemptId attempt) =>
        record.UnresolvedClaims.Any(key => key.Attempt == attempt && !record.RootExits.ContainsKey(key) && !record.Fenced.Contains(key));

    private static ImmutableArray<EvidenceFile> Untracked(GitRepository repository, string checkout) =>
        [.. Value(repository.UntrackedFiles(checkout)).Where(path => path != ".idp" && !path.StartsWith(".idp/", StringComparison.Ordinal) &&
            path != ".worktrees" && !path.StartsWith(".worktrees/", StringComparison.Ordinal)).Order(StringComparer.Ordinal).Select(relative =>
        {
            var path = RunStorage.SafePath(checkout, relative);
            RegularFile.Verify(path);
            var bytes = File.ReadAllBytes(path);
            return new EvidenceFile(relative, Revision.Hash(bytes), bytes.LongLength);
        })];

    private void ResolveMaintenanceBlocks(CoordinatorPermit permit, OperationId operation, string reason)
    {
        var workflow = permit.Workflow;
        var run = permit.Run;
        foreach (var pair in Read(workflow, run).Blocks.Where(pair => !pair.Value.Resolved && pair.Value.Block.Operation == operation)
            .OrderBy(pair => pair.Key.Value))
            Journal("maintenance-resolve-" + pair.Key.Value.ToString("D"), () => _store.Record(permit,
                OperationIds.Derive(operation, "maintenance-resolve-" + pair.Key.Value.ToString("D")), new RunEvent.BlockResolved(pair.Key, reason)));
    }

    private Salvage SalvageBlock(CoordinatorPermit permit, OperationId operation, string step, MaterializationBlock block) =>
        Block(permit, operation, step, block) switch
        {
            Preparation.Blocked blocked => new Salvage.Blocked(blocked.Block),
            Preparation.Rejected rejected => new Salvage.Rejected(rejected.Reason),
            _ => throw new InvalidOperationException(),
        };
}
