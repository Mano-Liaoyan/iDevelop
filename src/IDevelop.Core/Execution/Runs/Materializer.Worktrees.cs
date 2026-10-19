using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

internal sealed partial class Materializer
{
    private RunRecord AllocateLayout(CoordinatorPermit permit, OperationId operation, TaskId task, GitRepository repository, RunRecord record)
    {
        var workflow = permit.Workflow;
        var run = permit.Run;
        var layoutId = OperationIds.Derive(new OperationId(run.Value), "layout");
        if (record.RunKey is null)
        {
            var taken = RunLayout.UsedRunKeys(Value(repository.RefSnapshot("refs/heads/idp/", "refs/idp/")));
            record = DecisionRecord(Journal("run-layout", () => _store.Record(permit, layoutId,
                new RunEvent.LayoutAllocated(new LayoutKey.Run(RunLayout.Key(run.Value, taken), repository.CommonDirectory)))));
        }
        VerifyRepository(record, repository);
        var actualLayout = record.Receipts.Single(pair => pair.Value.Event is RunEvent.LayoutAllocated { Key: LayoutKey.Run }).Key;
        var change = new RefChange(RunLayout.ApprovedBase(record.RunKey!), null, record.Base.Commit);
        RequirePublication(_refs.Publish(permit, actualLayout, actualLayout, "base", repository, change), new BlockScope.Refs([change.Ref]));
        if (Value(repository.ReadRef(change.Ref)) != change.Target)
            throw Fault(MaterializationProblem.UncertainOwnership, "Approved base ref has an unexpected value.", new BlockScope.Refs([change.Ref]));
        ApprovalPin.Release(repository, run, change.Target, change.Ref);
        record = Read(workflow, run);
        if (!record.TaskKeys.ContainsKey(task))
        {
            var taken = record.TaskKeys.Values.ToHashSet(StringComparer.Ordinal);
            record = DecisionRecord(Journal("task-layout", () => _store.Record(permit, OperationIds.Derive(operation, "task-layout"),
                new RunEvent.LayoutAllocated(new LayoutKey.Task(task, RunLayout.Key(task.Value, taken))))));
        }
        return record;
    }

    private static void VerifyRepository(RunRecord record, GitRepository repository)
    {
        if (record.Repository != repository.CommonDirectory)
            throw Fault(MaterializationProblem.UncertainOwnership, "The run belongs to a different repository common directory.", new BlockScope.Repository());
    }

    private void EnsureCheckout(CoordinatorPermit permit, OperationId operation, OperationId plan, GitRepository repository,
        ExecutionLocation location, RunRecord record, bool keepFiles = false, bool retry = false)
    {
        var workflow = permit.Workflow;
        var run = permit.Run;
        var owner = location.Owner;
        var checkout = RunStorage.SafePath(repository.ProjectFolder, owner.RelativePath);
        var worktrees = Value(repository.Worktrees());
        var registered = worktrees.SingleOrDefault(worktree => SamePath(worktree.Path, checkout));
        if (registered is null && owner.Branch is null)
        {
            if (Directory.Exists(checkout) && Directory.EnumerateFileSystemEntries(checkout).Any())
                throw Fault(MaterializationProblem.UncertainOwnership, "The occupied checkout is not a matching creation intent.", new BlockScope.Ownership());
            var intentId = OperationIds.Derive(operation, "create-worktree-intent");
            Journal("create-worktree-intent", () => _store.Record(permit, intentId,
                new RunEvent.GitIntended(plan, new GitMutation.CreateWorktree(owner, location.AttemptBase, false))));
            var result = Mutate("create-worktree", () => repository.AddDetachedWorktree(owner.RelativePath, location.AttemptBase));
            if (result.ExitCode != 0) throw Fault(MaterializationProblem.UncertainOwnership, result.Stderr, new BlockScope.Ownership());
        }
        else if (registered is null)
        {
            var branch = Value(repository.ReadRef(owner.Branch!));
            var existing = branch is not null;
            var oldIntent = record.GitIntents.Values.Select(intent => intent.Mutation).OfType<GitMutation.CreateWorktree>()
                .FirstOrDefault(create => create.Owner == owner && create.Start == branch);
            if (!RefOwnership.Accepts(record, repository, owner.Branch!, branch) ||
                existing && (oldIntent is null || Directory.Exists(checkout) && Directory.EnumerateFileSystemEntries(checkout).Any()))
                throw Fault(MaterializationProblem.UncertainOwnership, "The branch or occupied checkout is not a matching creation intent.", new BlockScope.Ownership());
            if (worktrees.Any(worktree => worktree.Branch == owner.Branch))
                throw Fault(MaterializationProblem.UncertainOwnership, "The task branch is registered at another checkout.", new BlockScope.Ownership());
            var start = existing ? oldIntent!.Start : location.AttemptBase;
            var label = existing ? "adopt-worktree" : "create-worktree";
            var intentId = OperationIds.Derive(operation, label + "-intent");
            Journal(label + "-intent", () => _store.Record(permit, intentId,
                new RunEvent.GitIntended(plan, new GitMutation.CreateWorktree(owner, start, existing))));
            // Git can create the branch before it refuses an occupied target directory.
            var result = Mutate(label, () => existing
                ? repository.AddWorktreeForExistingBranch(owner.RelativePath, RunLayout.TaskBranchShortName(record.RunKey!, record.TaskKeys[owner.Task]))
                : repository.AddWorktree(owner.RelativePath, RunLayout.TaskBranchShortName(record.RunKey!, record.TaskKeys[owner.Task]), start));
            if (result.ExitCode != 0) throw Fault(MaterializationProblem.UncertainOwnership, result.Stderr, new BlockScope.Ownership());
        }
        record = Read(workflow, run);
        VerifyRegistration(repository, location, record);
        foreach (var pair in record.GitIntents.Where(pair => pair.Value.Mutation is GitMutation.CreateWorktree create &&
            create.Owner == owner && !record.GitObservations.ContainsKey(pair.Key)))
        {
            var create = (GitMutation.CreateWorktree)pair.Value.Mutation;
            var tip = owner.Branch is { } created ? Value(repository.ReadRef(created)) : Value(repository.ResolveCheckoutHead(checkout));
            if (tip != create.Start) throw Fault(MaterializationProblem.UncertainOwnership, "Unobserved creation has an unexpected branch tip.", new BlockScope.Checkout([], Branch: true));
            Journal("worktree-observed", () => _store.Record(permit, OperationIds.Derive(operation, $"worktree-observed-{pair.Key.Value:D}"),
                new RunEvent.GitObserved(pair.Key, new(create.ExistingBranch, tip?.Hex))));
        }
        var reason = $"idevelop {record.RunKey}/{record.TaskKeys[owner.Task]}";
        var registration = Value(repository.Worktrees()).Single(worktree => SamePath(worktree.Path, checkout));
        if (registration.Locked && registration.LockReason != reason)
            throw Fault(MaterializationProblem.UncertainOwnership, "The worktree has another ownership lock.", new BlockScope.Ownership());
        if (!registration.Locked)
        {
            var result = Mutate("lock-worktree", () => repository.LockWorktree(owner.RelativePath, reason));
            if (result.ExitCode != 0) throw Fault(MaterializationProblem.UncertainOwnership, result.Stderr, new BlockScope.Ownership());
        }
        // An editing task's Retry whose inputs' work changed starts at the merge of that work into its earlier change. Its
        // branch moves there from where it was, and the files its worktree holds stay (#125).
        if (retry && owner.Branch is { } own && RunReducer.Earlier(record, owner.Task) is { } earlier && earlier != location.AttemptBase)
        {
            var intended = OperationIds.Derive(operation, "retry-merge-intent");
            var tip = Value(repository.ReadRef(own));
            // The branch is where a result or an earlier Retry's move left it; a failed attempt commits nothing to it.
            var placed = record.GitIntents.Where(pair => pair.Key != intended && record.GitObservations.ContainsKey(pair.Key))
                .Select(pair => pair.Value.Mutation).OfType<GitMutation.MoveRef>().Where(move => move.Change.Ref == own)
                .Select(move => (CommitId?)move.Change.Target).Append(earlier);
            if (tip != location.AttemptBase && placed.Contains(tip))
            {
                if (!record.GitIntents.ContainsKey(intended))
                    Journal("retry-merge-intent", () => _store.Record(permit, intended,
                        new RunEvent.GitIntended(plan, new GitMutation.MoveRef(new(own, tip, location.AttemptBase)))));
                var result = Mutate("retry-merge", () => repository.KeepReset(checkout, location.AttemptBase));
                if (result.ExitCode != 0) throw Fault(MaterializationProblem.DirtyWorktree, result.Stderr.Trim(), BlockScope.Checkout.Whole);
                tip = location.AttemptBase;
            }
            if (tip != location.AttemptBase)
                throw Fault(MaterializationProblem.UncertainOwnership, "The task's branch is not where its earlier change is.", new BlockScope.Checkout([], Branch: true));
            var moved = Read(workflow, run);
            if (moved.GitIntents.ContainsKey(intended) && !moved.GitObservations.ContainsKey(intended))
                Journal("retry-merge-observed", () => _store.Record(permit, OperationIds.Derive(operation, "retry-merge-observed"),
                    new RunEvent.GitObserved(intended, new(false, location.AttemptBase.Hex))));
        }
        // A read-only worktree commits nothing, so each attempt starts exactly at the merge of its predecessors, without
        // what an earlier attempt left in it. Ignored files, such as delivered inputs, stay. A Merge node's Retry keeps the
        // files its failed attempt left, while its worktree is still at the conflict that attempt worked on.
        if (owner.Branch is null && (Value(repository.ResolveCheckoutHead(checkout)) != location.AttemptBase ||
            !keepFiles && Value(repository.Status(checkout)).Length != 0))
        {
            var reset = new GitMutation.ResetCheckout(owner.Task, location.AttemptBase);
            var intended = OperationIds.Derive(operation, "read-only-reset-intent");
            if (!record.GitIntents.ContainsKey(intended))
                Journal("read-only-reset-intent", () => _store.Record(permit, intended, new RunEvent.GitIntended(plan, reset)));
            var result = Mutate("read-only-reset", () => repository.ResetCheckout(checkout, location.AttemptBase));
            if (result.ExitCode == 0) result = Mutate("read-only-clean", () => repository.CleanUntracked(checkout));
            if (result.ExitCode != 0) throw Fault(MaterializationProblem.GitFailed, result.Stderr, BlockScope.Checkout.Whole);
            if (!Read(workflow, run).GitObservations.ContainsKey(intended))
                Journal("read-only-reset-observed", () => _store.Record(permit, OperationIds.Derive(operation, "read-only-reset-observed"),
                    new RunEvent.GitObserved(intended, new(false, location.AttemptBase.Hex))));
        }
        if (File.Exists(Path.Combine(checkout, ".gitmodules")))
        {
            var result = Mutate("submodules", () => repository.InitializeSubmodules(checkout));
            if (result.ExitCode != 0) throw Fault(MaterializationProblem.SubmoduleUnavailable, result.Stderr, BlockScope.Checkout.Whole);
        }
    }

    /// <summary>
    /// Whether Git registers the task's worktree in this repository as the run recorded it: on the task's branch, or for a
    /// read-only task, detached. <paramref name="requireBranch"/> false accepts any HEAD.
    /// </summary>
    private static void VerifyRegistration(GitRepository repository, ExecutionLocation location, RunRecord record, bool requireBranch = true)
    {
        var owner = location.Owner;
        var checkout = RunStorage.SafePath(repository.ProjectFolder, owner.RelativePath);
        var registration = Value(repository.Worktrees()).SingleOrDefault(worktree => SamePath(worktree.Path, checkout));
        if (registration is null || requireBranch && registration.Branch != owner.Branch ||
            !SamePath(Value(repository.CheckoutCommonDirectory(checkout)), repository.CommonDirectory) ||
            requireBranch && Value(repository.SymbolicHead(checkout)) != owner.Branch ||
            !record.GitIntents.Values.Any(intent => intent.Mutation is GitMutation.CreateWorktree create && create.Owner == owner))
            throw Fault(MaterializationProblem.UncertainOwnership, "Registration, common directory, symbolic HEAD and recorded worktree owner do not agree.", new BlockScope.Ownership());
    }

    /// <summary>Whether the task's worktree is registered as the run recorded it and still holds the run's ownership lock.</summary>
    private static void VerifyWorktree(GitRepository repository, ExecutionLocation location, RunRecord record)
    {
        VerifyRegistration(repository, location, record);
        VerifyOwnedCheckout(repository, location, record);
    }

    private static void VerifyOwnedCheckout(GitRepository repository, ExecutionLocation location, RunRecord record)
    {
        VerifyRegistration(repository, location, record, requireBranch: false);
        var registration = Value(repository.Worktrees()).Single(worktree => SamePath(worktree.Path, Checkout(repository, location.Owner)));
        if (!registration.Locked || registration.LockReason != $"idevelop {record.RunKey}/{record.TaskKeys[location.Owner.Task]}")
            throw Fault(MaterializationProblem.UncertainOwnership, "The recorded worktree ownership lock is absent or differs.", new BlockScope.Ownership());
    }

    private static bool SamePath(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void VerifyJoin(RunRecord record, GitRepository repository, TaskId task, InputId inputs, ImmutableArray<CodeSource> sources, JoinRecord join)
    {
        if (record.Plans.GetValueOrDefault(join.Operation) is not MaterializationPlan.Join plan ||
            plan.Task != task || plan.Inputs != inputs || !RunReducer.Same(plan.Sources, sources) ||
            !RunReducer.Same(join.Sources, plan.Sources) || join.Commit != plan.Commit || join.Tree != plan.Recipe.Tree || join.Ref != plan.Ref ||
            !record.GitIntents.Any(pair => pair.Value.Plan == join.Operation && pair.Value.Mutation is GitMutation.MoveRef move &&
                move.Change == new RefChange(plan.Ref, plan.Previous, plan.Commit) &&
                record.GitObservations.GetValueOrDefault(pair.Key)?.Value == plan.Commit.Hex) ||
            repository.ObjectType(join.Commit.Hex) is not GitRead<string>.Read { Value: "commit" } ||
            repository.ReadCommit(join.Commit) is not GitRead<GitCommit>.Read commit || commit.Value.Tree != join.Tree ||
            !commit.Value.Parents.SequenceEqual(plan.Recipe.Parents) ||
            repository.ReadRef(join.Ref) is not GitRead<CommitId?>.Read reference || reference.Value != join.Commit)
            throw Fault(MaterializationProblem.InputUnavailable, "The join does not match its recorded plan, observation, commit object and ref.", new BlockScope.Operation());
    }
}
