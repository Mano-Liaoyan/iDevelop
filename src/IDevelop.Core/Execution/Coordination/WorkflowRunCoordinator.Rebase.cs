using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>Review updated inputs (E3f): a stale task's preview and its approved clean rebase, with no client run.</summary>
internal sealed partial class WorkflowRunCoordinator
{
    /// <summary>
    /// Previews the rebase of <paramref name="task"/>'s stale result onto its current inputs. It records nothing, and only
    /// the window that controls the run can preview, because the preview reads the task's checkout under its lease.
    /// </summary>
    public Task<RebasePreviewRead> ReviewUpdatedInputs(RunAddress address, TaskId task, CancellationToken wait = default) =>
        Request(address, task, message => new RebasePreviewRead.Unavailable(message), reason => new RebasePreviewRead.Rejected(reason),
            mark: false, () =>
            {
                if (_permit!.TakeTask(task) is not LeaseTake.Taken taken) return new RebasePreviewRead.Rejected(new(RunProblem.TaskBusy));
                using (taken.Lease) return _materializer().PreviewRebase(taken.Lease);
            }, preview => preview, wait);

    /// <summary>
    /// Rebases <paramref name="task"/>'s stale result as the person approved the preview <paramref name="preview"/> under
    /// <paramref name="command"/>. A changed preview, a conflict, or a changed checkout refuses or blocks, and nothing moves.
    /// Repeating the command returns its receipt. Cancelling <paramref name="wait"/> does not revoke a recorded approval,
    /// which Resume finishes. The outcome names a block with its own problem and scope.
    /// </summary>
    public Task<Rebasing> ApproveRebase(RunAddress address, TaskId task, Digest preview, OperationId command, CancellationToken wait = default) =>
        Request(address, task, message => new Rebasing.Unavailable(message), reason => new Rebasing.Rejected(reason), mark: true,
            () => Rebase(task, command, preview), outcome =>
            {
                Held(task, outcome);
                return outcome;
            }, wait);

    /// <summary>
    /// Runs <paramref name="work"/> off the loop for the controlling window and answers with <paramref name="finish"/> of its
    /// outcome, on the loop. With <paramref name="mark"/> the task counts as settling while it runs, so nothing else starts it.
    /// </summary>
    private Task<T> Request<T, TWork>(RunAddress address, TaskId task, Func<string, T> unavailable, Func<RunRejection, T> refused, bool mark,
        Func<TWork> work, Func<TWork, T> finish, CancellationToken wait) =>
        Request(address, task, unavailable, refused, mark, () => Task.FromResult(work()), finish, wait);

    /// <inheritdoc cref="Request{T, TWork}(RunAddress, TaskId, Func{string, T}, Func{RunRejection, T}, bool, Func{TWork}, Func{TWork, T}, CancellationToken)"/>
    private Task<T> Request<T, TWork>(RunAddress address, TaskId task, Func<string, T> unavailable, Func<RunRejection, T> refused, bool mark,
        Func<Task<TWork>> work, Func<TWork, T> finish, CancellationToken wait)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Post(() =>
            {
                if (_permit is null) done.TrySetResult(unavailable(ElsewhereMessage));
                else if (!Same(address)) done.TrySetResult(refused(new(RunProblem.IdentityMismatch)));
                else if (_live.ContainsKey(task)) done.TrySetResult(refused(new(RunProblem.TaskBusy)));
                else
                {
                    if (mark) _live[task] = new(LiveStage.Settling);
                    Background(work, outcome =>
                    {
                        if (mark) _live.Remove(task);
                        done.TrySetResult(finish(outcome));
                    }, error =>
                    {
                        if (mark) _live.Remove(task);
                        _problem = error.Message;
                        done.TrySetResult(refused(new(RunProblem.StorageUnavailable)));
                    });
                }
            }))
            done.TrySetResult(refused(new(RunProblem.RunStopped)));
        return done.Task.WaitAsync(wait);
    }

    private Rebasing Rebase(TaskId task, OperationId approval, Digest preview)
    {
        if (_permit!.TakeTask(task) is not LeaseTake.Taken taken) return new Rebasing.Rejected(new(RunProblem.TaskBusy));
        using (taken.Lease)
        {
            _runs.Probe?.Invoke("coordinator.rebase.before");
            return _materializer().Rebase(taken.Lease, approval, preview);
        }
    }

    /// <summary>What a rebase's outcome holds: a block until the journal shows it resolved, a transient refusal until its retry.</summary>
    private void Held(TaskId task, Rebasing outcome)
    {
        switch (outcome)
        {
            case Rebasing.Rebased:
                _holds.Remove(task);
                break;
            case Rebasing.Blocked blocked:
                Hold(task, new TaskHold.Blocked(blocked.Block));
                break;
            case Rebasing.Rejected rejected when Transient(rejected.Reason.Problem):
                Hold(task, new TaskHold.Refused(rejected.Reason, null, Transient: true));
                break;
        }
    }

    /// <summary>
    /// Finishes each rebase a person approved whose plan the journal recorded but whose result it does not, as Resume
    /// finishes a pending publication. It reruns the approval's own operation, so it builds no other candidate.
    /// </summary>
    private void FinishRebases(RunRecord record)
    {
        foreach (var (plan, rebase) in record.Plans)
        {
            if (rebase is not MaterializationPlan.Rebase pending || record.Results.Any(result => result.Id == pending.Result)) continue;
            var task = pending.Task;
            if (_live.ContainsKey(task) || _holds.ContainsKey(task)) continue;
            _live[task] = new(LiveStage.Settling);
            Offload(() => Rebase(task, pending.Approval, pending.Preview), outcome =>
            {
                _live.Remove(task);
                Held(task, outcome);
                // Any other refusal waits for Resume, which clears this window's holds, rather than repeating at once.
                if (outcome is Rebasing.Rejected { Reason: var reason } && !Transient(reason.Problem))
                    Hold(task, new TaskHold.Refused(reason, null, Transient: false));
            }, error => Faulted(task, error));
        }
    }
}
