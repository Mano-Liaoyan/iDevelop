using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

internal sealed record ExecutionAddress(string Repository, WorkflowId Workflow, RunId Run, TaskId Task, LaunchKey Launch);

internal abstract record TurnIntent
{
    private TurnIntent() { }
    internal sealed record First(OperationId Operation, TaskId Task, AttemptCause Cause, string? BasePrompt = null) : TurnIntent;
    internal sealed record Next(OperationId Operation, LaunchKey Launch, string Prompt) : TurnIntent
    {
        /// <summary>A reviewer's turn after a fix round: what the fix reported, which its turn request records.</summary>
        public FixReport? Report { get; init; }
    }
}

internal abstract record TurnStart
{
    private TurnStart() { }
    internal sealed record Started(RunningTurn Turn) : TurnStart;
    internal sealed record Settled(TurnSettlement Settlement) : TurnStart;
    internal sealed record Existing(LaunchKey Launch, RunningTurn? Running, TurnSettlement? Settlement) : TurnStart;
    internal sealed record Blocked(MaterializationBlock Block) : TurnStart;
    internal sealed record Refused(RunRejection Reason, StartProblem? Client = null) : TurnStart;
}

internal sealed class RunningTurn(ExecutionAddress address, Task rootExited, Task<TurnSettlement> settlement,
    Func<Task<SendResult>> cancel, Func<string, bool, Task<SendResult>> send)
{
    public ExecutionAddress Address { get; } = address;
    public Task RootExited { get; } = rootExited;
    public Task<TurnSettlement> Settlement { get; } = settlement;
    public Task<SendResult> CancelAsync() => cancel();
    public Task<SendResult> SendAsync(string text, bool stopTurn) => send(text, stopTurn);
}

internal abstract record TurnSettlement
{
    private TurnSettlement() { }
    internal sealed record Settled(SettledTurn Turn) : TurnSettlement;
    internal sealed record Unresolved(UnresolvedTurn Turn) : TurnSettlement;
}

internal abstract record Reconciliation
{
    private Reconciliation() { }
    internal sealed record Found(TurnSettlement Settlement) : Reconciliation;
    internal sealed record Refused(RunRejection Reason) : Reconciliation;
}

internal sealed class SettledTurn(Func<Release> release, ExecutionAddress address, PreparedExecution preparation,
    LogCheckpoint log, RunEvent.RootExitObserved exit, RunEvent.TurnClosed closure, RunEvent.CaptureDisposed capture,
    AttemptRecord attempt, AttemptEvent.CleanedUp? cleanup, RunLease lease)
{
    public ExecutionAddress Address { get; } = address;
    public PreparedExecution Preparation { get; } = preparation;
    public LogCheckpoint Log { get; } = log;
    public RunEvent.RootExitObserved Exit { get; } = exit;
    public RunEvent.TurnClosed Closure { get; } = closure;
    public RunEvent.CaptureDisposed Capture { get; } = capture;
    public AttemptRecord Attempt { get; } = attempt;
    public AttemptEvent.CleanedUp? Cleanup { get; } = cleanup;
    public RunLease Lease { get; } = lease;
    public Release Release() => release();
}

internal enum UnresolvedReason { Uncertain, IncompleteEvidence, OwnershipConflict }

internal sealed class UnresolvedTurn(Func<Release> release, ExecutionAddress address, UnresolvedReason reason,
    RunRejection? rejection, ProcessMatch? root, RunLease lease)
{
    public ExecutionAddress Address { get; } = address;
    public UnresolvedReason Reason { get; } = reason;
    public RunRejection? Rejection { get; } = rejection;
    public ProcessMatch? Root { get; } = root;
    public RunLease Lease { get; } = lease;
    public Release Release() => release();
}

internal abstract record TurnDisposition
{
    private TurnDisposition() { }
    internal sealed record Published(ResultId Result) : TurnDisposition;
    internal sealed record Resting(AttemptStatus Status, ImmutableArray<string> Queued) : TurnDisposition;
    internal sealed record Ended(AttemptEnd End) : TurnDisposition;
    internal sealed record Blocked(OperationId Block) : TurnDisposition;
    internal sealed record Preserved(OperationId Plan) : TurnDisposition;
}

internal abstract record Release
{
    private Release() { }
    internal sealed record Released(TurnDisposition Receipt) : Release;
    internal sealed record Held(RunRejection Reason) : Release;
}

/// <summary>How a resting attempt ends: Mark done of a waiting attempt, a review's conclusion, or cancellation.</summary>
internal abstract record RestingEnd
{
    private RestingEnd() { }
    internal sealed record MarkDone : RestingEnd;
    internal sealed record Conclude(string? Failure) : RestingEnd;
    internal sealed record Cancel : RestingEnd;
}

internal abstract record RestingClose
{
    private RestingClose() { }
    internal sealed record Closed(ClosedAttempt Attempt) : RestingClose;
    internal sealed record Blocked(MaterializationBlock Block) : RestingClose;
    internal sealed record Refused(RunRejection Reason) : RestingClose;
}

/// <summary>A closed attempt whose task lease the closure still holds, until <see cref="Release"/> finds a durable receipt.</summary>
internal sealed class ClosedAttempt(Func<ClosedAttempt, Release> release, ExecutionAddress address, AttemptEnd end, RunLease lease)
{
    private readonly Lock _gate = new();
    private TurnDisposition? _receipt;
    public ExecutionAddress Address { get; } = address;
    public AttemptEnd End { get; } = end;
    public RunLease Lease { get; } = lease;

    public bool Released
    {
        get { lock (_gate) return _receipt is not null; }
    }

    public Release Release()
    {
        lock (_gate)
        {
            if (_receipt is not null) return new Release.Released(_receipt);
            var outcome = release(this);
            if (outcome is Execution.Release.Released released)
            {
                _receipt = released.Receipt;
                Lease.Dispose();
            }
            return outcome;
        }
    }
}

/// <summary>The live checkout against an attempt's recorded baseline before its resting closure.</summary>
internal abstract record RestingCheck
{
    private RestingCheck() { }
    internal sealed record Matched : RestingCheck;
    internal sealed record Drifted(MaterializationBlock Block) : RestingCheck;
    internal sealed record Rejected(RunRejection Reason) : RestingCheck;
}

internal abstract record ClaimCheck
{
    private ClaimCheck() { }
    internal sealed record Granted(RunEvent.TurnClaimed Claim) : ClaimCheck;
    internal sealed record Existing(RunEvent.TurnClaimed Claim) : ClaimCheck;
    internal sealed record Blocked(MaterializationBlock Block) : ClaimCheck;
    internal sealed record Rejected(RunRejection Reason) : ClaimCheck;
}

internal static class TurnReceipts
{
    /// <summary>A preservation of the launch's attempt recorded after the launch's claim, the newest first.</summary>
    private static OperationId? Preservation(RunRecord record, LaunchKey launch)
    {
        var claimed = record.Receipts.Values.FirstOrDefault(entry => entry.Event is RunEvent.TurnClaimed claim && claim.Key == launch)?.Sequence;
        return claimed is null ? null : record.Receipts.Values
            .Where(entry => entry.Sequence > claimed && entry.Event is RunEvent.Preserved preserved &&
                record.Plans.GetValueOrDefault(preserved.Plan) is MaterializationPlan.Preservation plan && plan.Attempt == launch.Attempt)
            .OrderByDescending(entry => entry.Sequence).Select(entry => (OperationId?)((RunEvent.Preserved)entry.Event).Plan).FirstOrDefault();
    }

    public static TurnDisposition? Receipt(RunStore store, ExecutionAddress address)
    {
        if (store.Read(address.Workflow, address.Run) is not RunRead.Loaded loaded)
            return null;
        var record = loaded.Record;
        var attempt = address.Launch.Attempt;
        var log = AttemptEvidence.Read(store.AttemptFolder(address.Workflow, address.Run, address.Task, attempt));
        var last = record.Claims.Keys.Where(key => key.Attempt == attempt).OrderBy(key => key.Turn).LastOrDefault();
        TurnDisposition? receipt = null;
        if (record.Results.FirstOrDefault(result => result.Origin is ResultOrigin.Executed executed && executed.Attempt == attempt) is { } result)
            receipt = new TurnDisposition.Published(result.Id);
        else if (!record.Closures.ContainsKey(attempt) && log is { Rejection: null, Record: { BetweenTurns: true } resting } &&
            record.TurnClosures.ContainsKey(last) &&
            (resting.Status is AttemptStatus.WaitingForInput or AttemptStatus.InReview || resting.Status == AttemptStatus.Running && !resting.Queued.IsEmpty))
            receipt = new TurnDisposition.Resting(resting.Status, [.. resting.Queued.Select(message => message.Text)]);
        else if (record.Closures.TryGetValue(attempt, out var end) && end is not AttemptEnd.Logged { Outcome: TerminalAttemptOutcome.Succeeded })
            receipt = new TurnDisposition.Ended(end);
        else if (Preservation(record, address.Launch) is { } preserved)
            receipt = new TurnDisposition.Preserved(preserved);
        else if (record.Blocks.FirstOrDefault(pair => !pair.Value.Resolved && pair.Value.Block.Attempt == attempt) is { Value: not null } block)
            receipt = new TurnDisposition.Blocked(block.Key);
        return receipt;
    }
}
