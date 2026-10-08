using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

public sealed partial class ProjectRuns
{
    /// <summary>
    /// Ends a resting attempt with Mark done, a review's conclusion, or cancellation, and returns the closed attempt with
    /// its task lease. A duplicate returns the same handle while it holds the lease. Each step finds its earlier effect
    /// first, so a rerun with the same operation converges after a crash, a fault, or a refused journal write.
    /// </summary>
    internal Task<RestingClose> CloseResting(CoordinatorPermit permit, OperationId operation, AttemptId attempt, RestingEnd end,
        CancellationToken wait = default)
    {
        Task<RestingClose> command;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_leaving is not null, this);
            var key = (permit.Workflow, permit.Run, operation);
            var intent = new ClosureIntent(attempt, end);
            if (_commands.TryGetValue(key, out var prior))
            {
                if (!Equals(prior.Intent, intent) || prior.Task is not Task<RestingClose> closing)
                    return Task.FromResult<RestingClose>(new RestingClose.Refused(new(RunProblem.OperationConflict)));
                if (!closing.IsCompleted ||
                    closing.IsCompletedSuccessfully && closing.Result is RestingClose.Closed { Attempt: { Released: false, Lease.Held: true } })
                    return closing.WaitAsync(wait);
                _commands.Remove(key);
            }
            command = Task.Run(() => CloseRestingCore(permit, operation, attempt, end));
            _commands.Add(key, new(intent, command));
        }
        return command.WaitAsync(wait);
    }

    private RestingClose CloseRestingCore(CoordinatorPermit permit, OperationId operation, AttemptId attemptId, RestingEnd end)
    {
        var store = TurnStore;
        RunLease? lease = null;
        try
        {
            var closure = OperationIds.Derive(operation, "close");
            if (store.Read(permit.Workflow, permit.Run) is not RunRead.Loaded loaded) return Refused(RunProblem.StorageUnavailable);
            var record = loaded.Record;
            if (!record.Attempts.TryGetValue(attemptId, out var attempt)) return Refused(RunProblem.UnknownAttempt);
            // The closing events carry no operation. A reused operation is refused before anything reaches the log.
            if (record.Receipts.TryGetValue(closure, out var receipt) &&
                (receipt.Event is not RunEvent.AttemptClosed closedEvent || closedEvent.Attempt != attemptId))
                return Refused(RunProblem.OperationConflict);
            var folder = store.AttemptFolder(permit.Workflow, permit.Run, attempt.Task, attemptId);
            // A closed attempt answers from its log alone: the closing event after its last turn, compared by content.
            if (record.Closures.TryGetValue(attemptId, out var ended) && !ClosedAs(record, folder, attemptId, ended, end))
                return Refused(RunProblem.OutcomeMismatch);
            if (permit.TakeTask(attempt.Task) is not LeaseTake.Taken taken) return Refused(RunProblem.TaskBusy);
            lease = taken.Lease;
            if (store.Read(permit.Workflow, permit.Run) is not RunRead.Loaded current) return Refused(RunProblem.StorageUnavailable);
            record = current.Record;
            var last = LastLaunch(record, attemptId);
            var turn = last.Turn == 0 ? null : record.TurnClosures.GetValueOrDefault(last);
            var address = new ExecutionAddress(record.Repository!, permit.Workflow, permit.Run, attempt.Task, last);
            if (record.Closures.TryGetValue(attemptId, out ended))
                return ClosedAs(record, folder, attemptId, ended, end) ? Closed(store, address, ended, ref lease) : Refused(RunProblem.OutcomeMismatch);
            if (turn is null) return Refused(RunProblem.InvalidClaim);
            var log = AttemptEvidence.Read(folder);
            if (log.Rejection is not null || AttemptEvidence.Suffix(folder, turn, log.Checkpoint!) is not { } suffix)
                return Refused(RunProblem.EvidenceMismatch);
            switch (WithoutReplies(suffix, end))
            {
                case []:
                    if (!Rests(AttemptEvidence.ReadPrefix(folder, turn).Record, end)) return Refused(RunProblem.InvalidClaim);
                    switch (TurnMaterializer(store).CheckRestingBaseline(lease, operation, attemptId, end is RestingEnd.Cancel))
                    {
                        case RestingCheck.Rejected rejected: return new RestingClose.Refused(rejected.Reason);
                        case RestingCheck.Drifted drifted when end is not RestingEnd.Cancel: return new RestingClose.Blocked(drifted.Block);
                    }
                    Probe?.Invoke("runner.close.append.before");
                    using (var append = AttemptLog.Open(folder)) append.Append(ClosingEvent(end, TimeProvider.GetUtcNow()));
                    Probe?.Invoke("runner.close.append.after");
                    log = AttemptEvidence.Read(folder);
                    if (log.Rejection is not null) return Refused(RunProblem.EvidenceMismatch);
                    break;
                case [var only] when ClosingEnd(only) is { } recorded:
                    if (recorded != end) return Refused(RunProblem.OutcomeMismatch);
                    break;
                default: return Refused(RunProblem.InvalidClaim);
            }
            Probe?.Invoke("runner.close.attempt.before");
            return store.CloseAttempt(permit, closure, attemptId, Outcome(end), log.Checkpoint!) switch
            {
                RunDecision.Recorded { Event: RunEvent.AttemptClosed closed } => Closed(store, address, closed.End, ref lease),
                RunDecision.Existing { Event: RunEvent.AttemptClosed closed } => Closed(store, address, closed.End, ref lease),
                RunDecision.Rejected rejected => new RestingClose.Refused(rejected.Reason),
                _ => throw new InvalidOperationException(),
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Refused(RunProblem.StorageUnavailable);
        }
        finally
        {
            lease?.Dispose();
        }

        static RestingClose Refused(RunProblem problem) => new RestingClose.Refused(new(problem));
    }

    private static LaunchKey LastLaunch(RunRecord record, AttemptId attempt) =>
        record.Claims.Keys.Where(key => key.Attempt == attempt).OrderBy(key => key.Turn).LastOrDefault();

    /// <summary>Whether the attempt closed with exactly <paramref name="end"/>'s closing event after its last turn.</summary>
    private static bool ClosedAs(RunRecord record, string folder, AttemptId attempt, AttemptEnd ended, RestingEnd end) =>
        ended is AttemptEnd.Logged logged && logged.Outcome == Outcome(end) &&
        record.TurnClosures.GetValueOrDefault(LastLaunch(record, attempt)) is { } turn &&
        AttemptEvidence.Suffix(folder, turn, logged.Evidence) is { } suffix && WithoutReplies(suffix, end) is [var only] && ClosingEnd(only) == end;

    /// <summary>
    /// What the log adds after the last turn, less the person's replies that a cancellation leaves unsent. Only a run-owned
    /// conversation writes a reply after a turn closed, and the next turn consumes it; a stop or a cancel closes it unsent.
    /// </summary>
    private static ImmutableArray<AttemptEvent> WithoutReplies(ImmutableArray<AttemptEvent> suffix, RestingEnd end) =>
        end is RestingEnd.Cancel ? [.. suffix.SkipWhile(e => e is AttemptEvent.MessageQueued)] : suffix;

    private static RestingClose Closed(RunStore store, ExecutionAddress address, AttemptEnd end, ref RunLease? lease)
    {
        var handle = new ClosedAttempt(closed => TurnReceipts.Receipt(store, closed.Address) is { } receipt
            ? new Release.Released(receipt) : new Release.Held(new(RunProblem.NotSettled)), address, end, lease!);
        lease = null;
        return new RestingClose.Closed(handle);
    }

    /// <summary>Whether the attempt rests the way <paramref name="end"/> needs: waiting, in review, or stopped with queued text.</summary>
    private static bool Rests(AttemptRecord? record, RestingEnd end) => record is { BetweenTurns: true } && end switch
    {
        RestingEnd.MarkDone => record.Status == AttemptStatus.WaitingForInput,
        RestingEnd.Conclude => record.Status == AttemptStatus.InReview,
        RestingEnd.Cancel => record.Status is AttemptStatus.WaitingForInput or AttemptStatus.InReview ||
            record.Status == AttemptStatus.Running && !record.Queued.IsEmpty,
        _ => false,
    };

    private static AttemptEvent ClosingEvent(RestingEnd end, DateTimeOffset at) => end switch
    {
        RestingEnd.MarkDone => new AttemptEvent.MarkedDone(at),
        RestingEnd.Conclude conclude => new AttemptEvent.Concluded(at, conclude.Failure),
        RestingEnd.Cancel => new AttemptEvent.CancelRequested(at),
        _ => throw new InvalidOperationException(),
    };

    /// <summary>The end a closing event records, compared by kind and failure text, never by time.</summary>
    private static RestingEnd? ClosingEnd(AttemptEvent e) => e switch
    {
        AttemptEvent.MarkedDone => new RestingEnd.MarkDone(),
        AttemptEvent.Concluded concluded => new RestingEnd.Conclude(concluded.Failure),
        AttemptEvent.CancelRequested => new RestingEnd.Cancel(),
        _ => null,
    };

    private static TerminalAttemptOutcome Outcome(RestingEnd end) => end switch
    {
        RestingEnd.MarkDone => TerminalAttemptOutcome.Succeeded,
        RestingEnd.Conclude { Failure: null } => TerminalAttemptOutcome.Succeeded,
        RestingEnd.Conclude => TerminalAttemptOutcome.Failed,
        RestingEnd.Cancel => TerminalAttemptOutcome.Cancelled,
        _ => throw new InvalidOperationException(),
    };

    private sealed record ClosureIntent(AttemptId Attempt, RestingEnd End);
}
