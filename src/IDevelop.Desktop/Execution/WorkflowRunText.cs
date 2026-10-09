using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
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

    /// <summary>
    /// The run's status on its pill. While the run goes on, it counts only the tasks whose client runs: "3 running", or
    /// "Running" for one. With none running, it says "Starting" or "Finishing" while a task's client starts or finishes,
    /// and otherwise the run's own label.
    /// </summary>
    public static string Status(RunView view)
    {
        if (view.Status != RunStatus.Running) return view.Label;
        var tasks = view.Tasks.Values;
        return tasks.Count(task => task.State == TaskState.Running) switch
        {
            > 1 and var running => $"{running} running",
            1 => "Running",
            _ when tasks.Any(task => task.State == TaskState.Starting) => "Starting",
            _ when tasks.Any(task => task.State == TaskState.Settling) => "Finishing",
            _ => view.Label,
        };
    }

    /// <summary>Whether the task's client starts, runs, or finishes, which its card shows with the running ring.</summary>
    public static bool Busy(TaskView task) => task.State is TaskState.Starting or TaskState.Running or TaskState.Settling;

    /// <summary>
    /// What the run's busy tasks do, by title, as in Running "A" and "B" · Starting "C". Null while none is busy.
    /// </summary>
    public static string? Working(IEnumerable<TaskView> tasks, Func<TaskId, string> title)
    {
        var busy = tasks.Where(Busy).Select(task => (task.State, Title: title(task.Task), task.Task))
            .OrderBy(task => task.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(task => task.Task).ToArray();
        var groups = new[] { (TaskState.Running, "Running"), (TaskState.Starting, "Starting"), (TaskState.Settling, "Finishing") }
            .Select(group => (Verb: group.Item2, Titles: busy.Where(task => task.State == group.Item1).Select(task => task.Title).ToArray()))
            .Where(group => group.Titles.Length > 0)
            .Select(group => $"{group.Verb} {Listed(group.Titles)}")
            .ToArray();
        return groups.Length == 0 ? null : string.Join(" · ", groups);
    }

    /// <summary>
    /// <paramref name="text"/> with each quoted title kept on one line beside the word before it, so a line wraps only
    /// between items: after a comma or a middle dot. Spaces and hyphens inside quotes, a space between a word and an opening
    /// quote, and a space before a middle dot become non-breaking, and a slash inside quotes joins the next character.
    /// </summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? Unbroken(string? text)
    {
        if (text is null) return null;
        var unbroken = new StringBuilder(text.Length);
        var quoted = false;
        for (var at = 0; at < text.Length; at++)
        {
            var character = text[at];
            if (character == '"') quoted = !quoted;
            else if (quoted && character is ' ' or '-' or '/')
            {
                unbroken.Append(character switch { ' ' => NoBreakSpace, '-' => NoBreakHyphen, _ => "/" + WordJoiner });
                continue;
            }
            else if (!quoted && character == ' ' && at + 1 < text.Length &&
                (text[at + 1] == '"' && at > 0 && char.IsLetter(text[at - 1]) || text[at + 1] == '·'))
            {
                unbroken.Append(NoBreakSpace);
                continue;
            }
            unbroken.Append(character);
        }
        return unbroken.ToString();
    }

    private const string NoBreakSpace = "\u00A0";
    private const string NoBreakHyphen = "\u2011";
    private const string WordJoiner = "\u2060";

    /// <summary>
    /// "2 of 5 done" over the tasks the run can reach, or null for a run without any. Run Workflow reaches every task; a run
    /// that a node's Run started reaches the nodes the person ran and the tasks after them, not a task nobody ran (#90).
    /// </summary>
    /// <remarks>
    /// A task whose result the run carried from an earlier run did not run in it, so it counts apart (#90): "2 of 2 done ·
    /// 1 from an earlier run".
    /// </remarks>
    public static string? Progress(RunView view)
    {
        var tasks = view.Tasks.Values;
        var reached = tasks.Count(task => task.State != TaskState.Unrequested && !task.Carried);
        var carried = tasks.Count(task => task.Carried);
        var ran = reached > 0 ? $"{tasks.Count(task => task.State == TaskState.Done && !task.Carried)} of {reached} done" : null;
        var earlier = carried switch
        {
            0 => null,
            1 => "1 from an earlier run",
            _ => $"{carried} from earlier runs",
        };
        return ran is null ? earlier : earlier is null ? ran : $"{ran} · {earlier}";
    }

    /// <summary>
    /// What a completed run's tasks still wait for: each task of the run that waits for a task nobody ran, as
    /// "\"Ship\" waits for \"Render view\"", in title order and joined by middle dots. Null when none waits.
    /// </summary>
    public static string? StillWaiting(IEnumerable<TaskView> tasks, Func<TaskId, string> title)
    {
        var all = tasks.ToDictionary(task => task.Task);
        string[] waiting = [.. all.Values
            .Where(task => task.State == TaskState.Pending)
            .Select(task => (task.Task, Unrun: task.HeldBy.Where(holder => all.GetValueOrDefault(holder) is { State: TaskState.Unrequested }).ToArray()))
            .Where(task => task.Unrun.Length > 0)
            .OrderBy(task => title(task.Task), StringComparer.CurrentCultureIgnoreCase).ThenBy(task => task.Task)
            .Select(task => $"\"{title(task.Task)}\" waits for {Names(task.Unrun, title)}")];
        return waiting.Length == 0 ? null : string.Join(" · ", waiting) + ".";
    }

    /// <summary>
    /// The run says no more of the task than that it has not started: nobody ran it in the active run, or a settled run
    /// never started it. Its card then shows its agent line, as a card outside any run does (#90).
    /// </summary>
    public static bool NotStarted(TaskView task, bool active) => task.State == TaskState.Unrequested ||
        !active && task.State is TaskState.Pending or TaskState.Ready or TaskState.Unsupported;

    /// <summary>A task of the run as its card shows it: the node state for its ring and glyph, and its subtitle.</summary>
    /// <param name="active">The run is approved or stopping. A task that a settled run never started shows as not started.</param>
    public static (NodeState State, string Label) Of(TaskView task, Func<TaskId, string> title, bool active = true) => task.State switch
    {
        TaskState.Pending or TaskState.Ready or TaskState.Unsupported or TaskState.Unrequested when !active => (NodeState.Idle, "Not started"),
        // In a run that a node's Run started, nobody ran it and nothing before it is part of the run (#90).
        TaskState.Unrequested => (NodeState.Idle, "Not started"),
        // A card fits one title; the inspector's detail names every task it waits for.
        TaskState.Pending => (NodeState.Idle, task.HeldBy.Count switch
        {
            0 => "Pending",
            1 => $"Waits for \"{title(task.HeldBy.Min)}\"",
            var count => $"Waits for {count} tasks",
        }),
        TaskState.Ready => (NodeState.Idle, "Ready"),
        TaskState.Starting => (NodeState.Running, "Starting"),
        TaskState.Running => (NodeState.Running, "Running"),
        TaskState.Settling => (NodeState.Running, "Finishing"),
        TaskState.Waiting when task.Gate is not null => (NodeState.Waiting, "Waiting for approval"),
        TaskState.Waiting when task.Fix is not null => (NodeState.Waiting, "Fix interrupted"),
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
            AttemptEnd.Recovered { Outcome: RecoveryOutcome.NotStarted } => (NodeState.Cancelled, "Not started"),
            AttemptEnd.Recovered => (NodeState.Interrupted, "Closed as stopped"),
            _ => (NodeState.Failed, "Failed"),
        },
        TaskState.Blocked => (NodeState.Failed, "Blocked"),
        TaskState.Uncertain => (NodeState.Interrupted, "Uncertain"),
        TaskState.Refused when task.Problem is { } problem && NodeStates.IsSetup(problem) => (NodeState.NeedsSetup, CardText.ShortReason(problem) ?? "Couldn't start"),
        TaskState.Refused => (NodeState.Failed, "Couldn't start"),
        TaskState.Unsupported => (NodeState.Idle, "Nothing to review"),
        TaskState.SentBack => (NodeState.Failed, "Sent back"),
        _ => throw new UnreachableException(),
    };

    /// <summary>Why the task stands where it does, in a sentence or two, or null when its label says enough.</summary>
    /// <param name="active">The run is approved or stopping. A settled run says nothing more about a task it never started.</param>
    /// <param name="tasks">The run's other tasks, so a waiting task can say which of those it waits for nobody ran.</param>
    public static string? Detail(TaskView task, Func<TaskId, string> title, bool active = true, IReadOnlyDictionary<TaskId, TaskView>? tasks = null) => task.State switch
    {
        TaskState.Pending or TaskState.Ready or TaskState.Unsupported or TaskState.Unrequested when !active => null,
        TaskState.Pending when !task.HeldBy.IsEmpty => Waits(task.HeldBy, title, tasks),
        TaskState.Blocked when task.Block is { } block && block.Task != task.Task => $"The checkout of \"{title(block.Task)}\" holds it. {block.Detail}".Trim(),
        TaskState.Blocked when task.Block is { } block => $"{RecoveryText.Problem(block)} {block.Detail}".Trim(),
        TaskState.Refused when task.Problem is { } problem => RunText.Describe(problem),
        TaskState.Refused when task.Refusal is { } refusal => Problem(refusal),
        TaskState.Uncertain => "Its turn's end was not recorded, so the run never starts it again by itself.",
        TaskState.Failed when task.End is AttemptEnd.Recovered { Reason: var reason } => reason,
        TaskState.Stale => "A task before it handed on a newer result after this one finished.",
        TaskState.SentBack when task.Gate?.Reason is { } reason => $"Sent back: {reason}",
        TaskState.Unsupported => RunText.Describe(new StartProblem.NoSubject()),
        _ => null,
    };

    /// <summary>
    /// What a waiting task waits for: "It starts once "A" and "B" hand on a result.", and for the predecessors nobody ran,
    /// ""B" runs only when you run it."
    /// </summary>
    private static string Waits(IReadOnlyCollection<TaskId> holders, Func<TaskId, string> title, IReadOnlyDictionary<TaskId, TaskView>? tasks)
    {
        var waits = $"It starts once {Names(holders, title)} {(holders.Count == 1 ? "hands" : "hand")} on a result.";
        var unrun = holders.Where(holder => tasks?.GetValueOrDefault(holder) is { State: TaskState.Unrequested }).ToArray();
        return unrun.Length switch
        {
            0 => waits,
            // The last word stays with the one before it, so a narrow inspector leaves no word alone on a line.
            1 => $"{waits} {Names(unrun, title)} runs only when you run\u00A0it.",
            _ => $"{waits} {Names(unrun, title)} run only when you run\u00A0them.",
        };
    }

    /// <summary>
    /// Why a node's Run starts nothing: its dependency predecessors have no results yet. "Runs after "A". Run "A" first."
    /// for one, and the first and a count beyond two. Null when it has none (#90).
    /// </summary>
    public static string? RunsAfter(IReadOnlyCollection<TaskId> predecessors, Func<TaskId, string> title) => predecessors.Count switch
    {
        0 => null,
        1 => $"Runs after {Names(predecessors, title)}. Run {Names(predecessors, title)} first.",
        _ => $"Runs after {Names(predecessors, title)}. Run them first.",
    };

    /// <summary>
    /// Why a run's approved version of a task keeps it from starting although the document's version could: "This run uses
    /// "B" as it was when the run started, and it had no agent then. Run it once the run finishes."
    /// </summary>
    public static string AsApproved(string title, StartProblem problem) => $"This run uses \"{title}\" as it was when the run started, and " + problem switch
    {
        StartProblem.NoAgent => "it had no agent then.",
        StartProblem.FieldMissing missing => $"its {missing.Label} field was empty then.",
        _ => "it could not start as it was then.",
    } + " Run it once the run finishes.";

    /// <summary>What a task of the run needs from the person, which its card's glyph shows, or null.</summary>
    public static Attention? Needs(TaskView task, Func<TaskId, string> title)
    {
        var (state, label) = Of(task, title);
        return task.State switch
        {
            TaskState.Waiting when task.Gate is not null || task.Fix is not null || task.Status == AttemptStatus.WaitingForInput => new Attention.Waiting(label),
            TaskState.Stale => new Attention.Problem($"{label}: {Detail(task, title)}"),
            TaskState.Failed when state == NodeState.Failed => new Attention.Problem(Detail(task, title) is { } why ? $"{label}: {why}" : label),
            TaskState.Blocked or TaskState.Uncertain or TaskState.SentBack or TaskState.Refused when state != NodeState.NeedsSetup =>
                new Attention.Problem(Detail(task, title) is { } detail ? $"{label}: {detail}" : label),
            _ => null,
        };
    }

    /// <summary>
    /// What a stopping run waits for: the person's closure of a turn whose client was never seen to exit, or the
    /// settlement of one whose client exited, which only opening the project again tries once more.
    /// </summary>
    public static string Stopping(IEnumerable<TaskView> tasks, Func<TaskId, string> title) =>
        tasks.FirstOrDefault(task => task.State == TaskState.Uncertain) switch
        {
            null => "Stopping. Finished work stays.",
            { RootExited: true } settling => $"Stopping waits for \"{title(settling.Task)}\" to settle. Open the project again to retry.",
            var uncertain => $"Stopping waits for you to close \"{title(uncertain.Task)}\" as stopped.",
        };

    /// <summary>
    /// Why a run's review waits for Continue fix or Retry fix: closing iDevelop interrupted its fix round, or the round's
    /// turn ended without a recorded end and a person closed it as stopped. Then what each choice can do, with the reason
    /// Continue cannot go on when it cannot.
    /// </summary>
    /// <param name="end">How the fix attempt ended, or null when the run does not show it.</param>
    public static string FixChoice(FixRecovery fix, string subject, AttemptEnd? end)
    {
        var what = end is AttemptEnd.Recovered
            ? $"Fix round {fix.Round} of \"{subject}\" ended without a recorded end, and it was closed as stopped."
            : $"Closing iDevelop interrupted fix round {fix.Round} of \"{subject}\".";
        return fix.ContinueUnavailable is { } why ? $"{what} {why}" : $"{what} Continue the fix in its session, or retry it in a fresh one.";
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

    internal static string Problem(MaterializationProblem problem) => problem switch
    {
        MaterializationProblem.DirtyWorktree => "Files changed in the task's checkout after its turn ended.",
        MaterializationProblem.UncertainOwnership => "A branch moved that no task of the run moved.",
        MaterializationProblem.FanInConflict => "The results it joins conflict.",
        MaterializationProblem.GitVersionUnsupported => "Joining results needs a newer Git.",
        MaterializationProblem.LiveWriter => "Another writer still holds the task's checkout.",
        MaterializationProblem.InputUnavailable => "An input it needs is unavailable.",
        _ => $"{problem}.",
    };

    /// <summary>Up to three quoted titles in full, and the first two and a count beyond that.</summary>
    private static string Listed(string[] titles)
    {
        var names = titles.Select(title => $"\"{title}\"").ToArray();
        return names.Length switch
        {
            1 => names[0],
            2 => $"{names[0]} and {names[1]}",
            3 => $"{names[0]}, {names[1]}, and {names[2]}",
            _ => $"{names[0]}, {names[1]}, and {names.Length - 2} more",
        };
    }

    /// <summary>Quoted titles in title order, as the run bar lists them: one, two, or the first and a count.</summary>
    private static string Names(IEnumerable<TaskId> tasks, Func<TaskId, string> title)
    {
        var names = tasks.Select(task => (Title: title(task), Task: task)).OrderBy(task => task.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(task => task.Task).Select(task => $"\"{task.Title}\"").ToArray();
        return names.Length switch
        {
            1 => names[0],
            2 => $"{names[0]} and {names[1]}",
            _ => $"{names[0]} and {names.Length - 1} more",
        };
    }
}
