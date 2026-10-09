using System.Collections.Immutable;
using System.Text.Json;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>
/// Review updated inputs: a stale writer result's recorded change, replayed onto the current inputs in E2b's isolated
/// merge directory, and the person-approved move of the task's branch and checkout to that candidate. No client runs.
/// </summary>
internal sealed partial class Materializer
{
    private const string RebaseIdentity = "iDevelop <idevelop@localhost>";
    private const string StaleCheckoutChanged = "The checkout changed after its result was accepted. Preserve and restore it, then review the updated inputs again.";

    /// <summary>Everything a preview found, and what an approval needs to plan the same candidate.</summary>
    private sealed record RebaseBuild(RebasePreview Preview, ResultRecord Source, OwnedCode Code, ExecutionLocation Location,
        ImmutableArray<InputBinding> Bindings, ImmutableArray<CodeSource> Sources, ReviewInput? Review, CommitRecipe? Recipe,
        CommitRecipe? Join, CommitId? JoinCommit);

    /// <summary>
    /// Shows a stale task's old and current inputs, its recorded change, the report and artifacts a rebase carries forward,
    /// and a candidate built without touching its branch, index, or files. Records nothing.
    /// </summary>
    public RebasePreviewRead PreviewRebase(RunLease lease)
    {
        using var authority = lease.Use();
        if (authority is null) return new RebasePreviewRead.Rejected(new(RunProblem.TaskBusy));
        if (LeaseProblem(lease) is { } problem) return new RebasePreviewRead.Rejected(new(problem));
        try
        {
            // The preview builds merges and clears stale merge scratch folders, which only the repository lock's holder may do.
            var repository = OpenRepository();
            using var mutation = repository.TakeMutationLock(MutationPatience, Halted);
            if (mutation is null) return new RebasePreviewRead.Rejected(new(RunProblem.JournalBusy));
            var record = Read(lease.Permit.Workflow, lease.Permit.Run);
            VerifyRepository(record, repository);
            var build = BuildRebase(repository, record, lease.Task);
            if (RunReducer.AttemptDrift(record, id => id == build.Code.Attempt) is { } held)
                return new RebasePreviewRead.Refused(held.Value.Block.Problem, held.Value.Block.Detail, held.Value.Block.Scope);
            return new RebasePreviewRead.Previewed(build.Preview);
        }
        catch (Refusal refused) { return new RebasePreviewRead.Rejected(refused.Reason); }
        catch (MaterializationFailure failed) { return new RebasePreviewRead.Refused(failed.Problem, failed.Message, failed.Scope); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return new RebasePreviewRead.Refused(MaterializationProblem.InputUnavailable, error.Message, new BlockScope.Operation()); }
    }

    /// <summary>
    /// Rebases the task's stale result as the person approved under <paramref name="approval"/>, only while a fresh
    /// preview still has <paramref name="preview"/> as its identity: unchanged source results, candidate, ownership, and
    /// checkout. Then it retains the candidate, moves the branch from the stale result's commit, resets the clean checkout,
    /// and records the rebased result. Every move is journaled before and after, so repeating the approval, or Resume,
    /// finishes a plan a crash interrupted and never builds another candidate.
    /// </summary>
    public Rebasing Rebase(RunLease lease, OperationId approval, Digest preview)
    {
        using var authority = lease.Use();
        if (authority is null) return new Rebasing.Rejected(new(RunProblem.TaskBusy));
        if (LeaseProblem(lease) is { } problem) return new Rebasing.Rejected(new(problem));
        if (approval.Value == Guid.Empty) return new Rebasing.Rejected(new(RunProblem.ConfirmationRequired));
        var permit = lease.Permit;
        var workflow = permit.Workflow;
        var run = permit.Run;
        var task = lease.Task;
        var planId = OperationIds.Derive(approval, "rebase-plan");
        var owner = approval;
        var step = "rebase-preconditions";
        AttemptId? publisher = null;
        InputId? inputs = null;
        try
        {
            var record = Read(workflow, run);
            if (record.Plans.TryGetValue(planId, out var existing) && (existing is not MaterializationPlan.Rebase old ||
                old.Task != task || old.Approval != approval || old.Preview != preview))
                return new Rebasing.Rejected(new(RunProblem.OperationConflict));
            if (RebasedResult(record, planId) is { } done)
            {
                ResolveRebaseBlocks(permit, approval, planId);
                return new Rebasing.Rebased(done);
            }
            var repository = OpenRepository();
            using var mutation = repository.TakeMutationLock(MutationPatience, Halted);
            if (mutation is null) return new Rebasing.Rejected(new(RunProblem.JournalBusy));
            record = Read(workflow, run);
            if (RebasedResult(record, planId) is { } concurrent)
            {
                ResolveRebaseBlocks(permit, approval, planId);
                return new Rebasing.Rebased(concurrent);
            }
            VerifyRepository(record, repository);
            MaterializationPlan.Rebase plan;
            ExecutionLocation location;
            if (record.Plans.GetValueOrDefault(planId) is MaterializationPlan.Rebase persisted)
            {
                plan = persisted;
                owner = planId;
                var source = record.Results.Single(result => result.Id == plan.Source);
                var code = ((CodeOutput.Produced)source.Code!).Code;
                publisher = code.Attempt;
                inputs = source.Inputs;
                location = record.Preparations[new(code.Attempt, 1)].Location;
            }
            else
            {
                // The journal refuses a new plan once the run stops.
                if (record.Plans.Values.OfType<MaterializationPlan.Rebase>().Any(other => other.Task == task &&
                    !record.Results.Any(result => result.Id == other.Result)))
                    return new Rebasing.Rejected(new(RunProblem.ReplacementConflict));
                step = "rebase-preview";
                if (record.CurrentResults.GetValueOrDefault(task) is { Code: CodeOutput.Produced { Code: var current } } stale)
                {
                    publisher = current.Attempt;
                    inputs = stale.Inputs;
                    if (RunReducer.AttemptDrift(record, id => id == current.Attempt, approval) is { } held) return new Rebasing.Blocked(held.Value.Block);
                }
                var build = BuildRebase(repository, record, task);
                if (build.Preview.Identity != preview) return new Rebasing.Rejected(new(RunProblem.EvidenceMismatch));
                switch (build.Preview.Candidate)
                {
                    case RebaseCandidate.Conflicted: return new Rebasing.Rejected(new(RunProblem.InputConflict));
                    case RebaseCandidate.Unavailable: return new Rebasing.Rejected(new(RunProblem.UnsupportedWork));
                }
                location = build.Location;
                plan = RebasePlan(record, build, planId, approval, preview);
                step = "rebase-plan";
                Journal("rebase-plan", () => _store.Record(permit, planId, new RunEvent.Planned(plan)));
                owner = planId;
            }
            VerifyOwnedCheckout(repository, location, Read(workflow, run));
            var checkout = Checkout(repository, location.Owner);
            step = "rebase-commit";
            if (plan.JoinRecipe is { } joinRecipe && Value(Mutate("rebase-join", () => repository.CreateCommit(joinRecipe))) != plan.Recipe.Parents[0])
                throw Fault(MaterializationProblem.UncertainOwnership, "The recorded rebase join recipe produced a different commit.", new BlockScope.Operation());
            if (Value(Mutate("rebase-candidate", () => repository.CreateCommit(plan.Recipe))) != plan.Commit)
                throw Fault(MaterializationProblem.UncertainOwnership, "The recorded rebase recipe produced a different commit.", new BlockScope.Operation());
            var oldTree = Value(repository.ReadCommit(plan.From)).Tree;
            step = "rebase-retain";
            RequirePublication(_refs.Publish(permit, approval, planId, "rebase-retain", repository, new(plan.Ref, null, plan.Commit)),
                new BlockScope.Refs([plan.Ref]));
            step = "rebase-branch";
            var branch = new GitMutation.MoveRef(new(location.Owner.Branch, plan.From, plan.Commit));
            if (!PublicationObserved(Read(workflow, run), planId, branch))
            {
                // Before the branch moves, the checkout must still be the clean result the person saw.
                if (Value(repository.ReadRef(location.Owner.Branch)) == plan.From) VerifyRebaseHead(repository, checkout, location.Owner.Branch, plan.From);
                VerifyRebaseFiles(repository, checkout, oldTree, null);
                VerifyIgnoredObstructions(repository, checkout, Value(repository.TreeFiles(plan.Commit)));
                RequirePublication(_refs.Publish(permit, approval, planId, "rebase-branch", repository, branch.Change),
                    new BlockScope.Checkout([], Branch: true));
            }
            step = "rebase-reset";
            var reset = new GitMutation.ResetCheckout(task, plan.Commit);
            record = Read(workflow, run);
            if (!PublicationObserved(record, planId, reset))
            {
                var intended = OperationIds.Derive(approval, "rebase-reset-intent");
                VerifyResetHead(repository, checkout, location.Owner.Branch, plan.Commit);
                // A reset the journal intended may have run before a crash; either the old clean files or the candidate's pass.
                var adopted = VerifyRebaseFiles(repository, checkout, oldTree, record.GitIntents.ContainsKey(intended) ? plan.Recipe.Tree : null);
                if (!adopted) VerifyIgnoredObstructions(repository, checkout, Value(repository.TreeFiles(plan.Commit)));
                if (!record.GitIntents.ContainsKey(intended))
                    Journal("rebase-reset-intent", () => _store.Record(permit, intended, new RunEvent.GitIntended(planId, reset)));
                if (!adopted)
                {
                    var result = Mutate("rebase-reset", () =>
                    {
                        VerifyResetHead(repository, checkout, location.Owner.Branch, plan.Commit);
                        return repository.ResetCheckout(checkout, plan.Commit);
                    });
                    if (result.ExitCode != 0) throw Fault(MaterializationProblem.GitFailed, result.Stderr, BlockScope.Checkout.Whole);
                }
                Journal("rebase-reset-observed", () => _store.Record(permit, OperationIds.Derive(approval, "rebase-reset-observed"),
                    new RunEvent.GitObserved(intended, new(adopted, plan.Commit.Hex))));
            }
            step = "rebase-verify";
            VerifyRebased(repository, checkout, location, plan, Read(workflow, run));
            step = "rebase-artifacts";
            CopyRebaseArtifacts(workflow, run, Read(workflow, run).Results.Single(result => result.Id == plan.Source), plan.Result);
            step = "rebase-accepted";
            var decision = Journal("rebase-accepted", () => _store.AcceptRebase(permit, OperationIds.Derive(approval, "rebase-accepted"), planId));
            ResolveRebaseBlocks(permit, approval, planId);
            return new Rebasing.Rebased(((RunEvent.ResultAccepted)DecisionEvent(decision)).Result);
        }
        catch (Refusal refused) { return new Rebasing.Rejected(refused.Reason); }
        catch (MaterializationFailure failed)
        { return RebaseBlock(permit, approval, step, new(owner, task, publisher, failed.Problem, inputs, [], failed.Message) { Scope = failed.Scope }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return RebaseBlock(permit, approval, step, new(owner, task, publisher, MaterializationProblem.InputUnavailable, inputs, [], error.Message) { Scope = new BlockScope.Operation() }); }
    }

    private static ResultRecord? RebasedResult(RunRecord record, OperationId plan) =>
        record.Results.FirstOrDefault(result => result.Origin is ResultOrigin.Rebased rebased && rebased.Plan == plan);

    /// <summary>The stale result, its owner's checkout as the journal last recorded it, and the candidate on the current inputs.</summary>
    private RebaseBuild BuildRebase(GitRepository repository, RunRecord record, TaskId task)
    {
        if (record.Schema != 3) throw new Refusal(new(RunProblem.UnsupportedSchema));
        if (record.CurrentResults.GetValueOrDefault(task) is not { } source || !record.StaleResults.Contains(source.Id))
            throw new Refusal(new(RunProblem.UnknownResult, Task: task));
        // Only a writer's result carries code of its own. A stale report is retried instead.
        if (source.Code is not CodeOutput.Produced { Code: var code })
            throw new Refusal(new(RunProblem.UnsupportedResult, Task: task));
        if (record.Attempts.Values.Any(attempt => attempt.Task == task && !record.Closures.ContainsKey(attempt.Id)))
            throw new Refusal(new(RunProblem.UnclosedAttempts, Task: task));
        var previous = record.Inputs[source.Inputs];
        var oldBase = previous.CodeBase;
        var capture = RunStore.Inputs(record, task, source.Revision, default);
        if (capture.Rejection is { } rejection) throw new Refusal(rejection);
        var bindings = capture.Inputs!.Bindings;
        var sources = InputMaterial.Sources(record, bindings);
        var review = InputMaterial.Review(record.Revisions[source.Revision].Snapshot, task, bindings);
        ImmutableArray<TaskId> updated = [.. bindings.Select(binding => binding switch
        {
            InputBinding.Provided provided => (provided.Edge.From, (ResultId?)provided.Result),
            InputBinding.MissingContext missing => (missing.Edge.From, null),
            _ => throw new InvalidOperationException(),
        }).Where(binding => !previous.Bindings.Any(old => old is InputBinding.Provided provided && provided.Edge.From == binding.Item1 &&
            provided.Result == binding.Item2)).Select(binding => binding.Item1).Distinct().OrderBy(id => id.ToString(), StringComparer.Ordinal)];

        // The owner's checkout must match what the journal last recorded for it: the stale result, clean.
        var location = record.Preparations[new(code.Attempt, 1)].Location;
        var fold = CheckoutBaseline.Fold(record, location.Owner, commit => Value(repository.ReadCommit(commit)).Tree);
        if (fold.Components.Values.Any(component => component is not ComponentBaseline.Fixed))
            throw new Refusal(new(RunProblem.UnresolvedOwnership, Task: task));
        // A later attempt, such as a failed retry, may have left its own files as the checkout's last recorded state. A
        // rebase replaces only the stale result's clean checkout, so that checkout needs a Retry or a restore first.
        string? Recorded(string name) => (fold.Components.GetValueOrDefault(name) as ComponentBaseline.Fixed)?.Value;
        if (Recorded("branch") != code.Commit.Hex || Recorded("head") != location.Owner.Branch ||
            Recorded("files") != code.Tree.Hex || Recorded("index") != code.Tree.Hex)
            throw new Refusal(new(RunProblem.StartConflict, Task: task));
        var state = ReadRecoveryCheckout(repository, record, location, code.Attempt);
        // The fold is the stale result's clean checkout, so a match means nothing changed it since.
        if (BaselineDifference(repository, fold.Components, state) is { } difference) throw Fault(DriftProblem(difference), StaleCheckoutChanged, difference);
        if (state.IndexLock is not null)
            throw Fault(MaterializationProblem.DirtyWorktree, "An index lock holds the checkout. Preserve and restore it first.", new BlockScope.Checkout([], IndexLock: true));

        var changes = Value(repository.DiffTreePaths(Value(repository.ReadCommit(oldBase)).Tree, code.Tree));
        var supported = GitRepository.ParseVersion(repository.Version) is { } version && version >= new Version(2, 43, 0);
        var unsupported = new RebaseCandidate.Unavailable(MaterializationProblem.GitVersionUnsupported,
            $"Rebasing needs Git 2.43 or later. Installed: {repository.Version}.");
        RebaseCandidate candidate;
        CommitRecipe? recipe = null;
        CommitRecipe? join = null;
        CommitId? newBase = null;
        ImmutableArray<CommitId> commits = [.. sources.Select(item => item.Commit).Distinct()];
        if (commits.Length >= 2 && !supported) candidate = unsupported;
        else
        {
            RebaseCandidate? conflict = null;
            if (commits.Length >= 2) (newBase, join, conflict) = JoinSources(repository, record, task, commits);
            else newBase = commits.Length == 1 ? commits[0] : record.Base.Commit;
            if (conflict is not null) candidate = conflict;
            else if (newBase != oldBase && !supported) candidate = unsupported;
            else
            {
                var tree = code.Tree;
                if (newBase != oldBase)
                {
                    repository.RemoveMergeScratchFolders();
                    switch (Mutate("rebase-merge", () => repository.MergeTrees(newBase!.Value, code.Commit, record.Base.Commit, oldBase)))
                    {
                        case TreeMerge.Clean clean:
                            tree = clean.Tree;
                            break;
                        case TreeMerge.Conflicted conflicted:
                            var paths = ConflictPaths(conflicted);
                            conflict = new RebaseCandidate.Conflicted(paths, "The task's change conflicts with its updated inputs" +
                                (paths.IsEmpty ? "." : " in " + string.Join(", ", paths) + "."));
                            break;
                        case TreeMerge.Failed failed:
                            throw Fault(MaterializationProblem.GitFailed, failed.Detail, new BlockScope.Operation());
                    }
                }
                if (conflict is not null) candidate = conflict;
                else
                {
                    var timestamps = Value(repository.CommitterTimestamps([newBase!.Value, code.Commit]));
                    recipe = new(tree, [newBase.Value], $"Rebase onto updated inputs\n\nIDP-Run: {record.Id.Value:D}\nIDP-Task: {task.Value:D}\n" +
                        $"IDP-Rebased-From: {code.Commit.Hex}\n", RebaseIdentity, RebaseIdentity, timestamps.Max());
                    var commit = Value(Mutate("rebase-candidate", () => repository.CreateCommit(recipe)));
                    candidate = new RebaseCandidate.Clean(commit, tree, Value(repository.DiffTreePaths(code.Tree, tree)));
                }
            }
        }
        var identity = Revision.Hash(RunJournal.Canonical(new
        {
            task,
            stale = source.Id,
            previous = previous.Id,
            current = bindings,
            sources,
            oldBase,
            newBase,
            join,
            recipe,
            candidate = candidate switch
            {
                RebaseCandidate.Clean clean => (object)new { kind = "clean", clean.Commit, clean.Tree },
                RebaseCandidate.Conflicted conflicted => new { kind = "conflicted", conflicted.Paths },
                RebaseCandidate.Unavailable unavailable => new { kind = "unavailable", unavailable.Problem, unavailable.Detail },
                _ => throw new InvalidOperationException(),
            },
            // The index's bytes change whenever Git refreshes its stat data, so the preview binds its tree.
            checkout = new { state.Branch, state.SymbolicHead, state.Files, state.IndexTree },
            report = Revision.Hash(source.Report),
            artifacts = source.Artifacts,
        }));
        var preview = new RebasePreview(task, source.Id, previous, bindings, updated, oldBase, newBase, changes, source.Report,
            source.Artifacts, candidate, identity);
        return new(preview, source, code, location, bindings, sources, review, recipe, join, join is null ? null : newBase);
    }

    /// <summary>The current sources' join, built and named the way a consumer's own join is, or the conflict between them.</summary>
    private (CommitId? Commit, CommitRecipe? Recipe, RebaseCandidate? Conflict) JoinSources(GitRepository repository, RunRecord record,
        TaskId task, ImmutableArray<CommitId> parents)
    {
        repository.RemoveMergeScratchFolders();
        var timestamps = Value(repository.CommitterTimestamps(parents));
        var timestamp = timestamps[0];
        var accumulator = parents[0];
        TreeId tree = default;
        for (var step = 1; step < parents.Length; step++)
        {
            if (timestamps[step] > timestamp) timestamp = timestamps[step];
            switch (Mutate("rebase-join-merge-" + step, () => repository.MergeTrees(accumulator, parents[step], record.Base.Commit)))
            {
                case TreeMerge.Clean clean:
                    tree = clean.Tree;
                    if (step < parents.Length - 1)
                        accumulator = Value(Mutate("rebase-join-accumulator-" + step, () => repository.CreateCommit(JoinRecipe(record, task, tree, [accumulator, parents[step]], timestamp))));
                    break;
                case TreeMerge.Conflicted conflicted:
                    var paths = ConflictPaths(conflicted);
                    return (null, null, new RebaseCandidate.Conflicted(paths, "The updated inputs conflict with each other" +
                        (paths.IsEmpty ? "." : " in " + string.Join(", ", paths) + ".")));
                case TreeMerge.Failed failed:
                    throw Fault(MaterializationProblem.GitFailed, failed.Detail, new BlockScope.Operation());
            }
        }
        var recipe = JoinRecipe(record, task, tree, parents, timestamp);
        return (Value(Mutate("rebase-join", () => repository.CreateCommit(recipe))), recipe, null);
    }

    private static CommitRecipe JoinRecipe(RunRecord record, TaskId task, TreeId tree, ImmutableArray<CommitId> parents, DateTimeOffset at) =>
        new(tree, parents, $"Join dependency results\n\nIDP-Run: {record.Id.Value:D}\nIDP-Task: {task.Value:D}\n", RebaseIdentity, RebaseIdentity, at);

    private static ImmutableArray<string> ConflictPaths(TreeMerge.Conflicted conflict) =>
        [.. conflict.Stages.Select(entry => entry.Path).Concat(conflict.Messages
            .Where(message => message.Type.StartsWith("CONFLICT", StringComparison.Ordinal)).SelectMany(message => message.Paths))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    private static MaterializationPlan.Rebase RebasePlan(RunRecord record, RebaseBuild build, OperationId planId, OperationId approval, Digest preview)
    {
        var task = build.Preview.Task;
        var result = new ResultId(OperationIds.Derive(approval, "rebase-result").Value);
        var reference = RunLayout.RebaseRef(record.RunKey!, record.TaskKeys[task], result);
        JoinRecord? join = build.Join is { } recipe ? new(planId, build.Sources, build.JoinCommit!.Value, recipe.Tree, reference) : null;
        var inputs = InputMaterial.Build(record, new InputId(OperationIds.Derive(approval, "rebase-inputs").Value), task, build.Source.Revision,
            build.Bindings, build.Sources, build.Review, join);
        var clean = (RebaseCandidate.Clean)build.Preview.Candidate;
        return new(task, build.Source.Id, result, inputs, build.Code.Commit, build.Recipe!, clean.Commit, reference, approval, preview)
        { JoinRecipe = build.Join };
    }

    private static void VerifyRebaseHead(GitRepository repository, string checkout, string branch, CommitId tip)
    {
        if (Value(repository.SymbolicHead(checkout)) != branch || Value(repository.ResolveCheckoutHead(checkout)) != tip)
            throw Fault(MaterializationProblem.UncertainOwnership, "The checkout HEAD is not on its task branch at the stale result.",
                new BlockScope.Checkout([], Head: true));
    }

    /// <summary>
    /// Requires a clean checkout whose files and index are <paramref name="tree"/>, or <paramref name="target"/> when a
    /// recorded reset may already have run. Returns whether the checkout is already at <paramref name="target"/>.
    /// </summary>
    private bool VerifyRebaseFiles(GitRepository repository, string checkout, TreeId tree, TreeId? target)
    {
        var stages = Value(repository.UnmergedEntries(checkout));
        if (!stages.IsEmpty)
            throw Fault(MaterializationProblem.DirtyWorktree, "The checkout has unresolved stages.",
                new BlockScope.Checkout([.. stages.Select(stage => stage.Path).Distinct().Order(StringComparer.Ordinal)]));
        var capture = Value(Mutate("rebase-capture", () => repository.Capture(checkout)));
        var index = Value(repository.ReadIndexTree(checkout));
        if (capture.IndexBefore == capture.IndexAfter && capture.Tree == index)
        {
            if (capture.Tree == tree) return false;
            if (capture.Tree == target) return true;
        }
        var paths = new SortedSet<string>(Value(repository.DiffTreePaths(tree, capture.Tree)), StringComparer.Ordinal);
        paths.UnionWith(Value(repository.DiffTreePaths(tree, index)));
        throw Fault(MaterializationProblem.DirtyWorktree, StaleCheckoutChanged, new BlockScope.Checkout([.. paths]));
    }

    private static void VerifyRebased(GitRepository repository, string checkout, ExecutionLocation location, MaterializationPlan.Rebase plan, RunRecord record)
    {
        VerifyOwnedCheckout(repository, location, record);
        VerifyResetHead(repository, checkout, location.Owner.Branch, plan.Commit);
        if (Value(repository.ReadRef(plan.Ref)) != plan.Commit)
            throw Fault(MaterializationProblem.UncertainOwnership, "The rebase retention ref changed.", new BlockScope.Refs([plan.Ref]));
        if (Value(repository.Capture(checkout)).Tree != plan.Recipe.Tree || Value(repository.ReadIndexTree(checkout)) != plan.Recipe.Tree ||
            Value(repository.Status(checkout)).Length != 0)
            throw Fault(MaterializationProblem.DirtyWorktree, "The checkout changed after the rebase reset it.", BlockScope.Checkout.Whole);
    }

    private void CopyRebaseArtifacts(WorkflowId workflow, RunId run, ResultRecord source, ResultId result)
    {
        var storage = new RunStorage(_project, workflow, run);
        foreach (var artifact in source.Artifacts)
        {
            var destination = RunStorage.ArtifactPath(result, artifact.Name);
            try
            {
                var bytes = storage.ReadArtifact(source.Id, artifact);
                RunStorage.Publish(storage.Folder, destination, bytes, artifact.Content, artifact.ByteLength);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw Fault(MaterializationProblem.InputUnavailable,
                    $"Result {source.Id.Value:D}, path {artifact.StoredPath}, rebased result path {destination}: {error.Message}", new BlockScope.Operation());
            }
        }
    }

    /// <summary>Resolves the faults this rebase recorded under its approval or its plan once it is verified.</summary>
    private void ResolveRebaseBlocks(CoordinatorPermit permit, OperationId approval, OperationId plan)
    {
        var record = Read(permit.Workflow, permit.Run);
        foreach (var pair in record.Blocks.Where(pair => !pair.Value.Resolved && (pair.Value.Block.Operation == approval || pair.Value.Block.Operation == plan))
            .OrderBy(pair => pair.Key.Value))
            Journal("rebase-resolve-" + pair.Key.Value.ToString("D"), () => _store.Record(permit,
                OperationIds.Derive(approval, "rebase-resolve-" + pair.Key.Value.ToString("D")), new RunEvent.BlockResolved(pair.Key, "Rebase verified.")));
    }

    private Rebasing RebaseBlock(CoordinatorPermit permit, OperationId approval, string step, MaterializationBlock block) =>
        Block(permit, approval, step, block) switch
        {
            Preparation.Blocked blocked => new Rebasing.Blocked(blocked.Block),
            Preparation.Rejected rejected => new Rebasing.Rejected(rejected.Reason),
            _ => throw new InvalidOperationException(),
        };
}
