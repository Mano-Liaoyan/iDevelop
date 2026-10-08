using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>
/// A person's recovery of the run's tasks (E3g.2): the evidence of what holds a task, Preserve and restore of a task's
/// checkout through E3a.3, and the confirmed closure of a turn whose execution is unresolved. Only the controlling window
/// runs them, each under the task's lease, and each names its run.
/// </summary>
internal sealed partial class WorkflowRunCoordinator
{
    /// <summary>
    /// What a person needs to judge <paramref name="task"/>'s state: the blocks that hold it, and its attempt's root
    /// process, root exit, captures, and cleanup. <paramref name="attempt"/> names the attempt, such as a block's; by
    /// default it is the task's newest one. Null when the journal cannot be read or the task has no attempt. It reads the
    /// journal and the attempt's log off the loop and checks whether the root process still runs.
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
        var launched = events.OfType<AttemptEvent.Launched>().LastOrDefault();
        ProcessIdentity? root = launched is null ? null : new(launched.ProcessId, launched.ProcessStarted);
        var captures = launch is { } key
            ? record.Captures.Values.SelectMany(list => list).Where(observation => observation.Launch == key)
                .OrderBy(observation => observation.Started).ThenBy(observation => observation.Ordinal).ToImmutableArray()
            : [];
        return new(task, id, blocks)
        {
            Root = root,
            RootNow = root is { } identity && (launch is not { } claimed || !record.RootExits.ContainsKey(claimed)) ? ProcessCheck.Check(identity) : null,
            Exit = launch is { } exited ? record.RootExits.GetValueOrDefault(exited) : null,
            Captures = captures,
            Dispositions = [.. captures.Select(observation => observation.Capture).Distinct()
                .Select(capture => record.Dispositions.GetValueOrDefault(capture)).OfType<RunEvent.CaptureDisposed>()],
            Cleanup = events.OfType<AttemptEvent.CleanedUp>().LastOrDefault(),
        };
    }

    /// <summary>
    /// Retains <paramref name="task"/>'s checkout as <paramref name="attempt"/> left it, under <paramref name="command"/>,
    /// which names the preservation for <see cref="PreviewRestore"/> and <see cref="Restore"/>. Two matching observations
    /// are retained; continued changes keep the block and move nothing. A repeat returns the receipt.
    /// </summary>
    public Task<Preservation> Preserve(RunAddress address, TaskId task, AttemptId attempt, OperationId command, CancellationToken wait = default) =>
        Request(address, task, message => new Preservation.Unavailable(message), reason => new Preservation.Rejected(reason), mark: true,
            () => Leased(task, async lease => await _materializer().Preserve(lease, command, attempt).ConfigureAwait(false),
                reason => new Preservation.Rejected(reason)),
            outcome =>
            {
                // A receipt is a disposition of a turn whose settlement stayed unresolved, so that turn lets go of the task.
                if (outcome is Preservation.Preserved && _unresolved.Remove(task, out var turn)) turn.Release();
                return outcome;
            }, wait);

    /// <summary>
    /// What restoring <paramref name="task"/>'s checkout from the preservation <paramref name="preservation"/> would move:
    /// its current state, the recorded baseline, the paths, refs, and index lock, and the blocks it repairs. It records
    /// nothing. A checkout that changed since it was preserved, or a move this system cannot make, is refused with why.
    /// </summary>
    public Task<RestorePreviewRead> PreviewRestore(RunAddress address, TaskId task, AttemptId attempt, OperationId preservation,
        CancellationToken wait = default) =>
        Request(address, task, message => new RestorePreviewRead.Unavailable(message), reason => new RestorePreviewRead.Rejected(reason), mark: false,
            () => Leased(task, lease => Task.FromResult(_materializer().PreviewRestore(lease, attempt, preservation)),
                reason => new RestorePreviewRead.Rejected(reason)),
            preview => preview, wait);

    /// <summary>
    /// Restores <paramref name="task"/>'s checkout as the person confirmed the preview <paramref name="preview"/> under
    /// <paramref name="command"/>. Each move is rechecked and journaled, and only the blocks it repaired resolve, so the
    /// tasks they held start again. A changed preview refuses and moves nothing. A repeat returns the receipt.
    /// </summary>
    public Task<Restoration> Restore(RunAddress address, TaskId task, AttemptId attempt, OperationId preservation, Digest preview,
        OperationId command, CancellationToken wait = default) =>
        Request(address, task, message => new Restoration.Unavailable(message), reason => new Restoration.Rejected(reason), mark: true,
            () => Leased(task, lease => Task.FromResult(_materializer().Restore(lease, OperationIds.Derive(command, "restore"), attempt,
                preservation, command, preview)), reason => new Restoration.Rejected(reason)),
            outcome =>
            {
                // A resolved block no longer holds the task; one the restore returned holds it until the journal says otherwise.
                if (outcome is Restoration.Blocked blocked) Hold(task, new TaskHold.Blocked(blocked.Block));
                else if (outcome is Restoration.Restored) _holds.Remove(task);
                return outcome;
            }, wait);

    /// <summary>
    /// Closes <paramref name="attempt"/>, whose turn's execution is unresolved, as stopped, on the person's confirmation
    /// with <paramref name="reason"/>. It records no success and no exit, and nothing launches it again. A repeat returns
    /// the closure. It also works while the run is stopping, which waits for such an attempt to close.
    /// </summary>
    public Task<RunCommand> ConfirmStopped(RunAddress address, TaskId task, AttemptId attempt, string reason, OperationId command,
        CancellationToken wait = default) =>
        Request(address, task, message => new RunCommand.Unavailable(message), reason => new RunCommand.Refused(reason), mark: true, () =>
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
            return Leased(task, lease => Task.FromResult<RunCommand>(_store.Recover(lease, OperationIds.Derive(command, "recover"), attempt,
                    RecoveryOutcome.Stopped, command, reason) switch
                {
                    RunDecision.Rejected rejected => new RunCommand.Refused(rejected.Reason),
                    _ => Accepted,
                }), refused => new RunCommand.Refused(refused));
        }, outcome =>
        {
            if (outcome is RunCommand.Accepted)
            {
                if (_unresolved.Remove(task, out var turn)) turn.Release();
                _holds.Remove(task);
            }
            return outcome;
        }, wait);

    /// <summary>
    /// Runs <paramref name="work"/> under <paramref name="task"/>'s lease: the one a turn whose settlement stayed
    /// unresolved still holds, or a lease taken for it alone, which it gives up afterwards.
    /// </summary>
    private async Task<T> Leased<T>(TaskId task, Func<RunLease, Task<T>> work, Func<RunRejection, T> busy)
    {
        if (_unresolved.TryGetValue(task, out var turn) && turn.Lease.Held) return await work(turn.Lease).ConfigureAwait(false);
        if (_permit!.TakeTask(task) is not LeaseTake.Taken taken) return busy(new(RunProblem.TaskBusy));
        using (taken.Lease) return await work(taken.Lease).ConfigureAwait(false);
    }
}

/// <summary>What one attempt of a task left as evidence, for a person to judge a block or an unresolved turn.</summary>
/// <param name="Attempt">The attempt the evidence is about, or null for a task without one.</param>
/// <param name="Blocks">The unresolved blocks that hold the task, oldest first.</param>
internal sealed record TaskEvidence(TaskId Task, AttemptId? Attempt, ImmutableArray<MaterializationBlock> Blocks)
{
    /// <summary>The newest turn's client process, as its launch recorded it.</summary>
    public ProcessIdentity? Root { get; init; }

    /// <summary>Whether that process still runs, checked now, while no root exit is recorded.</summary>
    public ProcessMatch? RootNow { get; init; }

    /// <summary>The newest turn's recorded root exit, or null when none was recorded.</summary>
    public RunEvent.RootExitObserved? Exit { get; init; }

    /// <summary>The newest turn's retained turn-end observations, in order.</summary>
    public ImmutableArray<CaptureObservation> Captures { get; init; } = [];

    public ImmutableArray<RunEvent.CaptureDisposed> Dispositions { get; init; } = [];

    /// <summary>The newest turn's cleanup, with its steps and failures.</summary>
    public AttemptEvent.CleanedUp? Cleanup { get; init; }
}
