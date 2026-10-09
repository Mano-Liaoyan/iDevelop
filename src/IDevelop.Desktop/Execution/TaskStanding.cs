using System.Collections.Immutable;
using IDevelop.Desktop.Canvas;
using IDevelop.Execution;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Execution;

/// <summary>
/// Where a task stands between runs, from the runs of its workflow that settled (#90): its result still counts, it is out
/// of date, its newest run ended it without a result, or it waits for tasks before it that have none. Its card keeps it
/// after the run and after a restart.
/// </summary>
internal abstract record TaskStanding
{
    private TaskStanding() { }

    /// <summary>Its newest run left it a result that still counts, so a node's Run of a task after it uses it.</summary>
    /// <param name="At">When that run recorded the result.</param>
    internal sealed record Succeeded(DateTimeOffset At) : TaskStanding;

    /// <summary>Its newest result no longer counts, for <paramref name="Reason"/>.</summary>
    internal sealed record OutOfDate(OutOfDateReason Reason, TaskId? Input, DateTimeOffset At) : TaskStanding
    {
        /// <summary>The task it took a result from has a current result itself, so this task can run again at once.</summary>
        public bool InputComplete { get; init; }
    }

    /// <summary>
    /// Its newest run that ran it ended without a result, as <paramref name="Task"/>, its view in that run, shows: failed,
    /// cancelled, closed when the run stopped, or blocked. The card and the inspector show it as that run showed it.
    /// </summary>
    /// <param name="At">When that run settled.</param>
    internal sealed record Ended(TaskView Task, DateTimeOffset At) : TaskStanding;

    /// <summary>
    /// It has no result, and its newest run that could start it did not, because <paramref name="Holders"/>, dependency
    /// predecessors of it, had no result there and have none since. Running them starts it once the last one finishes.
    /// </summary>
    internal sealed record Waits(ImmutableSortedSet<TaskId> Holders) : TaskStanding
    {
        public bool Equals(Waits? other) => other is not null && Holders.SequenceEqual(other.Holders);

        public override int GetHashCode() => Holders.Count;
    }

    /// <summary>What <paramref name="history"/> says of <paramref name="task"/> in <paramref name="workflow"/>, or null when it says nothing.</summary>
    public static TaskStanding? Of(RunHistory history, Workflow workflow, TaskId task)
    {
        switch (history[task])
        {
            case TaskHistory.Current current: return new Succeeded(current.Result.At);
            case TaskHistory.OutOfDate outOfDate:
                return new OutOfDate(outOfDate.Reason, outOfDate.Input, outOfDate.Result.At)
                {
                    InputComplete = outOfDate.Input is { } input && history.CurrentOf(input) is not null,
                };
        }

        if (history.Ended(task) is { } ended) return new Ended(ended.Task, ended.At);

        // A task no run could start yet waits for nothing: it shows as it always did.
        var holders = history.WaitsFor(task);
        return holders.IsEmpty ? null : new Waits(holders);
    }

    /// <summary>The state its card's ring and glyph show, and its subtitle.</summary>
    public (NodeState State, string Label) Card(Func<TaskId, string> title) => this switch
    {
        Succeeded => (NodeState.Succeeded, "Succeeded"),
        OutOfDate => (NodeState.OutOfDate, "Out of date"),
        Ended ended => WorkflowRunText.Of(ended.Task, title, active: false),
        Waits waits => (NodeState.Idle, waits.Holders.Count == 1 ? $"Waits for \"{title(waits.Holders.Min)}\"" : $"Waits for {waits.Holders.Count} tasks"),
        _ => throw new InvalidOperationException(),
    };

    /// <summary>
    /// Why it stands there, in one sentence, or null when its label and the line under Run say enough: a task that waits,
    /// or one whose input has no current result, which Run's refusal names. The last words stay together, so no line ends
    /// with a single word.
    /// </summary>
    public string? Detail(Func<TaskId, string> title) => this switch
    {
        Succeeded => "Its result from an earlier run still\u00A0counts.",
        OutOfDate { Reason: OutOfDateReason.Changed } => "Out of date because it changed since it\u00A0ran.",
        OutOfDate { Input: { } input, InputComplete: true } => $"Out of date because \"{title(input)}\" has a newer\u00A0result.",
        OutOfDate { Input: not null } => null,
        OutOfDate => "Out of date because a task before it has a newer\u00A0result.",
        Ended ended => WorkflowRunText.Detail(ended.Task, title, active: false),
        _ => null,
    };

    /// <summary>
    /// Run's refusal when this task is out of date because of <paramref name="holder"/>, the one task it runs after that has
    /// no current result: one sentence, as in "Out of date because "B" changed. Run "B" first." when B changed itself,
    /// "Out of date because "B" is out of date. Run "B" first." when B is out of date for a task before it,
    /// or "Out of date because a later run of "B" ended without a result. Run "B" first." when B's newest run left it none.
    /// Null otherwise.
    /// </summary>
    public string? RunsAfter(TaskId holder, TaskHistory? holderHistory, Func<TaskId, string> title) => (this, holderHistory) switch
    {
        (OutOfDate { Input: { } input }, TaskHistory.OutOfDate { Reason: OutOfDateReason.Changed }) when input == holder =>
            $"Out of date because \"{title(holder)}\" changed. Run \"{title(holder)}\"\u00A0first.",
        // The task before it did not change itself: a task before that one did, or has another result now.
        (OutOfDate { Input: { } input }, TaskHistory.OutOfDate) when input == holder =>
            $"Out of date because \"{title(holder)}\" is out of date. Run \"{title(holder)}\"\u00A0first.",
        (OutOfDate { Input: { } input, Reason: OutOfDateReason.InputReplaced }, TaskHistory.None) when input == holder =>
            $"Out of date because a later run of \"{title(holder)}\" ended without a result. Run \"{title(holder)}\"\u00A0first.",
        _ => null,
    };

    /// <summary>
    /// Which run left it where it stands, for one whose newest run ended it without a result, as that run's line said:
    /// "A run of the "W" workflow ran this task last." Null otherwise, since its status and detail say it.
    /// </summary>
    public string? Owner(string workflow, bool hasAgent) => this is Ended
        ? hasAgent ? $"A run of the \"{workflow}\" workflow ran this task last." : $"A run of the \"{workflow}\" workflow asked for this approval last."
        : null;

    /// <summary>What its card's tooltip adds, which the card may only count: the tasks it waits for, by name.</summary>
    public string? Tip(Func<TaskId, string> title) => this is Waits waits ? $"Waits for {Names(waits.Holders, title)}." : null;

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
