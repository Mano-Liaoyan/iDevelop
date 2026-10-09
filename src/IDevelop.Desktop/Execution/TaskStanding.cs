using System.Collections.Immutable;
using IDevelop.Desktop.Canvas;
using IDevelop.Execution;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Execution;

/// <summary>
/// Where a task stands between runs, from the runs of its workflow that settled (#90): its result still counts, it is out
/// of date, or it waits for tasks before it that have none. Its card keeps it after the run and after a restart.
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

        // A task no run could start yet waits for nothing: it shows as it always did.
        var holders = history.WaitsFor(task);
        return holders.IsEmpty ? null : new Waits(holders);
    }

    /// <summary>The state its card's ring and glyph show, and its subtitle.</summary>
    public (NodeState State, string Label) Card(Func<TaskId, string> title) => this switch
    {
        Succeeded => (NodeState.Succeeded, "Succeeded"),
        OutOfDate => (NodeState.OutOfDate, "Out of date"),
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
        _ => null,
    };

    /// <summary>
    /// Run's refusal when this task is out of date because of <paramref name="holder"/>, the one task it runs after that has
    /// no current result: one sentence, as in "Out of date because "B" changed. Run "B" first." when B is out of date too,
    /// or "Out of date because a later run of "B" ended without a result. Run "B" first." when B's newest run left it none.
    /// Null otherwise.
    /// </summary>
    public string? RunsAfter(TaskId holder, TaskHistory? holderHistory, Func<TaskId, string> title) => (this, holderHistory) switch
    {
        (OutOfDate { Input: { } input }, TaskHistory.OutOfDate) when input == holder =>
            $"Out of date because \"{title(holder)}\" changed. Run \"{title(holder)}\"\u00A0first.",
        (OutOfDate { Input: { } input, Reason: OutOfDateReason.InputReplaced }, TaskHistory.None) when input == holder =>
            $"Out of date because a later run of \"{title(holder)}\" ended without a result. Run \"{title(holder)}\"\u00A0first.",
        _ => null,
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
