using System.Collections.Immutable;
using System.Text.Json;
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
    /// What a person needs to judge <paramref name="task"/>'s state: the blocks that hold it, with where each shared ref
    /// they name pointed and points now, and its attempt's cause and newest turn: its root process, root exit, captures,
    /// and cleanup. <paramref name="attempt"/> names the attempt, such as a block's; by default it is the task's newest
    /// one, and a task without an attempt has its blocks alone. Null when the journal cannot be read or the run has no such
    /// task. It reads the journal, the blocks' evidence, and the attempt's log off the loop, and checks whether the root
    /// process still runs.
    /// </summary>
    public TaskEvidence? Evidence(TaskId task, AttemptId? attempt = null)
    {
        if (Record() is not { } record || !record.Revision.Snapshot.Tasks.ContainsKey(task)) return null;
        AttemptId? latest = RunProjection.LatestAttempts(record).TryGetValue(task, out var newest) ? newest : null;
        var blocks = RunProjection.Blocks(record, task, latest, record.CurrentResults.GetValueOrDefault(task)).ToImmutableArray();
        var refs = blocks.SelectMany(block => SharedRefs(record, block)).ToImmutableArray();
        if ((attempt ?? latest) is not { } id || !record.Attempts.TryGetValue(id, out var owner)) return new(task, null, blocks) { Refs = refs };
        var launch = RunProjection.LastLaunch(record, id);
        ImmutableArray<AttemptEvent> events;
        try { events = AttemptEvidence.Read(_store.AttemptFolder(Address.Workflow, Address.Run, owner.Task, id)).Events; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { events = []; }
        ProcessIdentity? root = launch is { } turn ? Launched(events, turn.Turn) : null;
        var captures = launch is { } key
            ? record.Captures.Values.SelectMany(list => list).Where(observation => observation.Launch == key)
                .OrderBy(observation => observation.Started).ThenBy(observation => observation.Ordinal).ToImmutableArray()
            : [];
        return new(task, id, blocks)
        {
            Refs = refs,
            Cause = owner.Cause,
            Turn = launch?.Turn,
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
            held => Leased(held, task, async lease => await _materializer().Preserve(lease, command, attempt).ConfigureAwait(false),
                reason => new Preservation.Rejected(reason)),
            preserved => preserved, wait);

    /// <summary>
    /// What restoring <paramref name="task"/>'s checkout from the preservation <paramref name="preservation"/> would move:
    /// its current state, the recorded baseline, the paths, refs, and index lock, the blocks it repairs, and the blocks it
    /// only checks again. It records nothing. A checkout that changed since it was preserved, or a move this system cannot
    /// make, is refused with why.
    /// </summary>
    public Task<RestorePreviewRead> PreviewRestore(RunAddress address, TaskId task, AttemptId attempt, OperationId preservation,
        CancellationToken wait = default) =>
        Request(address, task, message => new RestorePreviewRead.Unavailable(message), reason => new RestorePreviewRead.Rejected(reason), mark: false,
            held => Leased(held, task, lease => Task.FromResult(_materializer().PreviewRestore(lease, attempt, preservation)),
                reason => new RestorePreviewRead.Rejected(reason)),
            preview => preview, wait);

    /// <summary>
    /// Restores <paramref name="task"/>'s checkout as the person confirmed the preview <paramref name="preview"/> under
    /// <paramref name="command"/>. Each move is rechecked and journaled. Only the blocks it repaired, and the rechecked ones
    /// whose refs are back as recorded, resolve, so the tasks they held start again; the receipt names them. A changed
    /// preview refuses and moves nothing. A repeat returns the receipt.
    /// </summary>
    public Task<Restoration> Restore(RunAddress address, TaskId task, AttemptId attempt, OperationId preservation, Digest preview,
        OperationId command, CancellationToken wait = default) =>
        Request(address, task, message => new Restoration.Unavailable(message), reason => new Restoration.Rejected(reason), mark: true,
            held => Leased(held, task, lease => Task.FromResult(_materializer().Restore(lease, OperationIds.Derive(command, "restore"), attempt,
                preservation, command, preview)), reason => new Restoration.Rejected(reason)),
            // The journal records what the restore resolved and any block it met, and the next decision reads both.
            restored => restored, wait);

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
    /// For each shared ref a block names, the commit it pointed at when its turn was prepared and when the block was found,
    /// from the snapshots the block keeps: a publication's before and after, or a capture's preparation and observations.
    /// </summary>
    private IEnumerable<SharedRefDrift> SharedRefs(RunRecord record, MaterializationBlock block)
    {
        if (block.Scope is not BlockScope.Refs { Names: var names }) return [];
        EvidenceFile? Named(string name) => block.Evidence.LastOrDefault(file => file.RelativePath.EndsWith("/" + name, StringComparison.Ordinal));
        var before = Snapshot(record, Named("refs-before.json") ?? Named("shared-refs.json"));
        var after = Snapshot(record, Named("refs-after.json") ?? Named("refs.json"));
        return names.Select(name => new SharedRefDrift(name, before?.GetValueOrDefault(name), after?.GetValueOrDefault(name),
            before is not null && after is not null));
    }

    private SortedDictionary<string, CommitId?>? Snapshot(RunRecord record, EvidenceFile? file)
    {
        if (file is null) return null;
        try
        {
            var bytes = RunStorage.Read(new RunStorage(Address.Project, record.Workflow, record.Id).Folder, file.RelativePath, file.Content, file.ByteLength);
            var values = JsonSerializer.Deserialize<SortedDictionary<string, CommitId>>(bytes, RunJournal.Options);
            return values is null ? null : new(values.ToDictionary(pair => pair.Key, pair => (CommitId?)pair.Value), StringComparer.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}

/// <summary>What one attempt of a task left as evidence, for a person to judge a block or an unresolved turn.</summary>
/// <param name="Attempt">The attempt the evidence is about, or null for a task without one.</param>
/// <param name="Blocks">The unresolved blocks that hold the task, oldest first.</param>
internal sealed record TaskEvidence(TaskId Task, AttemptId? Attempt, ImmutableArray<MaterializationBlock> Blocks)
{
    /// <summary>For each shared ref the blocks name, where it pointed and where it points now.</summary>
    public ImmutableArray<SharedRefDrift> Refs { get; init; } = [];

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

    /// <summary>The newest turn's retained turn-end observations, in order.</summary>
    public ImmutableArray<CaptureObservation> Captures { get; init; } = [];

    public ImmutableArray<RunEvent.CaptureDisposed> Dispositions { get; init; } = [];

    /// <summary>The newest turn's cleanup, with its steps and failures.</summary>
    public AttemptEvent.CleanedUp? Cleanup { get; init; }
}

/// <summary>
/// A shared ref a block names: the commit it pointed at when the turn was prepared, and when the block was recorded, each
/// null for an absent ref. <paramref name="Known"/> is false when the block kept no snapshot of them.
/// </summary>
internal sealed record SharedRefDrift(string Name, CommitId? Recorded, CommitId? Observed, bool Known);
