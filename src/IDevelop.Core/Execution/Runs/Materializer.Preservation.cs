using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

internal sealed partial class Materializer
{
    public async ValueTask<Preservation> Preserve(RunLease lease, OperationId operation, AttemptId attempt,
        CancellationToken cancellation = default)
    {
        using var authority = lease.Use();
        if (authority is null) return new Preservation.Rejected(new(RunProblem.TaskBusy));
        if (LeaseProblem(lease) is { } problem) return new Preservation.Rejected(new(problem));
        var permit = lease.Permit;
        var task = lease.Task;
        InputId? inputs = null;
        var step = "preserve-preconditions";
        try
        {
            var record = Read(permit.Workflow, permit.Run);
            if (!record.Attempts.TryGetValue(attempt, out var writer)) return new Preservation.Rejected(new(RunProblem.UnknownAttempt));
            if (LeaseProblem(lease, writer.Task) is { } mismatch) return new Preservation.Rejected(new(mismatch));
            if (!record.Preparations.TryGetValue(new(attempt, 1), out var prepared)) return new Preservation.Rejected(new(RunProblem.InvalidClaim));
            inputs = prepared.Inputs;
            if (SalvageOwnershipUnresolved(record, attempt)) return new Preservation.Rejected(new(RunProblem.UnresolvedOwnership));
            var repository = OpenRepository();
            using var mutation = repository.TakeMutationLock(MutationPatience, Halted);
            if (mutation is null) return new Preservation.Rejected(new(RunProblem.JournalBusy));
            record = Read(permit.Workflow, permit.Run);
            if (SalvageOwnershipUnresolved(record, attempt)) return new Preservation.Rejected(new(RunProblem.UnresolvedOwnership));
            VerifyRepository(record, repository);
            VerifyOwnedCheckout(repository, prepared.Location, record);
            var planId = OperationIds.Derive(operation, "preserve-plan");
            if (record.Plans.TryGetValue(planId, out var existing) &&
                (existing is not MaterializationPlan.Preservation old || old.Attempt != attempt))
                return new Preservation.Rejected(new(RunProblem.OperationConflict));
            if (record.Preservations.TryGetValue(planId, out var receipt))
            {
                RecordPreservationDrift(permit, operation, repository, record, prepared.Location.Owner, attempt,
                    ((MaterializationPlan.Preservation)existing!).Preserved, inputs);
                ResolveMaintenanceBlocks(permit, operation, "Preserved.");
                return new Preservation.Preserved(receipt, receipt.Commit);
            }
            if (record.PreservationDivergences.TryGetValue(operation, out var divergence))
                return PreservationBlock(permit, operation, "preserve-diverged", DivergenceBlock(operation, task, attempt, inputs, divergence));
            MaterializationPlan.Preservation plan;
            if (existing is MaterializationPlan.Preservation persisted)
            {
                plan = persisted;
                RecordPreservationDrift(permit, operation, repository, record, prepared.Location.Owner, attempt, plan.Preserved, inputs);
                step = "preserve-commit";
                VerifyPreservationCommit(repository, record, operation, plan);
            }
            else
            {
                step = "preserve-observe";
                var title = record.Revisions[writer.Revision].Snapshot.Tasks[task].Title;
                var pair = await ObservePreservationPair(repository, record, prepared.Location, attempt, operation, "preserve",
                    PreservationMessage(record, task, attempt, operation, title), cancellation);
                var pins = new[] { 1, 2 }.Select(ordinal => RunLayout.PreservationPin(record.RunKey!, record.TaskKeys[task], attempt, operation, ordinal)).ToArray();
                ResolveMaintenanceBlocks(permit, operation, "Pins rewritten.", held => held is BlockScope.Refs named && named.Names.All(pins.Contains));
                if (pair.Scope is { } scope)
                {
                    step = "preserve-diverged";
                    divergence = RecordPreservationDivergence(permit, operation, "preserve", pair.First, pair.Second, scope);
                    return PreservationBlock(permit, operation, step, DivergenceBlock(operation, task, attempt, inputs, divergence));
                }
                RecordPreservationDrift(permit, operation, repository, record, prepared.Location.Owner, attempt, pair.First.State, inputs);
                plan = new(task, attempt, pair.First.State, pair.First.Recipe, pair.First.Commit, pair.First.Outbox,
                    RunLayout.PreserveRef(record.RunKey!, record.TaskKeys[task], operation));
                step = "preserve-plan";
                Journal(step, () => _store.Record(permit, planId, new RunEvent.Planned(plan)));
            }
            step = "preserve-ref";
            RequirePublication(_refs.Publish(permit, operation, planId, step, repository, new(plan.Ref, null, plan.Commit)), new BlockScope.Refs([plan.Ref]));
            if (Value(repository.ReadRef(plan.Ref)) != plan.Commit)
                throw Fault(MaterializationProblem.UncertainOwnership, "The preservation retention ref changed.", new BlockScope.Refs([plan.Ref]));
            step = "preserved";
            var preserved = (RunEvent.Preserved)DecisionEvent(Journal(step, () => _store.Record(permit,
                OperationIds.Derive(operation, "preserved"), new RunEvent.Preserved(planId, plan.Ref, plan.Commit))));
            ResolveMaintenanceBlocks(permit, operation, "Preserved.");
            return new Preservation.Preserved(preserved, preserved.Commit);
        }
        catch (Refusal refused) { return new Preservation.Rejected(refused.Reason); }
        catch (MaterializationFailure failed)
        { return PreservationBlock(permit, operation, step, new(operation, task, attempt, failed.Problem, inputs, [], failed.Message) { Scope = failed.Scope }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return PreservationBlock(permit, operation, step, new(operation, task, attempt, MaterializationProblem.InputUnavailable, inputs, [], error.Message) { Scope = new BlockScope.Operation() }); }
    }

    private void RecordPreservationDrift(CoordinatorPermit permit, OperationId operation, GitRepository repository, RunRecord record,
        WorktreeOwner owner, AttemptId attempt, CheckoutState state, InputId? inputs)
    {
        var id = OperationIds.Derive(operation, "preserve-drift");
        if (record.Receipts.ContainsKey(id)) return;
        if (record.Receipts.TryGetValue(OperationIds.Derive(operation, "preserve-plan"), out var planned))
            record = record with { Receipts = record.Receipts.Where(e => e.Value.Sequence < planned.Sequence).ToImmutableDictionary() };
        var baseline = CheckoutBaseline.Fold(record, owner, commit => Value(repository.ReadCommit(commit)).Tree);
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        var branch = false;
        var head = false;
        var drifted = state.IndexLock is not null;
        foreach (var component in baseline.Components)
        {
            var live = ComponentValue(state, component.Key);
            var differs = component.Value switch
            {
                ComponentBaseline.Fixed { Value: null } when component.Key == "index" => !SameBytes(state.Index, baseline.Index),
                ComponentBaseline.Fixed fixedValue => live != fixedValue.Value,
                ComponentBaseline.Pending pending => live != pending.Before && live != pending.After,
                _ => false,
            };
            if (!differs) continue;
            drifted = true;
            if (component.Key == "branch") branch = true;
            else if (component.Key == "head") head = true;
            else
            {
                var expected = component.Value is ComponentBaseline.Fixed f ? f.Value : ((ComponentBaseline.Pending)component.Value).Before;
                if (live is not null && expected is not null) paths.UnionWith(Value(repository.DiffTreePaths(new(live), new(expected))));
            }
        }
        if (!drifted) return;
        var block = new MaterializationBlock(id, owner.Task, attempt,
            branch || head ? MaterializationProblem.UncertainOwnership : MaterializationProblem.DirtyWorktree, inputs, [],
            "The checkout differs from its recorded baseline.") { Scope = new BlockScope.Checkout([.. paths], branch, head, state.IndexLock is not null) };
        Journal("preserve-drift", () => _store.Record(permit, id, new RunEvent.Blocked(block)));
    }

    private void VerifyPreservationCommit(GitRepository repository, RunRecord record, OperationId operation,
        MaterializationPlan.Preservation plan)
    {
        var pinned = Enumerable.Range(1, 2).Any(ordinal => Value(repository.ReadRef(
            RunLayout.PreservationPin(record.RunKey!, record.TaskKeys[plan.Task], plan.Attempt, operation, ordinal))) == plan.Commit);
        var valid = pinned
            ? repository.ReadCommit(plan.Commit) is GitRead<GitCommit>.Read read && read.Value.Tree == plan.Recipe.Tree &&
                read.Value.Parents.SequenceEqual(plan.Recipe.Parents)
            : Mutate("preserve-commit", () => repository.CreateCommit(plan.Recipe)) is GitRead<CommitId>.Read created && created.Value == plan.Commit;
        if (!valid) throw Fault(MaterializationProblem.InputUnavailable, "The preservation commit is missing. Preserve again.", new BlockScope.Operation());
    }

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
        var branch = Value(repository.ReadRef(location.Owner.Branch));
        var head = Value(repository.ResolveCheckoutHead(checkout));
        var symbolicHead = Value(repository.SymbolicHead(checkout));
        var lockFile = FileIdentities.ReadFile(Value(repository.IndexPath(checkout)) + ".lock");
        LockEvidence? indexLock = lockFile is { } locked ? new(storeBytes("index.lock", locked.Bytes), locked.Identity) : null;
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
        return (new(branch, head, symbolicHead, capture.Tree, index, indexTree, untracked, indexLock), outbox.ToImmutable(), stages);
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

    private Preservation PreservationBlock(CoordinatorPermit permit, OperationId operation, string step, MaterializationBlock block) =>
        Block(permit, operation, step, block) switch
        {
            Preparation.Blocked blocked => new Preservation.Blocked(blocked.Block),
            Preparation.Rejected rejected => new Preservation.Rejected(rejected.Reason),
            _ => throw new InvalidOperationException(),
        };
}
