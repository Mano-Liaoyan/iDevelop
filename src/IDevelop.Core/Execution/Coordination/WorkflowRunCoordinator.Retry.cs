using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>What a person's Retry of a run's task did.</summary>
internal abstract record RetryReply
{
    private RetryReply() { }

    /// <summary>The new attempt is reserved and prepared. It starts at once, or once a client slot is free at the run's bound.</summary>
    internal sealed record Reserved(AttemptId Attempt) : RetryReply;

    /// <summary>A recorded block holds the task's preparation, such as a worktree that no longer matches the run's records.</summary>
    internal sealed record Blocked(MaterializationBlock Block) : RetryReply;

    internal sealed record Refused(RunRejection Reason) : RetryReply;

    /// <summary>Another window controls the run. Nothing was recorded.</summary>
    internal sealed record Unavailable(string Message) : RetryReply;
}

/// <summary>
/// Retry (E3h.1, E3h.3): a new attempt of a run's task that ended, in a fresh client session. A task whose result went out
/// of date, or whose unstarted attempt's inputs changed, runs again by itself the same way.
/// </summary>
internal sealed partial class WorkflowRunCoordinator
{
    /// <summary>
    /// Retries <paramref name="task"/>, whose newest attempt ended without a result, as the person confirmed under
    /// <paramref name="confirmation"/>. The new attempt is reserved with <see cref="AttemptCause.Retry"/> and continues in the
    /// failed attempt's worktree with its files as they are (ADR 0032). Repeating the confirmation returns the same attempt;
    /// it makes one attempt per confirmation. A <paramref name="note"/> the person wrote ends the agent's first prompt (#126).
    /// </summary>
    internal Task<RetryReply> Retry(RunAddress address, TaskId task, OperationId confirmation, string? note = null, CancellationToken wait = default) =>
        Converse<RetryReply>(address, (record, complete) => RetryCore(record, task, confirmation, string.IsNullOrWhiteSpace(note) ? null : note.Trim(), complete), command => command switch
        {
            RunCommand.Unavailable unavailable => new RetryReply.Unavailable(unavailable.Message),
            RunCommand.Refused refused => new RetryReply.Refused(refused.Reason),
            _ => throw new InvalidOperationException(),
        }, wait);

    /// <summary>
    /// Whether a run's task offers Retry: an agent's task that is done (#125), or whose newest attempt ended without a result
    /// and fixed no review round, such as one whose result the run refused to accept, or a Merge node whose attempt ended
    /// without resolving the conflict that went to it.
    /// </summary>
    internal static bool OffersRetry(Workflow snapshot, TaskView? view) =>
        view is { State: TaskState.Failed or TaskState.Done, Attempt: not null } or { State: TaskState.Refused, Attempt: not null, End: not null } &&
        snapshot.Tasks.GetValueOrDefault(view.Task)?.Blueprint.Work switch
        {
            WorkSpec.Agent => view.State == TaskState.Done || !view.Fixes,
            WorkSpec.Merge => view is { State: TaskState.Failed, Merge: not null },
            _ => false,
        };

    private void RetryCore(RunRecord record, TaskId task, OperationId confirmation, string? note, Action<RetryReply> complete)
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
        var cause = new AttemptCause.Retry(previous, confirmation) { Note = note };
        // The ended attempt's settled turn whose disposition the run refused still holds the task's lease. The retry
        // replaces that attempt, so its lease goes, and its durable record stays as it is.
        if (_handles.Remove(task, out var refused)) refused.Lease.Dispose();
        _live[task] = new(LiveStage.Settling);
        Background(() => Reserve(task, cause), reply =>
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
    /// Under the task's lease: reserves and prepares the retry in the task's worktree. Its operation derives from the
    /// confirmation, so a repeat after a crash or a refusal converges.
    /// </summary>
    private async Task<RetryReply> Reserve(TaskId task, AttemptCause.Retry cause)
    {
        if (_permit!.TakeTask(task) is not LeaseTake.Taken taken) return new RetryReply.Refused(new(RunProblem.TaskBusy));
        using var lease = taken.Lease;
        var materializer = Commanded();
        RetryReply Refused(RunRejection reason) => new RetryReply.Refused(reason);
        return await materializer.Prepare(lease, RunOperations.First(Address.Run, task, cause), cause, agents: _runs.AgentOffers).ConfigureAwait(false) switch
        {
            Preparation.Ready ready => new RetryReply.Reserved(ready.Execution.Launch.Attempt),
            Preparation.Blocked blocked => new RetryReply.Blocked(blocked.Block),
            Preparation.Rejected rejected => Refused(rejected.Reason),
            _ => throw new InvalidOperationException(),
        };
    }
}
