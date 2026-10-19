using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

internal sealed partial class Materializer
{
    public async ValueTask<Salvage> Salvage(RunLease lease, OperationId operation, AttemptId attempt, CancellationToken cancellation = default)
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
            using var mutation = MutationLock(repository);
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
            if (record.PreservationDivergences.TryGetValue(operation, out var divergence))
                return SalvageBlock(permit, operation, "salvage-diverged", DivergenceBlock(operation, task, attempt, inputs, divergence));
            var checkout = Checkout(repository, prepared.Location.Owner);
            MaterializationPlan.Salvage plan;
            if (existing is MaterializationPlan.Salvage persisted)
            {
                plan = persisted;
                step = "salvage-commit";
                if (Value(Mutate("salvage-commit", () => repository.CreateCommit(plan.Recipe))) != plan.Commit)
                    throw Fault(MaterializationProblem.UncertainOwnership, "The persisted salvage recipe produced a different commit.", new BlockScope.Operation());
            }
            else
            {
                step = "salvage-observe";
                var title = record.Revisions[writer.Revision].Snapshot.Tasks[task].Title;
                var pair = await ObservePreservationPair(repository, record, prepared.Location, attempt, operation, "salvage",
                    PreservationMessage(record, task, attempt, operation, title), cancellation);
                if (pair.Scope is { } scope)
                {
                    step = "salvage-diverged";
                    divergence = RecordPreservationDivergence(permit, operation, "salvage", pair.First, pair.Second, scope);
                    return SalvageBlock(permit, operation, step, DivergenceBlock(operation, task, attempt, inputs, divergence));
                }
                var state = pair.First.State;
                var tip = state.Head!.Value;
                var branchTip = state.Branch;
                var recipe = new CommitRecipe(state.Files, pair.First.Recipe.Parents,
                    $"Salvage {title}\n\nIDP-Run: {run.Value:D}\nIDP-Task: {task.Value:D}\nIDP-Attempt: {attempt.Value:D}\n",
                    "iDevelop <idevelop@localhost>", "iDevelop <idevelop@localhost>",
                    DateTimeOffset.FromUnixTimeSeconds(_clock.GetUtcNow().ToUnixTimeSeconds()));
                step = "salvage-commit";
                var commit = Value(Mutate("salvage-commit", () => repository.CreateCommit(recipe)));
                var reference = RunLayout.SalvageRef(record.RunKey!, record.TaskKeys[task], attempt);
                if (Value(repository.ReadRef(reference)) is not null)
                    reference = RunLayout.ResalvageRef(record.RunKey!, record.TaskKeys[task], attempt, operation);
                plan = new(task, attempt, tip, branchTip, state.Index?.Content, recipe, commit, state.Untracked, reference) { Preserved = state };
                step = "salvage-plan";
                Journal("salvage-plan", () => _store.Record(permit, planId, new RunEvent.Planned(plan)));
            }
            step = "salvage-ref";
            RequirePublication(_refs.Publish(permit, operation, planId, "salvage-ref", repository, new(plan.Ref, null, plan.Commit)), new BlockScope.Refs([plan.Ref]));
            if (Value(repository.ReadRef(plan.Ref)) != plan.Commit)
                throw Fault(MaterializationProblem.UncertainOwnership, "The salvage retention ref changed.", new BlockScope.Refs([plan.Ref]));
            step = "salvage-retained";
            var retained = (RunEvent.SalvageRetained)DecisionEvent(Journal("salvage-retained", () => _store.Record(permit,
                OperationIds.Derive(operation, "salvage-retained"), new RunEvent.SalvageRetained(planId, plan.Ref, plan.Commit))));
            ResolveMaintenanceBlocks(permit, operation, "Salvage retained.");
            return new Salvage.Retained(retained, retained.Commit);
        }
        catch (Refusal refused) { return new Salvage.Rejected(refused.Reason); }
        catch (MaterializationFailure failed) { return SalvageBlock(permit, operation, step, new(operation, task, attempt, failed.Problem, inputs, evidence, failed.Message) { Scope = failed.Scope }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return SalvageBlock(permit, operation, step, new(operation, task, attempt, MaterializationProblem.InputUnavailable, inputs, evidence, error.Message) { Scope = new BlockScope.Operation() }); }
    }

    private static bool SalvageOwnershipUnresolved(RunRecord record, AttemptId attempt) =>
        record.UnresolvedClaims.Any(key => record.Preparations[key].Location.Owner == record.Preparations[new(attempt, 1)].Location.Owner &&
            !record.RootExits.ContainsKey(key) && !record.Fenced.Contains(key));

    private static ImmutableArray<EvidenceFile> Untracked(GitRepository repository, string checkout) =>
        [.. Value(repository.UntrackedFiles(checkout)).Where(path => path != ".idp" && !path.StartsWith(".idp/", StringComparison.Ordinal) &&
            path != ".worktrees" && !path.StartsWith(".worktrees/", StringComparison.Ordinal)).Order(StringComparer.Ordinal).Select(relative =>
        {
            var path = RunStorage.SafePath(checkout, relative);
            RegularFile.Verify(path);
            var bytes = File.ReadAllBytes(path);
            return new EvidenceFile(relative, Revision.Hash(bytes), bytes.LongLength);
        })];

    private static string PreservationMessage(RunRecord record, TaskId task, AttemptId attempt, OperationId operation, string title) =>
        $"Preserve {title}\n\nIDP-Run: {record.Id.Value:D}\nIDP-Task: {task.Value:D}\nIDP-Attempt: {attempt.Value:D}\nIDP-Operation: {operation.Value:D}\n";

    private async ValueTask<(PreservationObservation First, PreservationObservation Second, BlockScope.Checkout? Scope)> ObservePreservationPair(
        GitRepository repository, RunRecord record, ExecutionLocation location, AttemptId attempt, OperationId operation,
        string label, string message, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var first = Mutate(label + "-observe-1", () => ObservePreservation(repository, record, location, attempt, operation, 1, message, null));
        var remaining = first.Completed + TimeSpan.FromMilliseconds(250) - _clock.GetUtcNow();
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, _clock, cancellation);
        cancellation.ThrowIfCancellationRequested();
        var second = Mutate(label + "-observe-2", () => ObservePreservation(repository, record, location, attempt, operation, 2, message, first.Recipe.Timestamp));
        return (first, second, PreservationDifference(repository, attempt, first, second));
    }

    private PreservationObservation ObservePreservation(GitRepository repository, RunRecord record, ExecutionLocation location,
        AttemptId attempt, OperationId operation, int ordinal, string message, DateTimeOffset? timestamp)
    {
        var storage = new RunStorage(_project, record.Workflow, record.Id);
        var destination = $"preservations/{operation.Value:D}/{ordinal}";
        var folder = RunStorage.SafePath(storage.Folder, destination);
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        var started = _clock.GetUtcNow();
        var observation = ReadPreservationCheckout(repository, record, location, attempt, StoreBytes);
        var state = observation.State;
        var stages = observation.Stages;
        var completed = _clock.GetUtcNow();
        var parents = PreservationParents(repository, state.Branch, state.Head);
        var recipe = new CommitRecipe(state.Files, parents, message, "iDevelop <idevelop@localhost>", "iDevelop <idevelop@localhost>",
            timestamp ?? DateTimeOffset.FromUnixTimeSeconds(started.ToUnixTimeSeconds()));
        if (state.IndexTree != state.Files)
            parents = parents.Add(Value(repository.CreateCommit(recipe with { Tree = state.IndexTree!.Value, Parents = [], Message = "Index of " + message })));
        if (!stages.IsEmpty)
        {
            var stageTree = Value(repository.WriteTree(stages.Select(stage => stage with { Stage = 0, Path = $"stages/{stage.Stage}/{stage.Path}" })));
            parents = parents.Add(Value(repository.CreateCommit(recipe with { Tree = stageTree, Parents = [], Message = "Stages of " + message })));
        }
        recipe = recipe with { Parents = parents };
        var commit = Value(repository.CreateCommit(recipe));
        Pin(repository, RunLayout.PreservationPin(record.RunKey!, record.TaskKeys[location.Owner.Task], attempt, operation, ordinal), commit);
        return new(ordinal, started, completed, state,
            recipe, commit, observation.Outbox, stages);

        EvidenceFile StoreBytes(string name, byte[] bytes)
        {
            var file = new EvidenceFile(destination + "/" + name, Revision.Hash(bytes), bytes.LongLength);
            RunStorage.Publish(storage.Folder, file.RelativePath, bytes, file.Content, file.ByteLength);
            return file;
        }
    }

    private static (CheckoutState State, ImmutableArray<ArtifactRecord> Outbox, ImmutableArray<StageEntry> Stages) ReadPreservationCheckout(
        GitRepository repository, RunRecord record, ExecutionLocation location, AttemptId attempt, Func<string, byte[], EvidenceFile> storeBytes)
    {
        VerifyOwnedCheckout(repository, location, record);
        var checkout = Checkout(repository, location.Owner);
        var branch = Value(repository.ReadRef(location.Owner.Branch!));
        var head = Value(repository.ResolveCheckoutHead(checkout));
        var symbolicHead = Value(repository.SymbolicHead(checkout));
        if (File.Exists(Value(repository.IndexPath(checkout)) + ".lock"))
            throw Fault(MaterializationProblem.DirtyWorktree, "An index.lock exists in the writer checkout.", new BlockScope.Checkout([], IndexLock: true));
        var indexBytes = Value(repository.IndexBytes(checkout));
        EvidenceFile? index = indexBytes is { } bytes ? storeBytes("index", bytes) : null;
        var stages = Value(repository.UnmergedEntries(checkout));
        var capture = Value(repository.Capture(checkout));
        if (capture.IndexBefore != capture.IndexAfter || capture.IndexBefore != index?.Content)
            throw Fault(MaterializationProblem.DirtyWorktree, "The index changed during preservation capture.", BlockScope.Checkout.Whole);
        var indexTree = Value(repository.WriteTree(indexBytes is null ? [] : GitRepository.ParseIndex(indexBytes)));
        var untracked = Untracked(repository, checkout);
        var outbox = ImmutableArray.CreateBuilder<ArtifactRecord>();
        var outboxPath = RunStorage.SafePath(checkout, RunLayout.Outbox(attempt));
        if (Directory.Exists(outboxPath))
        {
            foreach (var path in Directory.EnumerateFiles(outboxPath, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(outboxPath, path).Replace(Path.DirectorySeparatorChar, '/')).Order(StringComparer.Ordinal))
            {
                var source = RunStorage.SafePath(outboxPath, path);
                RegularFile.Verify(source);
                var file = storeBytes("outbox/" + path, File.ReadAllBytes(source));
                outbox.Add(new(path, file.RelativePath, file.Content, file.ByteLength));
            }
        }
        return (new(branch, head, symbolicHead, capture.Tree, index, indexTree, untracked, null), outbox.ToImmutable(), stages);
    }

    private static ImmutableArray<CommitId> PreservationParents(GitRepository repository, CommitId? branch, CommitId? head)
    {
        if (head is null) throw Fault(MaterializationProblem.UncertainOwnership, "The checkout HEAD is absent.", new BlockScope.Checkout([], Head: true));
        if (branch is null || branch == head) return [head.Value];
        return repository.IsAncestor(branch.Value, head.Value) switch
        {
            GitAncestry.Yes => [head.Value],
            GitAncestry.No => [head.Value, branch.Value],
            GitAncestry.Failed failed => throw Fault(MaterializationProblem.GitFailed, failed.Detail, new BlockScope.Operation()),
            _ => throw new InvalidOperationException(),
        };
    }

    private static BlockScope.Checkout? PreservationDifference(GitRepository repository, AttemptId attempt,
        PreservationObservation first, PreservationObservation second)
    {
        var a = first.State;
        var b = second.State;
        var branch = a.Branch != b.Branch;
        var head = a.Head != b.Head || a.SymbolicHead != b.SymbolicHead;
        var lockDiffers = a.IndexLock?.Identity != b.IndexLock?.Identity || !SameBytes(a.IndexLock?.Bytes, b.IndexLock?.Bytes);
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        if (a.Files != b.Files) paths.UnionWith(Value(repository.DiffTreePaths(a.Files, b.Files)));
        if (a.IndexTree != b.IndexTree && a.IndexTree is { } ai && b.IndexTree is { } bi)
            paths.UnionWith(Value(repository.DiffTreePaths(ai, bi)));
        AddDifferences(a.Untracked, b.Untracked, file => file.RelativePath, file => (file.Content, file.ByteLength));
        AddDifferences(first.Outbox, second.Outbox, file => RunLayout.Outbox(attempt) + "/" + file.Name, file => (file.Content, file.ByteLength));
        AddDifferences(first.Stages, second.Stages, stage => $"{stage.Stage}/{stage.Path}", stage => (stage.Mode, stage.Object),
            key => key[(key.IndexOf('/') + 1)..]);
        if (!branch && !head && paths.Count == 0 && !lockDiffers && SameBytes(a.Index, b.Index) && a.IndexTree == b.IndexTree && first.Commit == second.Commit)
            return null;
        return new([.. paths], branch, head, lockDiffers);

        void AddDifferences<T, TValue>(ImmutableArray<T> left, ImmutableArray<T> right, Func<T, string> key, Func<T, TValue> value,
            Func<string, string>? path = null)
        {
            var l = left.ToDictionary(key, value, StringComparer.Ordinal);
            var r = right.ToDictionary(key, value, StringComparer.Ordinal);
            foreach (var name in l.Keys.Union(r.Keys, StringComparer.Ordinal))
                if (!l.TryGetValue(name, out var lv) || !r.TryGetValue(name, out var rv) || !EqualityComparer<TValue>.Default.Equals(lv, rv))
                    paths.Add(path is null ? name : path(name));
        }
    }

    private static bool SameBytes(EvidenceFile? first, EvidenceFile? second) =>
        first is null ? second is null : second is not null && first.Content == second.Content && first.ByteLength == second.ByteLength;

    private RunEvent.PreservationDiverged RecordPreservationDivergence(CoordinatorPermit permit, OperationId operation, string label,
        PreservationObservation first, PreservationObservation second, BlockScope.Checkout scope) =>
        (RunEvent.PreservationDiverged)DecisionEvent(Journal(label + "-diverged", () => _store.Record(permit,
            OperationIds.Derive(operation, "preservation-diverged"), new RunEvent.PreservationDiverged(operation, first, second, scope))));

    private static MaterializationBlock DivergenceBlock(OperationId operation, TaskId task, AttemptId attempt, InputId? inputs,
        RunEvent.PreservationDiverged divergence) => new(operation, task, attempt,
            divergence.Scope is { Branch: false, Head: false } ? MaterializationProblem.DirtyWorktree : MaterializationProblem.UncertainOwnership, inputs, [],
            divergence.Scope is { Branch: false, Head: false } ? "The checkout changed between preservation observations." :
                "The branch or HEAD changed between preservation observations.") { Scope = divergence.Scope };

    private void ResolveMaintenanceBlocks(CoordinatorPermit permit, OperationId operation, string reason, Func<BlockScope, bool>? scope = null)
    {
        var workflow = permit.Workflow;
        var run = permit.Run;
        foreach (var pair in Read(workflow, run).Blocks.Where(pair => !pair.Value.Resolved && pair.Value.Block.Operation == operation &&
            (scope is null || scope(pair.Value.Block.Scope)))
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
