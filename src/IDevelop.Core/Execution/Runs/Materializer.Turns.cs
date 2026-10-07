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
            VerifyRepository(record, repository);
            using var held = TakeTaskLock(task);
            VerifyCheckout(repository, old.Location, keepChanges: true, record);
            var storage = new RunStorage(_project, workflow, run);
            var execution = old with { Launch = key, Prompt = prompt, PromptHash = Revision.Hash(prompt) };
            if (record.Preparations.TryGetValue(key, out var existing))
            {
                if (!RunReducer.Same(existing, execution)) return ValueTask.FromResult<Preparation>(new Preparation.Rejected(new(RunProblem.OperationConflict)));
                return ValueTask.FromResult<Preparation>(new Preparation.Ready(existing, Checkout(repository, existing.Location.Owner)));
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
