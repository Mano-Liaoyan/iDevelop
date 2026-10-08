using IDevelop.Workflows;

namespace IDevelop.Execution;

internal enum GateStatus { Waiting, Approved, SentBack, Closed }

/// <summary>An Approval node's newest request as the person sees it.</summary>
/// <param name="Reason">Why the person sent it back, once they did.</param>
internal sealed record GateView(GateRequest Request, GateStatus Status, string? Reason)
{
    public string Label => Status switch
    {
        GateStatus.Waiting => "Waiting for approval",
        GateStatus.Approved => "Approved",
        GateStatus.SentBack => "Sent back",
        GateStatus.Closed => "Closed",
        _ => throw new InvalidOperationException(),
    };
}

/// <summary>The request a person answers, as they saw it: its id and the inputs it fixed.</summary>
internal sealed record GateResponse(GateId Request, InputId Inputs);

internal abstract record GateReply
{
    private GateReply() { }

    /// <summary>The journal entry of the answer. A repeat of the recorded answer returns the original entry.</summary>
    internal sealed record Recorded(RunEntry Receipt, bool Repeat) : GateReply;

    /// <summary>
    /// The request was answered otherwise, replaced, or its inputs were superseded. Nothing was recorded.
    /// <paramref name="Current"/> is the open request the node waits on now, if it has one yet. A sent-back request waits
    /// for nothing, so it is never <paramref name="Current"/>.
    /// </summary>
    internal sealed record Stale(GateRequest? Current) : GateReply;

    /// <summary>Another window controls the run. Nothing was recorded.</summary>
    internal sealed record Unavailable(string Message) : GateReply;

    internal sealed record Refused(RunRejection Reason) : GateReply;
}

/// <summary>An Approval node in the run projection. It never has an attempt.</summary>
internal static class GateProjection
{
    /// <summary>
    /// A current approval is done: a node without a checkout has nothing that drifts. A request preparation that this window
    /// holds or the journal blocks shows as such. A live request waits for the person, or shows that they sent it back, and
    /// closes once the run stops. Without a live request, as when the inputs of the last one were superseded, the schedule
    /// decides: the node is ready for a new request, or pending.
    /// </summary>
    public static TaskView? Of(RunRecord record, TaskId task, ResultRecord? result, TaskHold? hold)
    {
        var latest = RunReducer.LatestGate(record, task);
        var gate = latest is null ? null : View(record, latest);
        if (result is not null && !record.StaleResults.Contains(result.Id)) return new(task, TaskState.Done) { Result = result.Id, Gate = gate };
        switch (hold)
        {
            case TaskHold.Blocked blocked: return new(task, TaskState.Blocked) { Block = blocked.Block, Gate = gate };
            case TaskHold.Refused refused: return new(task, TaskState.Refused) { Refusal = refused.Reason, Problem = refused.Problem, Gate = gate };
        }
        if (RunProjection.Blocks(record, task, null, null).FirstOrDefault() is { } block) return new(task, TaskState.Blocked) { Block = block, Gate = gate };
        return RunReducer.LiveGate(record, task) switch
        {
            { Decision: GateDecision.SentBack } => new(task, TaskState.SentBack) { Gate = gate },
            { Decision: null } => new(task, record.Phase == RunPhase.Approved ? TaskState.Waiting : TaskState.Failed) { Gate = gate },
            _ => null,
        };
    }

    public static GateView View(RunRecord record, GateState state) => state.Decision switch
    {
        GateDecision.Approved => new(state.Request, GateStatus.Approved, null),
        GateDecision.SentBack sent => new(state.Request, GateStatus.SentBack, sent.Reason),
        _ => new(state.Request, record.Phase == RunPhase.Approved ? GateStatus.Waiting : GateStatus.Closed, null),
    };
}
