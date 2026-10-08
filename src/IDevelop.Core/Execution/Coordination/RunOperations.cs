using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>
/// The coordinator's operation ids. Each derives from durable identity alone: the run, a task, a launch, or the journal's
/// recorded stop. A repeated or recovered step therefore reruns its original operation, and E3a's receipts make it
/// converge. A fresh operation for an already prepared initial attempt would get <see cref="RunProblem.InvalidClaim"/>.
/// </summary>
internal static class RunOperations
{
    public static OperationId Root(RunId run) => OperationIds.Derive(new OperationId(run.Value), "coordinator");

    /// <summary>The initial turn of <paramref name="task"/>: its preparation, claim, and launch.</summary>
    public static OperationId Initial(RunId run, TaskId task) => OperationIds.Derive(Root(run), "initial/" + task.Value.ToString("D"));

    /// <summary>
    /// The operation that started <paramref name="launch"/>. The coordinator starts only initial turns; a later turn's
    /// operation is the one a continuation must use, so every window derives the same one.
    /// </summary>
    public static OperationId Turn(RunRecord record, LaunchKey launch) =>
        launch.Turn == 1 && record.Attempts[launch.Attempt].Cause is AttemptCause.Initial
            ? Initial(record.Id, record.Attempts[launch.Attempt].Task)
            : OperationIds.Derive(Root(record.Id), $"turn/{launch.Attempt.Value:D}/{launch.Turn}");

    public static OperationId CloseAttempt(OperationId turn) => OperationIds.Derive(turn, "coordinator/close-attempt");

    public static OperationId Publish(OperationId turn) => OperationIds.Derive(turn, "coordinator/publish");

    public static OperationId Accept(OperationId turn) => OperationIds.Derive(turn, "coordinator/accept");

    /// <summary>The root of an Approval node's requests. Each request derives from it and the inputs it fixes.</summary>
    public static OperationId Gate(RunId run, TaskId task) => OperationIds.Derive(Root(run), "gate/" + task.Value.ToString("D"));

    public static OperationId Completed(RunId run) => OperationIds.Derive(Root(run), "settle/completed");

    public static OperationId ReleasePins(RunId run) => OperationIds.Derive(Root(run), "release-pins");

    /// <summary>The operation of the run's recorded stop, which every stop step derives from, whichever command recorded it.</summary>
    public static OperationId? Stop(RunRecord record) =>
        record.Receipts.Values.FirstOrDefault(entry => entry.Event is RunEvent.StopRequested)?.Operation;

    public static OperationId Cancel(OperationId stop, AttemptId attempt) => OperationIds.Derive(stop, "cancel/" + attempt.Value.ToString("D"));

    public static OperationId NotStarted(OperationId stop, AttemptId attempt) => OperationIds.Derive(stop, "not-started/" + attempt.Value.ToString("D"));

    public static OperationId Stopped(OperationId stop) => OperationIds.Derive(stop, "settle/stopped");
}
