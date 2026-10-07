using IDevelop.Workflows;

namespace IDevelop.Execution;

internal sealed partial class Materializer
{
    public ValueTask<Preparation> PrepareTurn(WorkflowId workflow, RunId run, OperationId operation, LaunchKey key, string prompt,
        CancellationToken cancellation = default)
    {
        TaskId task = default;
        InputId? inputs = null;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var record = Read(workflow, run);
            if (record.Phase != RunPhase.Approved) return ValueTask.FromResult<Preparation>(new Preparation.Rejected(new(RunProblem.RunStopped)));
            if (!record.Attempts.TryGetValue(key.Attempt, out var attempt))
                return ValueTask.FromResult<Preparation>(new Preparation.Rejected(new(RunProblem.UnknownAttempt)));
            task = attempt.Task;
            var previous = new LaunchKey(key.Attempt, key.Turn - 1);
            if (key.Turn < 2 || record.Closures.ContainsKey(key.Attempt) || !record.TurnClosures.ContainsKey(previous) ||
                !record.Preparations.TryGetValue(previous, out var old))
                return ValueTask.FromResult<Preparation>(new Preparation.Rejected(new(RunProblem.InvalidClaim)));
            inputs = old.Inputs;
            var repository = OpenRepository();
            using var mutation = repository.TakeMutationLock();
            if (mutation is null) return ValueTask.FromResult<Preparation>(new Preparation.Rejected(new(RunProblem.JournalBusy)));
            record = Read(workflow, run);
            VerifyRepository(record, repository);
            using var held = TakeTaskLock(task);
            VerifyCheckout(repository, old.Location, keepChanges: true, record);
            var storage = new RunStorage(_project, workflow, run);
            var refreshPair = record.Plans.SingleOrDefault(pair => pair.Value is MaterializationPlan.Refresh refresh && refresh.Launch == key);
            var changed = record.Inputs[old.Inputs].Review is { } review && record.CurrentResults.GetValueOrDefault(review.Subject)?.Id != review.SubjectResult;
            if (record.Preparations.TryGetValue(key, out var existing))
            {
                var expectedPrompt = existing.Inputs == old.Inputs ? prompt : Prompt(record.Revisions[attempt.Revision].Snapshot.Tasks[task],
                    record.Inputs[existing.Inputs], attempt.Id, prompt);
                if (existing.Prompt != expectedPrompt) return ValueTask.FromResult<Preparation>(new Preparation.Rejected(new(RunProblem.OperationConflict)));
                VerifyDelivery(record, existing, repository);
                return ValueTask.FromResult<Preparation>(new Preparation.Ready(existing, Checkout(repository, existing.Location.Owner)));
            }
            var execution = old with { Launch = key, Prompt = prompt, PromptHash = Revision.Hash(prompt) };
            if (changed || refreshPair.Value is MaterializationPlan.Refresh)
            {
                var checkout = Checkout(repository, old.Location.Owner);
                if (Value(repository.Status(checkout)).Length != 0)
                    throw Fault(MaterializationProblem.DirtyWorktree, "The reviewer checkout must be clean before refreshing its inputs.");
                var refreshed = Journal("refresh-plan", () => _store.Refresh(workflow, run, OperationIds.Derive(operation, "refresh-plan"), key));
                record = DecisionRecord(refreshed);
                var plan = (MaterializationPlan.Refresh)((RunEvent.Planned)DecisionEvent(refreshed)).Plan;
                var planId = record.Plans.Single(pair => RunReducer.Same(pair.Value, plan)).Key;
                var input = record.Inputs[plan.Inputs];
                inputs = input.Id;
                if (RunReducer.InputProblem(record, input, true) is { } stale) throw new Refusal(stale);
                var reset = new GitMutation.ResetCheckout(task, input.CodeBase);
                if (!PublicationObserved(record, planId, reset))
                {
                    var intended = OperationIds.Derive(operation, "refresh-reset-intent");
                    Journal("refresh-reset-intent", () => _store.Record(workflow, run, intended, new RunEvent.GitIntended(planId, reset)));
                    // Hard reset is safe here because the reviewer checkout has no tracked or nonignored untracked changes.
                    var result = Mutate("refresh-reset", () => repository.ResetCheckout(checkout, input.CodeBase));
                    if (result.ExitCode != 0) throw Fault(MaterializationProblem.GitFailed, result.Stderr);
                    Journal("refresh-reset-observed", () => _store.Record(workflow, run, OperationIds.Derive(operation, "refresh-reset-observed"),
                        new RunEvent.GitObserved(intended, new(false, input.CodeBase.Hex))));
                }
                var location = old.Location with { AttemptBase = input.CodeBase };
                VerifyCheckout(repository, location, keepChanges: false, Read(workflow, run));
                Mutate("refresh-delivery", () => Deliver(record, input, repository, location.Owner, storage));
                var refreshedPrompt = Prompt(record.Revisions[attempt.Revision].Snapshot.Tasks[task], input, attempt.Id, prompt);
                execution = execution with { Inputs = input.Id, Location = location, Prompt = refreshedPrompt, PromptHash = Revision.Hash(refreshedPrompt) };
            }
            var refs = Snapshot(storage, operation, repository, record);
            Journal("prepared", () => _store.Record(workflow, run, OperationIds.Derive(operation, "prepared"), new RunEvent.Prepared(execution, refs)));
            return ValueTask.FromResult<Preparation>(new Preparation.Ready(execution, Checkout(repository, execution.Location.Owner)));
        }
        catch (Refusal refused) { return ValueTask.FromResult<Preparation>(new Preparation.Rejected(refused.Reason)); }
        catch (MaterializationFailure failed)
        { return ValueTask.FromResult(Block(workflow, run, operation, "turn", new(operation, task, key.Attempt, failed.Problem, inputs, [], failed.Message))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return ValueTask.FromResult(Block(workflow, run, operation, "turn", new(operation, task, key.Attempt, MaterializationProblem.InputUnavailable, inputs, [], error.Message))); }
    }
}
