using IDevelop.Workflows;

namespace IDevelop.Execution;

internal sealed partial class Materializer
{
    public async ValueTask<Preparation> PrepareTurn(RunLease lease, OperationId operation, LaunchKey key, string prompt,
        CancellationToken cancellation = default)
    {
        using var authority = lease.Use();
        if (authority is null) return new Preparation.Rejected(new(RunProblem.TaskBusy));
        if (LeaseProblem(lease) is { } problem) return new Preparation.Rejected(new(problem));
        var permit = lease.Permit;
        var workflow = permit.Workflow;
        var run = permit.Run;
        var task = lease.Task;
        InputId? inputs = null;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var record = Read(workflow, run);
            if (record.Phase != RunPhase.Approved) return new Preparation.Rejected(new(RunProblem.RunStopped));
            if (!record.Attempts.TryGetValue(key.Attempt, out var attempt))
                return new Preparation.Rejected(new(RunProblem.UnknownAttempt));
            if (LeaseProblem(lease, attempt.Task) is { } mismatch) return new Preparation.Rejected(new(mismatch));
            var previous = new LaunchKey(key.Attempt, key.Turn - 1);
            if (key.Turn < 2 || record.Closures.ContainsKey(key.Attempt) || !record.TurnClosures.ContainsKey(previous) ||
                !record.Preparations.TryGetValue(previous, out var old))
                return new Preparation.Rejected(new(RunProblem.InvalidClaim));
            inputs = old.Inputs;
            var repository = OpenRepository();
            using var mutation = repository.TakeMutationLock();
            if (mutation is null) return new Preparation.Rejected(new(RunProblem.JournalBusy));
            record = Read(workflow, run);
            VerifyRepository(record, repository);
            var storage = new RunStorage(_project, workflow, run);
            var refreshPair = record.Plans.SingleOrDefault(pair => pair.Value is MaterializationPlan.Refresh refresh && refresh.Launch == key);
            VerifyCheckout(repository, old.Location, keepChanges: true, record);
            var changed = record.Inputs[old.Inputs].Review is { } review && record.CurrentResults.GetValueOrDefault(review.Subject)?.Id != review.SubjectResult;
            if (record.Preparations.TryGetValue(key, out var existing))
            {
                var expectedPrompt = existing.Inputs == old.Inputs ? prompt : Prompt(record.Revisions[attempt.Revision].Snapshot.Tasks[task],
                    record.Inputs[existing.Inputs], attempt.Id, prompt);
                if (existing.Prompt != expectedPrompt) return new Preparation.Rejected(new(RunProblem.OperationConflict));
                if (record.Inputs[existing.Inputs].Code is CodeSelection.Joined existingJoin)
                    VerifyJoin(record, repository, task, existing.Inputs,
                        refreshPair.Value is MaterializationPlan.Refresh refreshed ? refreshed.Sources :
                            record.Plans.Values.OfType<MaterializationPlan.Preparation>().Single(plan => plan.Attempt == key.Attempt).Sources,
                        existingJoin.Join);
                VerifyDelivery(record, existing, repository);
                ResolveMaintenanceBlocks(permit, operation, "Prepared.");
                return new Preparation.Ready(existing, Checkout(repository, existing.Location.Owner));
            }
            var execution = old with { Launch = key, Prompt = prompt, PromptHash = Revision.Hash(prompt) };
            if (changed || refreshPair.Value is MaterializationPlan.Refresh)
            {
                var checkout = Checkout(repository, old.Location.Owner);
                var captureTree = Value(Mutate("refresh-capture", () => repository.Capture(checkout))).Tree;
                var indexTree = Value(repository.ReadIndexTree(checkout));
                var oldTree = Value(repository.ReadCommit(old.Location.AttemptBase)).Tree;
                var resetPending = refreshPair.Value is MaterializationPlan.Refresh refreshPlan && record.GitIntents.Values.Any(intent =>
                    intent.Plan == refreshPair.Key && intent.Mutation is GitMutation.ResetCheckout);
                var resetTree = resetPending ? Value(repository.ReadCommit(record.Inputs[((MaterializationPlan.Refresh)refreshPair.Value!).Inputs].CodeBase)).Tree : oldTree;
                if (captureTree != indexTree || captureTree != oldTree && captureTree != resetTree)
                    throw Fault(MaterializationProblem.DirtyWorktree, "The reviewer checkout must be clean before refreshing its inputs.", BlockScope.Checkout.Whole);
                JoinRecord? join = (refreshPair.Value as MaterializationPlan.Refresh)?.Composed;
                if (refreshPair.Value is not MaterializationPlan.Refresh)
                {
                    var capture = RunStore.Inputs(record, task, attempt.Revision, new(OperationIds.Derive(operation, "refresh-input").Value));
                    if (capture.Rejection is { } rejection) throw new Refusal(rejection);
                    VerifyForwarding(record, task, attempt.Revision, capture.Inputs!.Bindings);
                    var sources = InputMaterial.Sources(record, capture.Inputs!.Bindings);
                    if (sources.Select(source => source.Commit).Distinct().Count() >= 2)
                    {
                        inputs = capture.Inputs.Id;
                        var reference = RunLayout.JoinBranch(record.RunKey!, record.TaskKeys[task]);
                        var outcome = await _joins.Compose(new(permit, OperationIds.Derive(operation, "join"), task,
                            inputs.Value, sources, Value(repository.ReadRef(reference))), cancellation);
                        if (outcome is JoinOutcome.Blocked blocked)
                            return Block(permit, operation, "join", blocked.Block with { Operation = operation, Attempt = key.Attempt });
                        join = ((JoinOutcome.Ready)outcome).Join;
                        VerifyJoin(Read(workflow, run), repository, task, inputs.Value, sources, join);
                    }
                }
                else if (join is not null)
                {
                    var persisted = (MaterializationPlan.Refresh)refreshPair.Value;
                    VerifyJoin(record, repository, task, persisted.Inputs, persisted.Sources, join);
                }
                cancellation.ThrowIfCancellationRequested();
                var refreshed = Journal("refresh-plan", () => _store.Refresh(lease, OperationIds.Derive(operation, "refresh-plan"), key, join));
                record = DecisionRecord(refreshed);
                var plan = (MaterializationPlan.Refresh)((RunEvent.Planned)DecisionEvent(refreshed)).Plan;
                var planId = record.Plans.Single(pair => RunReducer.Same(pair.Value, plan)).Key;
                var input = record.Inputs[plan.Inputs];
                inputs = input.Id;
                if (RunReducer.InputProblem(record, input, true) is { } stale) throw new Refusal(stale);
                var retained = RunLayout.ResalvageRef(record.RunKey!, record.TaskKeys[task], key.Attempt, planId);
                RequirePublication(_refs.Publish(permit, operation, planId, "refresh-retain", repository,
                    new(retained, null, old.Location.AttemptBase)), new BlockScope.Refs([retained]));
                RequirePublication(_refs.Publish(permit, operation, planId, "refresh-branch", repository,
                    new(old.Location.Owner.Branch, old.Location.AttemptBase, input.CodeBase)), new BlockScope.Checkout([], Branch: true));
                var reset = new GitMutation.ResetCheckout(task, input.CodeBase);
                if (!PublicationObserved(record, planId, reset))
                {
                    var intended = OperationIds.Derive(operation, "refresh-reset-intent");
                    Journal("refresh-reset-intent", () => _store.Record(permit, intended, new RunEvent.GitIntended(planId, reset)));
                    VerifyIgnoredObstructions(repository, checkout, Value(repository.TreeFiles(input.CodeBase)));
                    var result = Mutate("refresh-reset", () =>
                    {
                        VerifyResetHead(repository, checkout, old.Location.Owner.Branch, input.CodeBase);
                        return repository.ResetCheckout(checkout, input.CodeBase);
                    });
                    if (result.ExitCode != 0) throw Fault(MaterializationProblem.GitFailed, result.Stderr, BlockScope.Checkout.Whole);
                    Journal("refresh-reset-observed", () => _store.Record(permit, OperationIds.Derive(operation, "refresh-reset-observed"),
                        new RunEvent.GitObserved(intended, new(false, input.CodeBase.Hex))));
                }
                var location = old.Location with { AttemptBase = input.CodeBase };
                VerifyCheckout(repository, location, keepChanges: false, Read(workflow, run));
                Mutate("refresh-delivery", () => Deliver(record, input, repository, location.Owner, storage));
                var refreshedPrompt = Prompt(record.Revisions[attempt.Revision].Snapshot.Tasks[task], input, attempt.Id, prompt);
                execution = execution with { Inputs = input.Id, Location = location, Prompt = refreshedPrompt, PromptHash = Revision.Hash(refreshedPrompt) };
            }
            var refs = Snapshot(storage, operation, repository, record);
            Journal("prepared", () => _store.Record(permit, OperationIds.Derive(operation, "prepared"), new RunEvent.Prepared(execution, refs)));
            ResolveMaintenanceBlocks(permit, operation, "Prepared.");
            return new Preparation.Ready(execution, Checkout(repository, execution.Location.Owner));
        }
        catch (Refusal refused) { return new Preparation.Rejected(refused.Reason); }
        catch (MaterializationFailure failed)
        { return Block(permit, operation, "turn", new(operation, task, key.Attempt, failed.Problem, inputs, [], failed.Message) { Scope = failed.Scope }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return Block(permit, operation, "turn", new(operation, task, key.Attempt, MaterializationProblem.InputUnavailable, inputs, [], error.Message) { Scope = new BlockScope.Operation() }); }
    }
}
