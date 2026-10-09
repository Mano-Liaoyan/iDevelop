using IDevelop.Nodes;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>
/// Reviews in the run (E3c.2). A review's reviewer turns and its subject's fix rounds use the same launch authority as
/// every other turn, and its next step comes from <see cref="ReviewWork"/> over the run's records. A conclusion takes no
/// client slot. A reviewer turn or a fix round starts beside the run's other clients; only at the run's bound of client
/// roots does it come after conversation continuations and ready tasks, so continuing review work goes to the tail. A fix
/// round that closing iDevelop interrupted waits for the person's Continue fix or Retry fix.
/// </summary>
internal sealed partial class WorkflowRunCoordinator
{
    /// <summary>A review attempt that rests between its reviewer's turns, with its log read once.</summary>
    private sealed record RestingReview(TaskId Task, AttemptId Attempt, LaunchKey Last, AttemptRecord Log, TaskId Subject);

    /// <summary>The task's review attempt that rests in review and that this window neither works on nor holds back, or null.</summary>
    private RestingReview? Reviewing(RunRecord record, TaskView? view)
    {
        if (view is not { State: TaskState.Waiting, Status: AttemptStatus.InReview, Attempt: { } attempt } || _live.ContainsKey(view.Task) ||
            _holds.ContainsKey(view.Task) || RunProjection.LastLaunch(record, attempt) is not { } last)
            return null;
        var subject = record.Revisions[record.Attempts[attempt].Revision].Snapshot.SubjectOf(view.Task);
        return subject is { } id && Log(record.Attempts[attempt]) is { Status: AttemptStatus.InReview } log ? new(view.Task, attempt, last, log, id) : null;
    }

    /// <summary>Whether a ready task's reserved attempt fixes a review round, which only the review's step resumes.</summary>
    private static bool FixesReview(RunRecord record, TaskView view) => view.Attempt is { } attempt && record.ReviewOf(attempt) is not null;

    /// <summary>The reviewer's first prompt for a review task's initial turn, or null for any other task. Reads Git.</summary>
    private string? FirstPrompt(RunRecord record, TaskView view, AttemptCause cause)
    {
        var revision = view.Attempt is { } reserved ? record.Attempts[reserved].Revision : record.Revision.Id;
        var definition = record.Revisions[revision].Snapshot.Tasks[view.Task];
        if (definition.Blueprint.Work is not WorkSpec.Review || cause is not AttemptCause.Initial) return null;
        var subject = RunReviews.Subject(record, view.Task, revision, Log, Address.Project);
        return ReviewWork.Instance.Next(new NodeContext(definition, "") { Subject = subject }, null) is NodeStep.RunTurn turn
            ? turn.Prompt : throw new InvalidOperationException("The review has no task to review.");
    }

    internal const string ReviewEndedReason = "The review ended before this fix round started.";

    /// <summary>
    /// A fix whose review has ended goes no further, as when the person cancelled the review: its running turn is cancelled,
    /// a fix that rests closes cancelled, and a reserved fix that never started closes as not started.
    /// </summary>
    private void EndFixes(RunRecord record, RunView view)
    {
        foreach (var (task, state) in view.Tasks)
        {
            if (state.Attempt is not { } fix || record.Closures.ContainsKey(fix) || record.ReviewOf(fix) is not { } link ||
                !record.Closures.ContainsKey(link.Attempt) || _holds.ContainsKey(task))
                continue;
            var ended = OperationIds.Derive(RunOperations.Root(Address.Run), $"review-ended/{link.Attempt.Value:D}");
            switch (_live.GetValueOrDefault(task))
            {
                case { Stage: LiveStage.Running, Turn: { } turn, Cancelled: false } live when turn.Address.Launch.Attempt == fix:
                    _live[task] = live with { Cancelled = true };
                    Background(() => turn.CancelAsync(), _ => { }, _ => { });
                    break;
                case null when state.State == TaskState.Waiting:
                    CloseWaiting(task, fix, ended);
                    break;
                case null when !record.Claims.Keys.Any(key => key.Attempt == fix):
                    CloseUnclaimed(task, fix, ended, ReviewEndedReason);
                    break;
            }
        }
    }

    /// <summary>Concludes every resting review whose step finishes or fails it. A conclusion takes no client slot.</summary>
    private void ConcludeReviews(RunRecord record, RunView view)
    {
        foreach (var state in view.Tasks.Values)
        {
            if (Reviewing(record, state) is not { } review || _live.ContainsKey(review.Subject)) continue;
            switch (RunReviews.Step(record, review.Log, Log, null))
            {
                case NodeStep.Finish:
                    Conclude(record, review, null);
                    break;
                case NodeStep.Fail fail:
                    Conclude(record, review, fail.Reason);
                    break;
            }
        }
    }

    /// <summary>
    /// Starts the next reviewer turn or fix round of each resting review that has one, in task order, up to
    /// <paramref name="free"/> of them. A reserved fix that was never claimed resumes with its recorded cause. A subject
    /// takes one fix round at a time, so a second review of it waits.
    /// </summary>
    private void AdvanceReviews(RunRecord record, RunView view, int free)
    {
        var started = 0;
        foreach (var state in view.Tasks.Values)
        {
            if (started >= free) return;
            if (Reviewing(record, state) is not { } review || _live.ContainsKey(review.Subject) || _holds.ContainsKey(review.Subject)) continue;
            if (RunProjection.LatestAttempts(record).GetValueOrDefault(review.Subject) is { } newest && record.ReviewOf(newest) is { } link &&
                link.Attempt == review.Attempt && !record.Claims.Keys.Any(key => key.Attempt == newest) && !record.Closures.ContainsKey(newest))
            {
                StartFix(record, review, newest);
                started++;
                continue;
            }
            switch (RunReviews.Step(record, review.Log, Log, null))
            {
                case NodeStep.RunTurn:
                    StartReviewTurn(record, review);
                    started++;
                    break;
                case NodeStep.FixRound:
                    StartFixRound(record, review);
                    started++;
                    break;
            }
        }
    }

    private void Conclude(RunRecord record, RestingReview review, string? failure)
    {
        var task = review.Task;
        var operation = RunOperations.Turn(record, review.Last);
        _live[task] = new(LiveStage.Settling);
        Background(async () =>
        {
            var closing = await _runs.CloseResting(_permit!, OperationIds.Derive(operation, "review/conclude"), review.Attempt,
                new RestingEnd.Conclude(failure), scheduled: true, halted: Halted).ConfigureAwait(false);
            switch (closing)
            {
                case RestingClose.Closed closed:
                    if (failure is null && Finish(closed.Attempt.Lease, task, review.Attempt, operation, Scheduled()) is { } refusal)
                    {
                        // The closure is recorded. Its report's acceptance is finished later, under a lease of its own.
                        closed.Attempt.Lease.Dispose();
                        return new TaskHold.Refused(refusal, null, Transient(refusal.Problem));
                    }
                    return closed.Attempt.Release() is Release.Held held ? new TaskHold.Refused(held.Reason, null, Transient(held.Reason.Problem)) : null;
                case RestingClose.Blocked blocked:
                    return new TaskHold.Blocked(blocked.Block);
                case RestingClose.Refused refused:
                    return new TaskHold.Refused(refused.Reason, null, Transient(refused.Reason.Problem));
                default:
                    return (TaskHold?)null;
            }
        }, hold =>
        {
            _live.Remove(task);
            if (hold is not null) Hold(task, hold);
        }, error => Faulted(task, error));
    }

    /// <summary>The reviewer's next turn: its round prompt with the fix's report, or a verdict repair. The refresh is E3a's.</summary>
    private void StartReviewTurn(RunRecord record, RestingReview review)
    {
        var task = review.Task;
        var next = new LaunchKey(review.Attempt, review.Last.Turn + 1);
        _live[task] = new(LiveStage.Starting);
        _runs.Probe?.Invoke("coordinator.review-turn");
        Background(() => RunReviews.Step(record, review.Log, Log, Address.Project) is NodeStep.RunTurn turn
                ? _runs.StartTurn(_permit!, new TurnIntent.Next(RunOperations.Turn(record, next), next, turn.Prompt) { Report = turn.Report }, halted: Halted)
                : Task.FromResult<TurnStart>(new TurnStart.Refused(new(RunProblem.InvalidClaim))),
            start => Started(task, start, record.Revision.Id), error => StartFaulted(task, error));
    }

    /// <summary>A new attempt of the subject for the review's latest round, as <see cref="AttemptCause.ReviewFix"/>.</summary>
    private void StartFixRound(RunRecord record, RestingReview review)
    {
        var subject = review.Subject;
        _live[subject] = new(LiveStage.Starting);
        _runs.Probe?.Invoke("coordinator.fix");
        Background(() =>
        {
            if (RunReviews.Step(record, review.Log, Log, Address.Project) is not NodeStep.FixRound fix)
                return Task.FromResult<TurnStart>(new TurnStart.Refused(new(RunProblem.InvalidClaim)));
            var cause = new AttemptCause.ReviewFix(new ReviewLink(review.Task, review.Attempt, fix.Round, fix.Guidance));
            return _runs.StartTurn(_permit!, new TurnIntent.First(RunOperations.First(Address.Run, subject, cause), subject, cause, fix.Prompt), halted: Halted);
        }, start => Started(subject, start, record.Revision.Id), error => StartFaulted(subject, error));
    }

    /// <summary>Resumes a reserved fix of the review with its own cause and operation, and its prompt rebuilt from them.</summary>
    private void StartFix(RunRecord record, RestingReview review, AttemptId attempt)
    {
        var subject = review.Subject;
        var cause = record.Attempts[attempt].Cause;
        _live[subject] = new(LiveStage.Starting);
        _runs.Probe?.Invoke("coordinator.fix");
        Background(() => _runs.StartTurn(_permit!, new TurnIntent.First(RunOperations.First(Address.Run, subject, cause), subject, cause,
                RunReviews.FixPrompt(record, review.Log, attempt, Log, Address.Project)), halted: Halted),
            start => Started(subject, start, record.Revision.Id), error => StartFaulted(subject, error));
    }

    private void StartFaulted(TaskId task, Exception error)
    {
        _live.Remove(task);
        _problem = error.Message;
        HoldStart(task, new TaskHold.Refused(new(RunProblem.StorageUnavailable), null, Transient: true));
    }

    /// <summary>
    /// Continue fix: after closing iDevelop interrupted the review's fix round, the round goes on in the fix's session from
    /// the files it left. Two matching observations preserve the checkout and become the new attempt's recovery baseline,
    /// which its claim checks again; they never stand in for the interrupted turn's success. The attempt is reserved at once
    /// and starts beside the run's other clients, or once a slot is free at the run's bound. Repeating the confirmation
    /// returns the same attempt.
    /// </summary>
    internal Task<FixReply> ContinueFix(RunAddress address, TaskId review, OperationId confirmation, CancellationToken wait = default) =>
        Recover(address, review, FixChoice.Continue, confirmation, wait);

    /// <summary>
    /// Retry fix: the interrupted round starts again in a fresh session. The fix's unfinished work is salvaged first, and a
    /// checked reset puts the checkout back at the subject's latest accepted result.
    /// </summary>
    internal Task<FixReply> RetryFix(RunAddress address, TaskId review, OperationId confirmation, CancellationToken wait = default) =>
        Recover(address, review, FixChoice.Retry, confirmation, wait);

    private Task<FixReply> Recover(RunAddress address, TaskId task, FixChoice choice, OperationId confirmation, CancellationToken wait) =>
        Converse<FixReply>(address, (record, complete) => RecoverCore(record, task, choice, confirmation, complete), command => command switch
        {
            RunCommand.Unavailable unavailable => new FixReply.Unavailable(unavailable.Message),
            RunCommand.Refused refused => new FixReply.Refused(refused.Reason),
            _ => throw new InvalidOperationException(),
        }, wait);

    private void RecoverCore(RunRecord record, TaskId task, FixChoice choice, OperationId confirmation, Action<FixReply> complete)
    {
        FixReply Refused(RunProblem problem) => new FixReply.Refused(new(problem, Task: task));
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
        AttemptCause Cause(AttemptId fix) => choice == FixChoice.Continue ? new AttemptCause.Continue(fix, confirmation) : new AttemptCause.Retry(fix, confirmation);
        var latest = RunProjection.LatestAttempts(record);
        if (record.Revision.Snapshot.SubjectOf(task) is { } owner && latest.GetValueOrDefault(task) is { } reviewer)
        {
            bool Replaces(RunAttempt attempt) => attempt.Task == owner && RunReducer.Previous(attempt.Cause) is not null && record.ReviewOf(attempt.Id)?.Attempt == reviewer;
            // A confirmation is one choice: it converges on its replacement while that is open, and never makes another.
            if (record.Attempts.Values.FirstOrDefault(attempt => Replaces(attempt) && Confirmation(attempt.Cause) == confirmation) is { } chosen)
            {
                complete(!record.Closures.ContainsKey(chosen.Id) && RunReducer.Same(chosen.Cause, Cause(RunReducer.Previous(chosen.Cause)!.Value))
                    ? new FixReply.Reserved(chosen.Id) : Refused(RunProblem.ReplacementConflict));
                return;
            }
            // Another choice already replaced the round's fix. While that replacement is open, and once it has ended
            // other than by an interruption that offers the choice again, this choice has nothing left to replace. The
            // replacement and the reviewer's next turn run by now, so the task would otherwise only look busy.
            if (latest.GetValueOrDefault(owner) is { } newest && Replaces(record.Attempts[newest]) &&
                (!record.Closures.ContainsKey(newest) || !OffersChoice(record, reviewer, newest)))
            {
                complete(Refused(RunProblem.ReplacementConflict));
                return;
            }
        }
        if (Reviewing(record, Project(record).Tasks.GetValueOrDefault(task)) is not { } review)
        {
            complete(Refused(_live.ContainsKey(task) ? RunProblem.TaskBusy : RunProblem.InvalidClaim));
            return;
        }
        var subject = review.Subject;
        if (_live.ContainsKey(subject))
        {
            complete(Refused(RunProblem.TaskBusy));
            return;
        }
        if (RunReviews.Recovery(record, review.Log, Log) is not { } recovery)
        {
            complete(Refused(RunProblem.InvalidClaim));
            return;
        }
        if (choice == FixChoice.Continue && recovery.ContinueUnavailable is not null)
        {
            complete(Refused(RunProblem.SessionUnavailable));
            return;
        }
        var cause = Cause(recovery.Fix);
        _live[subject] = new(LiveStage.Settling);
        Background(() => Replace(record, review, recovery.Fix, cause, confirmation), reply =>
        {
            _live.Remove(subject);
            complete(reply);
        }, error =>
        {
            _live.Remove(subject);
            _problem = error.Message;
            complete(new FixReply.Refused(new(RunProblem.StorageUnavailable, Task: task)));
        });
    }

    /// <summary>
    /// Whether the review, resting between its reviewer's turns, offers Continue fix or Retry fix for <paramref name="fix"/>,
    /// as after closing iDevelop interrupted it. A reviewer turn that runs offers no choice.
    /// </summary>
    private bool OffersChoice(RunRecord record, AttemptId review, AttemptId fix) =>
        Log(record.Attempts[review]) is { Status: AttemptStatus.InReview } reviewer && RunReviews.Recovery(record, reviewer, Log) is { } recovery &&
        recovery.Fix == fix;

    private static OperationId? Confirmation(AttemptCause cause) => cause switch
    {
        AttemptCause.Continue continued => continued.Confirmation,
        AttemptCause.Retry retry => retry.Confirmation,
        _ => null,
    };

    /// <summary>
    /// Under the subject's task lease: preserves and baselines the checkout for Continue, or salvages and resets it for
    /// Retry, then reserves and prepares the replacement. Each step derives its operation from the confirmation, so a
    /// repeat after a crash or a refusal converges on the same records.
    /// </summary>
    private async Task<FixReply> Replace(RunRecord record, RestingReview review, AttemptId fix, AttemptCause cause, OperationId confirmation)
    {
        if (_permit!.TakeTask(review.Subject) is not LeaseTake.Taken taken) return new FixReply.Refused(new(RunProblem.TaskBusy));
        using var lease = taken.Lease;
        var materializer = Commanded();
        FixReply Refused(RunRejection reason) => new FixReply.Refused(reason);
        if (cause is AttemptCause.Continue)
        {
            var preservation = OperationIds.Derive(confirmation, "continue-fix/preserve");
            _runs.Probe?.Invoke("coordinator.continue-fix.preserve");
            switch (await materializer.Preserve(lease, preservation, fix).ConfigureAwait(false))
            {
                case Preservation.Blocked blocked: return new FixReply.Blocked(blocked.Block);
                case Preservation.Rejected rejected: return Refused(rejected.Reason);
            }
            switch (materializer.RecordRecoveryBaseline(lease, OperationIds.Derive(confirmation, "continue-fix/baseline"), fix, confirmation, preservation))
            {
                case RecoveryBaselining.Blocked blocked: return new FixReply.Blocked(blocked.Block);
                case RecoveryBaselining.Rejected rejected: return Refused(rejected.Reason);
            }
        }
        else
        {
            _runs.Probe?.Invoke("coordinator.retry-fix.salvage");
            switch (await materializer.Salvage(lease, OperationIds.Derive(confirmation, "retry-fix/salvage"), fix).ConfigureAwait(false))
            {
                case Salvage.Blocked blocked: return new FixReply.Blocked(blocked.Block);
                case Salvage.Rejected rejected: return Refused(rejected.Reason);
                case Salvage.Retained retained:
                    switch (materializer.ResetForRetry(lease, OperationIds.Derive(confirmation, "retry-fix/reset"), retained.Receipt.Plan, confirmation))
                    {
                        case RetryReset.Blocked blocked: return new FixReply.Blocked(blocked.Block);
                        case RetryReset.Rejected rejected: return Refused(rejected.Reason);
                    }
                    break;
            }
        }
        // The prompt reads the review's records as the reserved attempt will, before the attempt exists.
        var prompt = RunReviews.ReplacementPrompt(record, review.Log, fix, cause, Log, Address.Project);
        return await materializer.Prepare(lease, RunOperations.First(Address.Run, review.Subject, cause), cause, prompt).ConfigureAwait(false) switch
        {
            Preparation.Ready ready => new FixReply.Reserved(ready.Execution.Launch.Attempt),
            Preparation.Blocked blocked => new FixReply.Blocked(blocked.Block),
            Preparation.Rejected rejected => Refused(rejected.Reason),
            _ => throw new InvalidOperationException(),
        };
    }
}
