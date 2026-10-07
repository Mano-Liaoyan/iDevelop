using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

internal sealed partial class Materializer
{
    private RunRecord AllocateLayout(WorkflowId workflow, RunId run, OperationId operation, TaskId task, GitRepository repository, RunRecord record)
    {
        var layoutId = OperationIds.Derive(new OperationId(run.Value), "layout");
        if (record.RunKey is null)
        {
            var taken = RunLayout.UsedRunKeys(Value(repository.RefSnapshot("refs/heads/idp/", "refs/idp/")));
            record = DecisionRecord(Journal("run-layout", () => _store.Record(workflow, run, layoutId,
                new RunEvent.LayoutAllocated(new LayoutKey.Run(RunLayout.Key(run.Value, taken), repository.CommonDirectory)))));
        }
        VerifyRepository(record, repository);
        var actualLayout = record.Receipts.Single(pair => pair.Value.Event is RunEvent.LayoutAllocated { Key: LayoutKey.Run }).Key;
        var change = new RefChange(RunLayout.ApprovedBase(record.RunKey!), null, record.Base.Commit);
        var intent = OperationIds.Derive(actualLayout, "base-intent");
        Journal("base-intent", () => _store.Record(workflow, run, intent, new RunEvent.GitIntended(actualLayout, new GitMutation.MoveRef(change))));
        var tip = Value(repository.ReadRef(change.Ref));
        if (tip != change.Target)
        {
            if (tip is not null) throw Fault(MaterializationProblem.UncertainOwnership, "Approved base ref has an unexpected value.");
            var move = Mutate("base-ref", () => repository.MoveRef(change));
            if (move is RefMove.Failed failed) throw Fault(MaterializationProblem.GitFailed, failed.Detail);
            if (move is RefMove.Conflict) throw Fault(MaterializationProblem.UncertainOwnership, "Approved base retention failed: " + move);
        }
        record = Read(workflow, run);
        if (!record.GitObservations.ContainsKey(intent))
            record = DecisionRecord(Journal("base-observed", () => _store.Record(workflow, run,
                OperationIds.Derive(actualLayout, "base-observed"), new RunEvent.GitObserved(intent, new(false, change.Target.Hex)))));
        if (!record.TaskKeys.ContainsKey(task))
        {
            var taken = record.TaskKeys.Values.ToHashSet(StringComparer.Ordinal);
            record = DecisionRecord(Journal("task-layout", () => _store.Record(workflow, run, OperationIds.Derive(operation, "task-layout"),
                new RunEvent.LayoutAllocated(new LayoutKey.Task(task, RunLayout.Key(task.Value, taken))))));
        }
        return record;
    }

    private static void VerifyRepository(RunRecord record, GitRepository repository)
    {
        if (record.Repository != repository.CommonDirectory)
            throw Fault(MaterializationProblem.UncertainOwnership, "The run belongs to a different repository common directory.");
    }

    private void EnsureCheckout(WorkflowId workflow, RunId run, OperationId operation, OperationId plan, GitRepository repository,
        ExecutionLocation location, RunRecord record)
    {
        var owner = location.Owner;
        var checkout = RunStorage.SafePath(repository.ProjectFolder, owner.RelativePath);
        var worktrees = Value(repository.Worktrees());
        var registered = worktrees.SingleOrDefault(worktree => SamePath(worktree.Path, checkout));
        if (registered is null)
        {
            var branch = Value(repository.ReadRef(owner.Branch));
            var existing = branch is not null;
            var oldIntent = record.GitIntents.Values.Select(intent => intent.Mutation).OfType<GitMutation.CreateWorktree>()
                .FirstOrDefault(create => create.Owner == owner && create.Start == branch);
            if (existing && (oldIntent is null || Directory.Exists(checkout) && Directory.EnumerateFileSystemEntries(checkout).Any()))
                throw Fault(MaterializationProblem.UncertainOwnership, "The branch or occupied checkout is not a matching creation intent.");
            if (worktrees.Any(worktree => worktree.Branch == owner.Branch))
                throw Fault(MaterializationProblem.UncertainOwnership, "The task branch is registered at another checkout.");
            var start = existing ? oldIntent!.Start : location.AttemptBase;
            var label = existing ? "adopt-worktree" : "create-worktree";
            var intentId = OperationIds.Derive(operation, label + "-intent");
            Journal(label + "-intent", () => _store.Record(workflow, run, intentId,
                new RunEvent.GitIntended(plan, new GitMutation.CreateWorktree(owner, start, existing))));
            // Git can create the branch before it refuses an occupied target directory.
            var result = Mutate(label, () => existing
                ? repository.AddWorktreeForExistingBranch(owner.RelativePath, RunLayout.TaskBranchShortName(record.RunKey!, record.TaskKeys[owner.Task]))
                : repository.AddWorktree(owner.RelativePath, RunLayout.TaskBranchShortName(record.RunKey!, record.TaskKeys[owner.Task]), start));
            if (result.ExitCode != 0) throw Fault(MaterializationProblem.UncertainOwnership, result.Stderr);
        }
        record = Read(workflow, run);
        VerifyRegistration(repository, location, record);
        foreach (var pair in record.GitIntents.Where(pair => pair.Value.Mutation is GitMutation.CreateWorktree create &&
            create.Owner == owner && !record.GitObservations.ContainsKey(pair.Key)))
        {
            var create = (GitMutation.CreateWorktree)pair.Value.Mutation;
            var tip = Value(repository.ReadRef(owner.Branch));
            if (tip != create.Start) throw Fault(MaterializationProblem.UncertainOwnership, "Unobserved creation has an unexpected branch tip.");
            Journal("worktree-observed", () => _store.Record(workflow, run, OperationIds.Derive(operation, $"worktree-observed-{pair.Key.Value:D}"),
                new RunEvent.GitObserved(pair.Key, new(create.ExistingBranch, tip?.Hex))));
        }
        var reason = $"idevelop {record.RunKey}/{record.TaskKeys[owner.Task]}";
        var registration = Value(repository.Worktrees()).Single(worktree => SamePath(worktree.Path, checkout));
        if (registration.Locked && registration.LockReason != reason)
            throw Fault(MaterializationProblem.UncertainOwnership, "The worktree has another ownership lock.");
        if (!registration.Locked)
        {
            var result = Mutate("lock-worktree", () => repository.LockWorktree(owner.RelativePath, reason));
            if (result.ExitCode != 0) throw Fault(MaterializationProblem.UncertainOwnership, result.Stderr);
        }
        if (File.Exists(Path.Combine(checkout, ".gitmodules")))
        {
            var result = Mutate("submodules", () => repository.InitializeSubmodules(checkout));
            if (result.ExitCode != 0) throw Fault(MaterializationProblem.SubmoduleUnavailable, result.Stderr);
        }
    }

    private static void VerifyRegistration(GitRepository repository, ExecutionLocation location, RunRecord record)
    {
        var owner = location.Owner;
        var checkout = RunStorage.SafePath(repository.ProjectFolder, owner.RelativePath);
        var registration = Value(repository.Worktrees()).SingleOrDefault(worktree => SamePath(worktree.Path, checkout));
        if (registration is null || registration.Branch != owner.Branch ||
            !SamePath(Value(repository.CheckoutCommonDirectory(checkout)), repository.CommonDirectory) ||
            Value(repository.SymbolicHead(checkout)) != owner.Branch ||
            !record.GitIntents.Values.Any(intent => intent.Mutation is GitMutation.CreateWorktree create && create.Owner == owner))
            throw Fault(MaterializationProblem.UncertainOwnership, "Registration, common directory, symbolic HEAD and recorded worktree owner do not agree.");
    }

    private static void VerifyCheckout(GitRepository repository, ExecutionLocation location, bool keepChanges, RunRecord record)
    {
        VerifyRegistration(repository, location, record);
        var tip = Value(repository.ReadRef(location.Owner.Branch));
        if (tip is null) throw Fault(MaterializationProblem.UncertainOwnership, "The task branch is absent.");
        if (keepChanges)
        {
            if (repository.IsAncestor(location.AttemptBase, tip.Value) is not GitAncestry.Yes)
                throw Fault(MaterializationProblem.UncertainOwnership, "The checkout no longer descends from its attempt base.");
        }
        else if (tip != location.AttemptBase || Value(repository.Status(Checkout(repository, location.Owner))).Length != 0)
            throw Fault(MaterializationProblem.DirtyWorktree, "The checkout tip or contents differ from the recorded attempt base.");
        var registration = Value(repository.Worktrees()).Single(worktree => SamePath(worktree.Path, Checkout(repository, location.Owner)));
        if (!registration.Locked || registration.LockReason != $"idevelop {record.RunKey}/{record.TaskKeys[location.Owner.Task]}")
            throw Fault(MaterializationProblem.UncertainOwnership, "The recorded worktree ownership lock is absent or differs.");
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
            throw Fault(MaterializationProblem.InputUnavailable, "The join does not match its recorded plan, observation, commit object and ref.");
    }
}
