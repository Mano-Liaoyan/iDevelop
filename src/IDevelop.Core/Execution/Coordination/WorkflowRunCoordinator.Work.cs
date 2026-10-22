using IDevelop.Workflows;

namespace IDevelop.Execution;

internal sealed partial class WorkflowRunCoordinator
{
    /// <summary>The decisions for one durable reread. Returns the record that the decisions' own journal writes left.</summary>
    private RunRecord Act(RunRecord record)
    {
        foreach (var (task, hold) in _holds.ToArray())
        {
            // A start's block that the journal shows resolved by now no longer holds the task.
            if (hold is TaskHold.Blocked blocked && record.Blocks.Values.Any(state => state.Resolved && RunReducer.Same(state.Block, blocked.Block)))
                _holds.Remove(task);
            // A start refused while a producer settled, or on inputs that changed since, starts again once the producer's
            // newer result is current, with the new inputs (#125).
            else if (hold is TaskHold.Refused { Reason.Problem: RunProblem.UnresolvedOwnership or RunProblem.StaleInput } && InputsMoved(record, task))
                _holds.Remove(task);
        }
        ReleaseClosed(record);
        var view = Project(record);
        switch (record.Phase)
        {
            case RunPhase.Approved when _resumed:
                Reconcile(record, view);
                RequestGates(record, view);
                HandOnMerges(record, view);
                Dispatch(record, view);
                record = Complete(record, view);
                break;
            case RunPhase.StopRequested:
                if (!_stopping)
                {
                    // What held a start back no longer matters; each task's closure gets one try before it may hold.
                    _stopping = true;
                    _ = _stop.CancelAsync();
                    _holds.Clear();
                    view = Project(record);
                }
                StopWork(record, view);
                Reconcile(record, view);
                record = Stopped(record);
                break;
        }
        if (record.Phase is RunPhase.Completed or RunPhase.Stopped or RunPhase.Failed) ReleasePins();
        return record;
    }

    /// <summary>
    /// Concludes the reviews that agreed or failed. Then starts every resting attempt's next turn that has text for its
    /// agent, every ready task, and every review's next reviewer turn or fix round, in that order and each in task order,
    /// while the run's client roots stay under <see cref="ProjectRuns.ClientRoots"/>. Below that bound they all start at
    /// once, so the order only decides which wait once the bound is reached (ADR 0018).
    /// </summary>
    private void Dispatch(RunRecord record, RunView view)
    {
        EndFixes(record, view);
        ConcludeReviews(record, view);
        var free = _runs.ClientRoots - Slotted.Length;
        free -= Continue(record, view, free);
        ResolveConflicts(record, view);
        free = _runs.ClientRoots - Slotted.Length;
        free -= StartReady(record, view, free);
        AdvanceReviews(record, view, free);
    }

    internal const string InputsChangedReason = "Its inputs changed before it started.";

    /// <summary>Starts each ready task, in task order, up to <paramref name="free"/> of them, and returns how many started.</summary>
    private int StartReady(RunRecord record, RunView view, int free)
    {
        var started = 0;
        foreach (var next in view.Tasks.Values)
        {
            if (started >= free) break;
            // A reserved fix of a review round resumes from its review, which knows its prompt. A Merge node's own step
            // hands on as a request does, and only its attempts that resolve a conflict start here.
            if (next.State != TaskState.Ready || _live.ContainsKey(next.Task) || !next.Reruns && FixesReview(record, next)) continue;
            // A task that runs again by itself starts a Retry of its newest attempt, under a confirmation every window
            // derives the same way: of the attempt that published its out-of-date result, or of its unstarted attempt
            // whose inputs changed, which closes as not started first (#125).
            var rerun = next is { Reruns: true, Attempt: { } previous } ? new AttemptCause.Retry(previous, RunOperations.Rerun(Address.Run, previous)) : null;
            if (record.Revision.Snapshot.Tasks[next.Task].Blueprint.Work is WorkSpec.Merge && (next.Attempt is not { } merge || !RunReducer.Merges(record, merge)))
                continue;
            Start(record, next, rerun);
            started++;
        }
        return started;
    }

    private void Start(RunRecord record, TaskView next, AttemptCause.Retry? rerun = null)
    {
        var task = next.Task;
        // A reserved attempt resumes with its own cause and operation; a task without one starts its initial attempt.
        var cause = rerun ?? (next.Attempt is { } reserved ? record.Attempts[reserved].Cause : new AttemptCause.Initial());
        var operation = RunOperations.First(Address.Run, task, cause);
        var revision = record.Revision.Id;
        var unstarted = rerun is not null && !record.Closures.ContainsKey(rerun.Previous) ? rerun : null;
        var prompt = FirstPrompt(record, next, cause);
        _live[task] = new(LiveStage.Starting);
        _runs.Probe?.Invoke("coordinator.dispatch");
        Background(async () => unstarted is not null && CloseChanged(task, unstarted) is { } refusal
                ? new TurnStart.Refused(refusal) : await StartTurn(new TurnIntent.First(operation, task, cause, prompt)).ConfigureAwait(false),
            start => Started(task, start, revision), error =>
            {
                _live.Remove(task);
                _problem = error.Message;
                HoldStart(task, new TaskHold.Refused(new(RunProblem.StorageUnavailable), null, Transient: true));
            });
    }

    /// <summary>
    /// Whether the task's newest attempt has not started, fixes no review round, and took a result that is no longer
    /// current, so it starts again with the new inputs.
    /// </summary>
    private static bool InputsMoved(RunRecord record, TaskId task) =>
        RunProjection.LatestAttempts(record).GetValueOrDefault(task) is var latest && latest != default &&
        !record.Closures.ContainsKey(latest) && !record.Claims.Keys.Any(key => key.Attempt == latest) && record.ReviewOf(latest) is null &&
        record.Inputs[record.Attempts[latest].InitialInputs].Bindings.OfType<InputBinding.Provided>()
            .Any(binding => record.CurrentResults.GetValueOrDefault(binding.Edge.From)?.Id != binding.Result);

    /// <summary>Under the task's lease, closes the unstarted attempt a rerun replaces as not started, since its inputs changed.</summary>
    private RunRejection? CloseChanged(TaskId task, AttemptCause.Retry rerun)
    {
        if (_permit!.TakeTask(task) is not LeaseTake.Taken taken) return new(RunProblem.TaskBusy);
        using (taken.Lease)
        {
            return _store.Recover(taken.Lease, RunOperations.NotStarted(rerun.Confirmation, rerun.Previous), rerun.Previous,
                RecoveryOutcome.NotStarted, rerun.Confirmation, InputsChangedReason) is RunDecision.Rejected rejected ? rejected.Reason : null;
        }
    }

    /// <summary>A refused or blocked start holds its task only while the run goes on. Once it stops, the stop closes the attempt.</summary>
    private void HoldStart(TaskId task, TaskHold hold)
    {
        if (!_stopping) Hold(task, hold);
    }

    /// <param name="dispatched">
    /// The run's revision when the start was dispatched. A start an amendment refused since, with
    /// <see cref="RunProblem.RevisionConflict"/>, is tried again, because its next preparation plans at the amended revision.
    /// </param>
    private void Started(TaskId task, TurnStart start, RevisionId? dispatched = null)
    {
        switch (start)
        {
            case TurnStart.Started started:
                Track(task, started.Turn);
                break;
            case TurnStart.Settled settled:
                Settled(task, settled.Settlement);
                break;
            case TurnStart.Existing { Running: { } running }:
                Track(task, running);
                break;
            case TurnStart.Existing { Settlement: { } recorded }:
                Settled(task, recorded);
                break;
            case TurnStart.Existing:
                // Another window claimed it. The next decision reconciles the recorded claim.
                _live.Remove(task);
                break;
            case TurnStart.Blocked blocked:
                _live.Remove(task);
                HoldStart(task, new TaskHold.Blocked(blocked.Block));
                break;
            case TurnStart.Refused refused:
                _live.Remove(task);
                if (refused.Reason.Problem != RunProblem.RunStopped)
                    // A start whose inputs changed before its claim prepares again with the new ones once the hold clears,
                    // unless it fixes a review round, which its review goes on with (#125).
                    HoldStart(task, new TaskHold.Refused(refused.Reason, refused.Client, Transient(refused.Reason.Problem) ||
                        refused.Reason.Problem == RunProblem.StaleInput && Record() is { } now &&
                            RunProjection.LatestAttempts(now).GetValueOrDefault(task) is var latest && (latest == default || now.ReviewOf(latest) is null) ||
                        refused.Reason.Problem == RunProblem.RevisionConflict && dispatched is { } before && Record()?.Revision.Id != before));
                break;
        }
    }

    private void Faulted(TaskId task, Exception error)
    {
        _live.Remove(task);
        _problem = error.Message;
        Hold(task, new TaskHold.Refused(new(RunProblem.StorageUnavailable), null, Transient: true));
    }

    /// <summary>Holds a client slot of the run until the turn's root exits, then waits for its settlement without one.</summary>
    private void Track(TaskId task, RunningTurn turn)
    {
        _live[task] = new(LiveStage.Running, turn);
        // A root that exits or fails to be observed frees the slot. Settlement goes on without it.
        void Exited()
        {
            if (_live.GetValueOrDefault(task) is { Stage: LiveStage.Running } live && live.Turn == turn) _live[task] = live with { Stage = LiveStage.Settling };
        }
        Background(async () =>
        {
            await turn.RootExited.ConfigureAwait(false);
            return true;
        }, _ => Exited(), _ => Exited());
        Background(() => turn.Settlement, settlement => Settled(task, settlement), _ =>
        {
            _live.Remove(task);
            Hold(task, new TaskHold.Unresolved(UnresolvedReason.IncompleteEvidence, null, Transient: true));
        });
    }

    private void Settled(TaskId task, TurnSettlement settlement)
    {
        switch (settlement)
        {
            case TurnSettlement.Settled settled:
                Dispose(task, settled.Turn);
                break;
            case TurnSettlement.Unresolved unresolved:
                _live.Remove(task);
                var turn = unresolved.Turn;
                _unresolved[task] = turn;
                Hold(task, new TaskHold.Unresolved(turn.Reason, turn.Rejection, Transient(turn.Rejection?.Problem)));
                break;
        }
    }

    /// <summary>Gives a settled turn its disposition and lets go of its lease against the receipt. A held release keeps the handle.</summary>
    private void Dispose(TaskId task, SettledTurn turn)
    {
        _live[task] = new(LiveStage.Settling);
        _handles[task] = turn;
        Offload(() => Disposition(turn), release => Disposed(task, release),
            error => Disposed(task, new Release.Held(new(RunProblem.StorageUnavailable))));
    }

    private void Disposed(TaskId task, Release release)
    {
        _live.Remove(task);
        if (release is Release.Held held)
        {
            Hold(task, new TaskHold.Refused(held.Reason, null, Transient(held.Reason.Problem)));
            return;
        }
        _handles.Remove(task);
        _holds.Remove(task);
    }

    /// <summary>
    /// A resting turn releases as it rests. Any other closes its attempt with the folded outcome at the frozen checkpoint;
    /// a success then accepts the attempt's report as its result. Each step converges on a repeat.
    /// </summary>
    private Release Disposition(SettledTurn turn)
    {
        var attempt = turn.Attempt;
        if (!RunProjection.Rests(attempt))
        {
            TerminalAttemptOutcome? outcome = attempt.Status switch
            {
                AttemptStatus.Succeeded => TerminalAttemptOutcome.Succeeded,
                AttemptStatus.Failed => TerminalAttemptOutcome.Failed,
                AttemptStatus.Cancelled => TerminalAttemptOutcome.Cancelled,
                AttemptStatus.Interrupted => TerminalAttemptOutcome.Interrupted,
                _ => null,
            };
            if (outcome is not { } ended) return new Release.Held(new(RunProblem.OutcomeMismatch));
            if (Record() is not { } record) return new Release.Held(new(RunProblem.StorageUnavailable));
            var launch = turn.Address.Launch;
            var operation = RunOperations.Turn(record, launch);
            _runs.Probe?.Invoke("coordinator.close-attempt.before");
            if (_store.CloseAttempt(_permit!, RunOperations.CloseAttempt(operation), launch.Attempt, ended, turn.Log) is RunDecision.Rejected rejected)
                return new Release.Held(rejected.Reason);
            if (ended == TerminalAttemptOutcome.Succeeded && Finish(turn.Lease, launch.Attempt, operation, Scheduled()) is { } refusal)
                return new Release.Held(refusal);
        }
        _runs.Probe?.Invoke("coordinator.release.before");
        return turn.Release();
    }

    /// <summary>
    /// Accepts a successfully closed agent attempt's report as its result, through <see cref="Materializer.Accept"/>. A
    /// blocked editing result shows as its block.
    /// </summary>
    private RunRejection? Finish(RunLease lease, AttemptId attempt, OperationId operation, Materializer materializer)
    {
        _runs.Probe?.Invoke("coordinator.accept.before");
        return materializer.Accept(lease, RunOperations.Accept(operation), attempt) is Acceptance.Rejected rejected ? rejected.Reason : null;
    }

    /// <summary>
    /// Settles what an earlier window or an earlier step left: every unclosed claim through <see cref="ProjectRuns.Reconcile"/>,
    /// which never launches, and every successful closure without a result through its acceptance.
    /// </summary>
    private void Reconcile(RunRecord record, RunView view)
    {
        foreach (var (task, state) in view.Tasks)
        {
            if (_live.ContainsKey(task) || _holds.ContainsKey(task) || state.Attempt is not { } attempt) continue;
            if (_handles.TryGetValue(task, out var handle))
            {
                Dispose(task, handle);
                continue;
            }
            if (record.Closures.TryGetValue(attempt, out var end))
            {
                if (state.State == TaskState.Settling && end is AttemptEnd.Logged { Outcome: TerminalAttemptOutcome.Succeeded }) FinishClosed(record, task, attempt);
                continue;
            }
            // A resting closure that reached the log but not the journal is finished from the log.
            if (state.State == TaskState.Settling && FinishClosing(record, task, attempt)) continue;
            // A waiting attempt rests as it is. Only a stop closes it here.
            if (state.State is TaskState.Settling or TaskState.Uncertain && RunProjection.LastLaunch(record, attempt) is { } claimed)
                ReconcileLaunch(record, task, claimed);
        }
    }

    private void ReconcileLaunch(RunRecord record, TaskId task, LaunchKey launch)
    {
        _live[task] = new(LiveStage.Settling);
        var operation = RunOperations.Turn(record, launch);
        Background(() => _runs.Reconcile(_permit!, operation, launch), reconciliation =>
        {
            switch (reconciliation)
            {
                case Reconciliation.Found found:
                    Settled(task, found.Settlement);
                    break;
                case Reconciliation.Refused refused:
                    _live.Remove(task);
                    Hold(task, new TaskHold.Refused(refused.Reason, null, Transient(refused.Reason.Problem)));
                    break;
            }
        }, error => Faulted(task, error));
    }

    private void FinishClosed(RunRecord record, TaskId task, AttemptId attempt)
    {
        _live[task] = new(LiveStage.Settling);
        var operation = RunOperations.Turn(record, RunProjection.LastLaunch(record, attempt) ?? new(attempt, 1));
        Offload(() =>
        {
            if (_permit!.TakeTask(task) is not LeaseTake.Taken taken) return new RunRejection(RunProblem.TaskBusy);
            using (taken.Lease) return Finish(taken.Lease, attempt, operation, Scheduled());
        }, refusal =>
        {
            _live.Remove(task);
            if (refusal is not null) Hold(task, new TaskHold.Refused(refusal, null, Transient(refusal.Problem)));
        }, error => Faulted(task, error));
    }

    /// <summary>
    /// Settles the run as completed once nothing more can start: every task is done or can no longer start (#90), which no
    /// unresolved block on a task's current attempt or result allows, and nothing is open, unresolved, or in flight. The
    /// run's finished work first merges into the branch it started from (#148). A Merge node whose attempt at that merge
    /// failed holds nothing back, since the person may still merge the work themselves.
    /// </summary>
    private RunRecord Complete(RunRecord record, RunView view)
    {
        if (_live.Count != 0 || _holds.Count != 0 ||
            !view.Tasks.Values.All(task => task.State == TaskState.Done || task.Dormant || FinalMergeFailed(task)) || !Closed(record))
            return record;
        return MergeAndComplete(record);
    }

    /// <summary>
    /// After the recorded stop: cancels running turns once, closes resting attempts without sending queued text, and closes
    /// attempts that were reserved but never claimed as not started, confirmed by the stop.
    /// </summary>
    private void StopWork(RunRecord record, RunView view)
    {
        var stop = RunOperations.Stop(record)!.Value;
        foreach (var (task, live) in _live.ToArray())
        {
            if (live is not { Stage: LiveStage.Running, Turn: { } turn, Cancelled: false }) continue;
            _live[task] = live with { Cancelled = true };
            Background(() => turn.CancelAsync(), _ => { }, _ => { });
        }
        foreach (var (task, state) in view.Tasks)
        {
            if (_live.ContainsKey(task) || _holds.ContainsKey(task) || state.Attempt is not { } attempt || record.Closures.ContainsKey(attempt))
                continue;
            if (state.State == TaskState.Waiting) CloseWaiting(task, attempt, stop);
            else if (!record.Claims.Keys.Any(key => key.Attempt == attempt)) CloseUnclaimed(task, attempt, stop);
        }
    }

    private void CloseWaiting(TaskId task, AttemptId attempt, OperationId stop)
    {
        _live[task] = new(LiveStage.Settling);
        Background(async () =>
        {
            var closing = await _runs.CloseResting(_permit!, RunOperations.Cancel(stop, attempt), attempt, new RestingEnd.Cancel())
                .ConfigureAwait(false);
            return closing switch
            {
                RestingClose.Closed closed => closed.Attempt.Release() is Release.Held held ? new TaskHold.Refused(held.Reason, null, Transient(held.Reason.Problem)) : null,
                RestingClose.Blocked blocked => new TaskHold.Blocked(blocked.Block),
                RestingClose.Refused refused => new TaskHold.Refused(refused.Reason, null, Transient(refused.Reason.Problem)),
                _ => (TaskHold?)null,
            };
        }, hold =>
        {
            _live.Remove(task);
            if (hold is not null) Hold(task, hold);
        }, error => Faulted(task, error));
    }

    private void CloseUnclaimed(TaskId task, AttemptId attempt, OperationId stop, string reason = StoppedReason)
    {
        _live[task] = new(LiveStage.Settling);
        Offload(() =>
        {
            if (_permit!.TakeTask(task) is not LeaseTake.Taken taken) return new RunRejection(RunProblem.TaskBusy);
            using (taken.Lease)
            {
                return _store.Recover(taken.Lease, RunOperations.NotStarted(stop, attempt), attempt, RecoveryOutcome.NotStarted, stop, reason)
                    is RunDecision.Rejected rejected ? rejected.Reason : null;
            }
        }, refusal =>
        {
            _live.Remove(task);
            if (refusal is not null) Hold(task, new TaskHold.Refused(refusal, null, Transient(refusal.Problem)));
        }, error => Faulted(task, error));
    }

    /// <summary>Settles a stopping run as stopped once nothing is in flight and every attempt is closed.</summary>
    private RunRecord Stopped(RunRecord record)
    {
        if (_live.Count != 0 || !Closed(record)) return record;
        return Settle(record, RunOperations.Stopped(RunOperations.Stop(record)!.Value), RunOutcome.Stopped);
    }

    private static bool Closed(RunRecord record) => record.Attempts.Keys.All(record.Closures.ContainsKey) && record.UnresolvedClaims.IsEmpty;

    private RunRecord Settle(RunRecord record, OperationId operation, RunOutcome outcome)
    {
        switch (_store.Settle(_permit!, operation, outcome))
        {
            case RunDecision.Rejected rejected:
                _problem = $"The run could not settle: {rejected.Reason.Problem}.";
                return record;
            default:
                return Read() ?? record;
        }
    }

    /// <summary>Releases the settled run's retention pins once per window. The count is diagnostic.</summary>
    private void ReleasePins()
    {
        if (_pinsReleasing) return;
        _pinsReleasing = true;
        Offload(() => Scheduled().ReleasePins(_permit!, RunOperations.ReleasePins(Address.Run)), release =>
        {
            if (release is PinRelease.Released) _pinsReleased = true;
            else _problem = $"The run's retention pins were not released: {release}.";
        }, error => _problem = error.Message);
    }
}
