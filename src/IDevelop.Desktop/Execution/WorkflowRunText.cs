using System.Diagnostics;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Conversation;
using IDevelop.Execution;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Execution;

/// <summary>What a workflow run and each of its tasks say on the toolbar, the cards, and the inspector.</summary>
internal static class WorkflowRunText
{
    /// <summary>The run's tone: the one its status pill and its sidebar dot take.</summary>
    public static StatusTone Tone(RunStatus status) => status switch
    {
        RunStatus.Running or RunStatus.Stopping => StatusTone.Running,
        RunStatus.Waiting => StatusTone.Waiting,
        RunStatus.NeedsAttention or RunStatus.Failed => StatusTone.Problem,
        RunStatus.Completed => StatusTone.Complete,
        RunStatus.Paused or RunStatus.Stopped or RunStatus.Abandoned or RunStatus.Elsewhere => StatusTone.Neutral,
        _ => throw new UnreachableException(),
    };

    /// <summary>"2 of 5 done", or null for a run without tasks.</summary>
    public static string? Progress(RunView view) => view.Tasks.Count == 0 ? null
        : $"{view.Tasks.Values.Count(task => task.State == TaskState.Done)} of {view.Tasks.Count} done";

    /// <summary>A task of the run as its card shows it: the node state for its ring and glyph, and its subtitle.</summary>
    /// <param name="active">The run is approved or stopping. A task that a settled run never started shows as not started.</param>
    public static (NodeState State, string Label) Of(TaskView task, Func<TaskId, string> title, bool active = true) => task.State switch
    {
        TaskState.Pending or TaskState.Ready or TaskState.Unsupported when !active => (NodeState.Idle, "Not started"),
        TaskState.Pending => (NodeState.Idle, task.HeldBy.IsEmpty ? "Pending" : $"Waits for {Names(task.HeldBy, title)}"),
        TaskState.Ready => (NodeState.Idle, "Ready"),
        TaskState.Starting => (NodeState.Running, "Starting"),
        TaskState.Running => (NodeState.Running, "Running"),
        TaskState.Settling => (NodeState.Running, "Finishing"),
        TaskState.Waiting when task.Gate is not null => (NodeState.Waiting, "Waiting for approval"),
        TaskState.Waiting when task.Status == AttemptStatus.InReview => (NodeState.InReview, "In review"),
        TaskState.Waiting when task.Status == AttemptStatus.WaitingForInput => (NodeState.Waiting, "Waiting for you"),
        TaskState.Waiting => (NodeState.Waiting, "Reply queued"),
        TaskState.Done when task.Gate is not null => (NodeState.Succeeded, "Approved"),
        TaskState.Done => (NodeState.Succeeded, "Succeeded"),
        TaskState.Stale => (NodeState.Interrupted, "Inputs changed"),
        TaskState.Failed when task.Gate is not null => (NodeState.Cancelled, "Closed"),
        TaskState.Failed => task.End switch
        {
            AttemptEnd.Logged { Outcome: TerminalAttemptOutcome.Cancelled } => (NodeState.Cancelled, "Cancelled"),
            AttemptEnd.Logged { Outcome: TerminalAttemptOutcome.Interrupted } => (NodeState.Interrupted, "Interrupted"),
            AttemptEnd.Recovered => (NodeState.Interrupted, "Closed by recovery"),
            _ => (NodeState.Failed, "Failed"),
        },
        TaskState.Blocked => (NodeState.Failed, "Blocked"),
        TaskState.Uncertain => (NodeState.Interrupted, "Uncertain"),
        TaskState.Refused when task.Problem is { } problem && NodeStates.IsSetup(problem) => (NodeState.NeedsSetup, CardText.ShortReason(problem) ?? "Couldn't start"),
        TaskState.Refused => (NodeState.Failed, "Couldn't start"),
        TaskState.Unsupported => (NodeState.Idle, "Not run by workflows yet"),
        TaskState.SentBack => (NodeState.Failed, "Sent back"),
        _ => throw new UnreachableException(),
    };

    /// <summary>Why the task stands where it does, in a sentence or two, or null when its label says enough.</summary>
    /// <param name="active">The run is approved or stopping. A settled run says nothing more about a task it never started.</param>
    public static string? Detail(TaskView task, Func<TaskId, string> title, bool active = true) => task.State switch
    {
        TaskState.Pending or TaskState.Ready or TaskState.Unsupported when !active => null,
        TaskState.Pending when !task.HeldBy.IsEmpty => $"It starts once {Names(task.HeldBy, title)} {(task.HeldBy.Count == 1 ? "hands" : "hand")} on a result.",
        TaskState.Blocked when task.Block is { } block => $"{Problem(block.Problem)} {block.Detail}".Trim(),
        TaskState.Refused when task.Problem is { } problem => RunText.Describe(problem),
        TaskState.Refused when task.Refusal is { } refusal => Problem(refusal),
        TaskState.Uncertain => "Its turn's end was not recorded, so the run never starts it again by itself.",
        TaskState.Stale => "A task before it handed on a newer result after this one finished.",
        TaskState.SentBack when task.Gate?.Reason is { } reason => $"Sent back: {reason}",
        TaskState.Unsupported => "Workflow runs do not run this kind of node yet.",
        _ => null,
    };

    /// <summary>What a task of the run needs from the person, which its card's glyph shows, or null.</summary>
    public static Attention? Needs(TaskView task, Func<TaskId, string> title)
    {
        var (state, label) = Of(task, title);
        return task.State switch
        {
            TaskState.Waiting when task.Gate is not null || task.Status == AttemptStatus.WaitingForInput => new Attention.Waiting(label),
            TaskState.Failed when state == NodeState.Failed => new Attention.Problem(Detail(task, title) is { } why ? $"{label}: {why}" : label),
            TaskState.Blocked or TaskState.Uncertain or TaskState.SentBack or TaskState.Refused when state != NodeState.NeedsSetup =>
                new Attention.Problem(Detail(task, title) is { } detail ? $"{label}: {detail}" : label),
            _ => null,
        };
    }

    /// <summary>One sentence for each reason the run refused a command.</summary>
    public static string Problem(RunRejection rejection) => rejection.Problem switch
    {
        RunProblem.JournalBusy => "The run's records are busy. Try again in a moment.",
        RunProblem.TaskBusy => "The task is busy. Try again in a moment.",
        RunProblem.RunBusy => "Another step of the run is being recorded. Try again in a moment.",
        RunProblem.RunStopped => "The run has stopped.",
        RunProblem.StorageUnavailable => "iDevelop could not read or write the run's records.",
        RunProblem.IdentityMismatch => "This was meant for another run.",
        RunProblem.StaleInput => "The request changed since you saw it.",
        RunProblem.IncompleteResults => "Not every task has a current result yet.",
        RunProblem.ReuseUnverifiable => "The earlier report no longer matches the workflow, its settings, or the base.",
        RunProblem.UnsupportedWork => "Workflow runs do not run this kind of node yet.",
        RunProblem.TaskUnconfigured => "A task is missing its agent or a required field.",
        RunProblem.NotApproved => "The run has no approval yet.",
        RunProblem.UnresolvedOwnership or RunProblem.UnclosedAttempts or RunProblem.SettlementPending or RunProblem.NotSettled =>
            "A task's last turn is not settled yet.",
        _ => "The run could not do this now.",
    };

    /// <summary>
    /// A sentence for an approval refusal's detail, which names a <see cref="RunProblem"/>, a client problem's kind, or a
    /// message from the system. A name never shows as it is.
    /// </summary>
    public static string ApprovalDetail(string detail) =>
        Enum.TryParse<RunProblem>(detail, out var problem) && Enum.IsDefined(problem) ? Problem(new RunRejection(problem))
        : detail.Length > 0 && !detail.Contains(' ', StringComparison.Ordinal) ? "The planner could not be marked done."
        : detail;

    /// <summary>Whether an approval refusal comes from a lock or a journal that another step holds for a moment.</summary>
    public static bool Transient(ApprovalProblem problem, string detail) => problem == ApprovalProblem.ApprovalBusy ||
        problem == ApprovalProblem.StorageUnavailable && Enum.TryParse<RunProblem>(detail, out var run) && Transient(new RunRejection(run));

    /// <summary>Whether a refusal comes from a lock or a journal that another step holds for a moment, so trying again can succeed.</summary>
    public static bool Transient(RunRejection rejection) => rejection.Problem is RunProblem.JournalBusy or RunProblem.TaskBusy or RunProblem.RunBusy;

    private static string Problem(MaterializationProblem problem) => problem switch
    {
        MaterializationProblem.DirtyWorktree => "Files changed in the task's checkout after its turn ended.",
        MaterializationProblem.UncertainOwnership => "A branch moved that no task of the run moved.",
        MaterializationProblem.FanInConflict => "The results it joins conflict.",
        MaterializationProblem.GitVersionUnsupported => "Joining results needs a newer Git.",
        MaterializationProblem.LiveWriter => "Another writer still holds the task's checkout.",
        MaterializationProblem.InputUnavailable => "An input it needs is unavailable.",
        _ => $"{problem}.",
    };

    private static string Names(IEnumerable<TaskId> tasks, Func<TaskId, string> title)
    {
        var names = tasks.Select(task => $"\"{title(task)}\"").ToArray();
        return names.Length switch
        {
            1 => names[0],
            2 => $"{names[0]} and {names[1]}",
            _ => $"{names[0]} and {names.Length - 1} more",
        };
    }
}
