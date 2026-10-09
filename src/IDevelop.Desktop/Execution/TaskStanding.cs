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
    internal sealed record OutOfDate(OutOfDateReason Reason, TaskId? Input, DateTimeOffset At) : TaskStanding;

    /// <summary>
    /// It has no result, and its newest run that could start it did not, because <paramref name="Holders"/>, dependency
    /// predecessors of it, had no result there and have none since. Running them starts it once the last one finishes.
    /// </summary>
    /// <param name="Latest">The run that could start it and did not is the workflow's newest settled run.</param>
    internal sealed record Waits(ImmutableSortedSet<TaskId> Holders, bool Latest) : TaskStanding
    {
        public bool Equals(Waits? other) => other is not null && Holders.SequenceEqual(other.Holders) && Latest == other.Latest;

        public override int GetHashCode() => Holders.Count;
    }

    /// <summary>What <paramref name="history"/> says of <paramref name="task"/> in <paramref name="workflow"/>, or null when it says nothing.</summary>
    public static TaskStanding? Of(RunHistory history, Workflow workflow, TaskId task)
    {
        switch (history[task])
        {
            case TaskHistory.Current current: return new Succeeded(current.Result.At);
            case TaskHistory.OutOfDate outOfDate: return new OutOfDate(outOfDate.Reason, outOfDate.Input, outOfDate.Result.At);
        }

        // A task no run could start yet waits for nothing: it shows as it always did.
        var (holders, latest) = history.WaitsFor(task);
        return holders.IsEmpty ? null : new Waits(holders, latest);
    }

    /// <summary>The state its card's ring and glyph show, and its subtitle.</summary>
    public (NodeState State, string Label) Card(Func<TaskId, string> title) => this switch
    {
        Succeeded => (NodeState.Succeeded, "Succeeded"),
        OutOfDate => (NodeState.Interrupted, "Out of date"),
        Waits waits => (NodeState.Idle, waits.Holders.Count == 1 ? $"Waits for \"{title(waits.Holders.Min)}\"" : $"Waits for {waits.Holders.Count} tasks"),
        _ => throw new InvalidOperationException(),
    };

    /// <summary>Which run left it where it stands, for the inspector.</summary>
    public string Owner(string workflow) => this switch
    {
        Succeeded => $"Its result from the last run of the \"{workflow}\" workflow still counts.",
        OutOfDate => $"Its result from the last run of the \"{workflow}\" workflow no longer counts.",
        Waits { Latest: true } => $"The last run of the \"{workflow}\" workflow did not start this task.",
        Waits => $"The last run of the \"{workflow}\" workflow that could start this task did not start it.",
        _ => throw new InvalidOperationException(),
    };

    /// <summary>Why it stands there, in a sentence or two, or null when its label says enough.</summary>
    public string? Detail(Func<TaskId, string> title) => this switch
    {
        Succeeded => "When you run a task after it, the run uses this result instead of running it again.",
        OutOfDate { Reason: OutOfDateReason.Changed } => "It changed since it ran, or so did the connections into it. Run it again to bring it up to date.",
        OutOfDate { Reason: OutOfDateReason.InputReplaced, Input: { } input } =>
            $"\"{title(input)}\" has a newer result than the one it used. Run it again to bring it up to date.",
        OutOfDate { Reason: OutOfDateReason.InputOutOfDate, Input: { } input } =>
            $"The result of \"{title(input)}\" that it used is out of date. Run it again to bring it up to date.",
        OutOfDate => "A task before it has a newer result than the one it used. Run it again to bring it up to date.",
        Waits waits => $"It starts once {Names(waits.Holders, title)} {(waits.Holders.Count == 1 ? "hands" : "hand")} on a result. " +
            $"{Names(waits.Holders, title)} {(waits.Holders.Count == 1 ? "runs" : "run")} only when you run {(waits.Holders.Count == 1 ? "it" : "them")}.",
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
