using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>What a person's Retry of a run's task did.</summary>
internal abstract record RetryReply
{
    private RetryReply() { }

    /// <summary>The new attempt is reserved and prepared. It starts at once, or once a client slot is free at the run's bound.</summary>
    internal sealed record Reserved(AttemptId Attempt) : RetryReply;

    /// <summary>A recorded block holds the task's checkout, such as a salvage whose observations differ.</summary>
    internal sealed record Blocked(MaterializationBlock Block) : RetryReply;

    internal sealed record Refused(RunRejection Reason) : RetryReply;

    /// <summary>Another window controls the run. Nothing was recorded.</summary>
    internal sealed record Unavailable(string Message) : RetryReply;
}

/// <summary>Retry (E3h.1): a new attempt of a run's task that ended without a result, in a fresh client session.</summary>
internal sealed partial class WorkflowRunCoordinator
{
    /// <summary>
    /// Retries <paramref name="task"/>, whose newest attempt ended without a result, as the person confirmed under
    /// <paramref name="confirmation"/>. The attempt's unfinished work is salvaged, a checked reset puts the checkout back
    /// where the attempt started, and the new attempt is reserved with <see cref="AttemptCause.Retry"/>. Repeating the
    /// confirmation returns the same attempt; it makes one attempt per confirmation.
    /// </summary>
    internal Task<RetryReply> Retry(RunAddress address, TaskId task, OperationId confirmation, CancellationToken wait = default) =>
        Converse<RetryReply>(address, (record, complete) => RetryCore(record, task, confirmation, complete), command => command switch
        {
            RunCommand.Unavailable unavailable => new RetryReply.Unavailable(unavailable.Message),
            RunCommand.Refused refused => new RetryReply.Refused(refused.Reason),
            _ => throw new InvalidOperationException(),
        }, wait);

    /// <summary>Whether a run's task offers Retry: an agent's task whose newest attempt ended without a result.</summary>
    internal static bool OffersRetry(Workflow snapshot, TaskView? view) =>
        view is { State: TaskState.Failed, Attempt: not null, Result: null } &&
        snapshot.Tasks.GetValueOrDefault(view.Task)?.Blueprint.Work is WorkSpec.Agent;

    private void RetryCore(RunRecord record, TaskId task, OperationId confirmation, Action<RetryReply> complete)
    {
        RetryReply Refused(RunProblem problem) => new RetryReply.Refused(new(problem, Task: task));
        if (record.Phase != RunPhase.Approved)
        {
            complete(Refused(RunProblem.RunStopped));
            return;
        }
        if (confirmation.Value == Guid.Empty)
        {
            complete(Refused(RunProblem.ConfirmationRequired));
            return;
        }
        // A confirmation is one choice: it converges on its attempt and never makes another.
        if (record.Attempts.Values.FirstOrDefault(attempt => attempt.Task == task &&
                attempt.Cause is AttemptCause.Retry { Confirmation: var chosen } && chosen == confirmation) is { } reserved)
        {
            complete(new RetryReply.Reserved(reserved.Id));
            return;
        }
        if (_live.ContainsKey(task))
        {
            complete(Refused(RunProblem.TaskBusy));
            return;
        }
        var view = Project(record).Tasks.GetValueOrDefault(task);
        if (view is { State: TaskState.Ready or TaskState.Starting or TaskState.Running or TaskState.Settling or TaskState.Waiting })
        {
            complete(Refused(RunProblem.TaskBusy));
            return;
        }
        if (!OffersRetry(record.Revision.Snapshot, view) || !record.Closures.ContainsKey(view!.Attempt!.Value))
        {
            complete(Refused(RunProblem.InvalidClaim));
            return;
        }
        var previous = view.Attempt!.Value;
        var cause = new AttemptCause.Retry(previous, confirmation);
        _live[task] = new(LiveStage.Settling);
        Background(() => Reserve(record, task, previous, cause, confirmation), reply =>
        {
            _live.Remove(task);
            if (reply is RetryReply.Reserved) _holds.Remove(task);
            complete(reply);
        }, error =>
        {
            _live.Remove(task);
            _problem = error.Message;
            complete(Refused(RunProblem.StorageUnavailable));
        });
    }

    /// <summary>
    /// Under the task's lease: salvages and resets the checkout the previous attempt left, then reserves and prepares the
    /// retry. Each step derives its operation from the confirmation, so a repeat after a crash or a refusal converges.
    /// </summary>
    private async Task<RetryReply> Reserve(RunRecord record, TaskId task, AttemptId previous, AttemptCause.Retry cause, OperationId confirmation)
    {
        if (_permit!.TakeTask(task) is not LeaseTake.Taken taken) return new RetryReply.Refused(new(RunProblem.TaskBusy));
        using var lease = taken.Lease;
        var materializer = Commanded();
        RetryReply Refused(RunRejection reason) => new RetryReply.Refused(reason);
        if (record.Preparations.ContainsKey(new(previous, 1)))
        {
            _runs.Probe?.Invoke("coordinator.retry.salvage");
            switch (await materializer.Salvage(lease, OperationIds.Derive(confirmation, "retry/salvage"), previous).ConfigureAwait(false))
            {
                case Salvage.Blocked blocked: return new RetryReply.Blocked(blocked.Block);
                case Salvage.Rejected rejected: return Refused(rejected.Reason);
                case Salvage.Retained retained:
                    switch (materializer.ResetForRetry(lease, OperationIds.Derive(confirmation, "retry/reset"), retained.Receipt.Plan, confirmation))
                    {
                        case RetryReset.Blocked blocked: return new RetryReply.Blocked(blocked.Block);
                        case RetryReset.Rejected rejected: return Refused(rejected.Reason);
                    }
                    break;
            }
        }
        return await materializer.Prepare(lease, RunOperations.First(Address.Run, task, cause), cause, agents: _runs.AgentOffers).ConfigureAwait(false) switch
        {
            Preparation.Ready ready => new RetryReply.Reserved(ready.Execution.Launch.Attempt),
            Preparation.Blocked blocked => new RetryReply.Blocked(blocked.Block),
            Preparation.Rejected rejected => Refused(rejected.Reason),
            _ => throw new InvalidOperationException(),
        };
    }
}
