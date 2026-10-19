using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>
/// A person's recovery of the run's tasks (E3g.2): the evidence of what holds a task, and the confirmed closure of a turn
/// whose execution is unresolved. Only the controlling window closes a turn, under the task's lease, and names its run.
/// </summary>
internal sealed partial class WorkflowRunCoordinator
{
    /// <summary>
    /// What a person needs to judge <paramref name="task"/>'s state: the blocks that hold it, and its attempt's cause and
    /// newest turn: its root process and root exit. <paramref name="attempt"/> names the attempt, such as a block's; by
    /// default it is the task's newest one, and a task without an attempt has its blocks alone. Null when the journal cannot
    /// be read or the run has no such task. It reads the journal and the attempt's log off the loop, and checks whether the
    /// root process still runs.
    /// </summary>
    public TaskEvidence? Evidence(TaskId task, AttemptId? attempt = null)
    {
        if (Record() is not { } record || !record.Revision.Snapshot.Tasks.ContainsKey(task)) return null;
        AttemptId? latest = RunProjection.LatestAttempts(record).TryGetValue(task, out var newest) ? newest : null;
        var blocks = RunProjection.Blocks(record, task, latest, record.CurrentResults.GetValueOrDefault(task)).ToImmutableArray();
        if ((attempt ?? latest) is not { } id || !record.Attempts.TryGetValue(id, out var owner)) return new(task, null, blocks);
        var launch = RunProjection.LastLaunch(record, id);
        ImmutableArray<AttemptEvent> events;
        try { events = AttemptEvidence.Read(_store.AttemptFolder(Address.Workflow, Address.Run, owner.Task, id)).Events; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { events = []; }
        ProcessIdentity? root = launch is { } turn ? Launched(events, turn.Turn) : null;
        return new(task, id, blocks)
        {
            Cause = owner.Cause,
            Turn = launch?.Turn,
            Root = root,
            RootNow = root is { } identity && (launch is not { } claimed || !record.RootExits.ContainsKey(claimed)) ? ProcessCheck.Check(identity) : null,
            Exit = launch is { } exited ? record.RootExits.GetValueOrDefault(exited) : null,
        };
    }

    /// <summary>
    /// Closes <paramref name="attempt"/>, whose turn's execution is unresolved, as stopped, on the person's confirmation
    /// with <paramref name="reason"/>. It records no success and no exit, and nothing launches it again. A turn whose root
    /// exit is recorded is refused, because only its settlement is unresolved. A repeat returns the closure. It also works
    /// while the run is stopping, which waits for such an attempt to close.
    /// </summary>
    public Task<RunCommand> ConfirmStopped(RunAddress address, TaskId task, AttemptId attempt, string reason, OperationId command,
        CancellationToken wait = default) =>
        Request(address, task, message => new RunCommand.Unavailable(message), reason => new RunCommand.Refused(reason), mark: true, held =>
        {
            if (Record() is not { } record) return Task.FromResult<RunCommand>(new RunCommand.Refused(new(RunProblem.StorageUnavailable)));
            if (!record.Attempts.TryGetValue(attempt, out var owner) || owner.Task != task)
                return Task.FromResult<RunCommand>(new RunCommand.Refused(new(RunProblem.UnknownAttempt)));
            // Only an unresolved claim without a root exit is closed this way. A repeat finds its own closure.
            if (record.Closures.TryGetValue(attempt, out var end))
                return Task.FromResult<RunCommand>(end is AttemptEnd.Recovered { Outcome: RecoveryOutcome.Stopped } recovered && recovered.Confirmation == command
                    ? Accepted : new RunCommand.Refused(new(RunProblem.InvalidClaim)));
            if (!record.UnresolvedClaims.Any(key => key.Attempt == attempt && !record.RootExits.ContainsKey(key)))
                return Task.FromResult<RunCommand>(new RunCommand.Refused(new(RunProblem.InvalidClaim)));
            return Leased(held, task, lease => Task.FromResult<RunCommand>(_store.Recover(lease, OperationIds.Derive(command, "recover"), attempt,
                    RecoveryOutcome.Stopped, command, reason) switch
                {
                    RunDecision.Rejected rejected => new RunCommand.Refused(rejected.Reason),
                    _ => Accepted,
                }), refused => new RunCommand.Refused(refused));
        }, outcome =>
        {
            // The next decision lets the unresolved turn give up the task's lease against the closure's receipt.
            if (outcome is RunCommand.Accepted) _holds.Remove(task);
            return outcome;
        }, wait);

    /// <summary>
    /// Lets each unresolved turn whose attempt the journal shows closed give up its task's lease. A release the turn holds
    /// back keeps the turn, shows why as the run's problem, and is tried again at the next decision.
    /// </summary>
    private void ReleaseClosed(RunRecord record)
    {
        foreach (var (task, turn) in _unresolved.ToArray())
        {
            if (!record.Closures.ContainsKey(turn.Address.Launch.Attempt) || _live.ContainsKey(task)) continue;
            switch (turn.Release())
            {
                case Release.Released:
                    _unresolved.Remove(task);
                    break;
                case Release.Held held:
                    _problem = $"A closed turn still holds its task's lock ({held.Reason.Problem}). iDevelop tries again at its next step.";
                    break;
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> under <paramref name="task"/>'s lease: the one <paramref name="held"/>, a turn whose
    /// settlement stayed unresolved, still holds, or a lease taken for it alone, which it gives up afterwards.
    /// </summary>
    private async Task<T> Leased<T>(UnresolvedTurn? held, TaskId task, Func<RunLease, Task<T>> work, Func<RunRejection, T> busy)
    {
        if (held is { Lease.Held: true }) return await work(held.Lease).ConfigureAwait(false);
        if (_permit!.TakeTask(task) is not LeaseTake.Taken taken) return busy(new(RunProblem.TaskBusy));
        using (taken.Lease) return await work(taken.Lease).ConfigureAwait(false);
    }

    /// <summary>The client process the log recorded for <paramref name="turn"/>, or null when it recorded none.</summary>
    private static ProcessIdentity? Launched(ImmutableArray<AttemptEvent> events, int turn)
    {
        // Each later turn begins with its request, as the runner's own reconciliation counts them.
        var index = 1;
        ProcessIdentity? identity = null;
        foreach (var e in events)
        {
            if (e is AttemptEvent.TurnRequested) index++;
            if (index == turn && e is AttemptEvent.Launched launched) identity = new(launched.ProcessId, launched.ProcessStarted);
        }
        return identity;
    }

    /// <summary>
    /// Runs <paramref name="work"/> off the loop for the controlling window and answers with <paramref name="finish"/> of its
    /// outcome, on the loop. With <paramref name="mark"/> the task counts as settling while it runs, so nothing else starts it.
    /// The work gets the task's turn whose settlement stayed unresolved, read on the loop, so it can use the lease that turn
    /// still holds.
    /// </summary>
    private Task<T> Request<T, TWork>(RunAddress address, TaskId task, Func<string, T> unavailable, Func<RunRejection, T> refused, bool mark,
        Func<UnresolvedTurn?, Task<TWork>> work, Func<TWork, T> finish, CancellationToken wait)
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
                    var held = _unresolved.GetValueOrDefault(task);
                    Background(() => work(held), outcome =>
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
}

/// <summary>What one attempt of a task left as evidence, for a person to judge a block or an unresolved turn.</summary>
/// <param name="Attempt">The attempt the evidence is about, or null for a task without one.</param>
/// <param name="Blocks">The unresolved blocks that hold the task, oldest first.</param>
internal sealed record TaskEvidence(TaskId Task, AttemptId? Attempt, ImmutableArray<MaterializationBlock> Blocks)
{
    /// <summary>Why the attempt was started: initially, as a retry, as a continuation, or for a review's fix round.</summary>
    public AttemptCause? Cause { get; init; }

    /// <summary>The attempt's newest claimed turn, or null before its first claim.</summary>
    public int? Turn { get; init; }

    /// <summary>The newest turn's client process, as its launch recorded it, or null when the log recorded none.</summary>
    public ProcessIdentity? Root { get; init; }

    /// <summary>Whether that process still runs, checked now, while no root exit is recorded.</summary>
    public ProcessMatch? RootNow { get; init; }

    /// <summary>The newest turn's recorded root exit, or null when none was recorded.</summary>
    public RunEvent.RootExitObserved? Exit { get; init; }
}

