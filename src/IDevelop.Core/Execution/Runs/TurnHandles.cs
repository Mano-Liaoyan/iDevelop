using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

internal sealed record ExecutionAddress(string Repository, WorkflowId Workflow, RunId Run, TaskId Task, LaunchKey Launch);

internal abstract record TurnIntent
{
    private TurnIntent() { }
    internal sealed record First(OperationId Operation, TaskId Task, AttemptCause Cause, string? BasePrompt = null) : TurnIntent;
    internal sealed record Next(OperationId Operation, LaunchKey Launch, string Prompt) : TurnIntent;
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
    internal sealed record Refused(RunRejection Reason) : TurnSettlement;
}

internal sealed class SettledTurn(RunStore store, ExecutionAddress address, PreparedExecution preparation,
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
    public Release Release() => TurnReceipts.Release(store, Address, Lease);
}

internal enum UnresolvedReason { Uncertain, IncompleteEvidence, OwnershipConflict }

internal sealed class UnresolvedTurn(RunStore store, ExecutionAddress address, UnresolvedReason reason,
    RunRejection? rejection, ProcessMatch? root, RunLease lease)
{
    public ExecutionAddress Address { get; } = address;
    public UnresolvedReason Reason { get; } = reason;
    public RunRejection? Rejection { get; } = rejection;
    public ProcessMatch? Root { get; } = root;
    public RunLease Lease { get; } = lease;
    public Release Release() => TurnReceipts.Release(store, Address, Lease);
}

internal abstract record TurnDisposition
{
    private TurnDisposition() { }
    internal sealed record Published(ResultId Result) : TurnDisposition;
    internal sealed record Resting(AttemptStatus Status, ImmutableArray<string> Queued) : TurnDisposition;
    internal sealed record Ended(AttemptEnd End) : TurnDisposition;
    internal sealed record Blocked(OperationId Block) : TurnDisposition;
}

internal abstract record Release
{
    private Release() { }
    internal sealed record Released(TurnDisposition Receipt) : Release;
    internal sealed record Held(RunRejection Reason) : Release;
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
    public static Release Release(RunStore store, ExecutionAddress address, RunLease lease)
    {
        if (store.Read(address.Workflow, address.Run) is not RunRead.Loaded loaded)
            return new Release.Held(new(RunProblem.NotSettled));
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
        else if (record.Blocks.FirstOrDefault(pair => !pair.Value.Resolved && pair.Value.Block.Attempt == attempt) is { Value: not null } block)
            receipt = new TurnDisposition.Blocked(block.Key);
        if (receipt is null) return new Release.Held(new(RunProblem.NotSettled));
        lease.Dispose();
        return new Release.Released(receipt);
    }
}
