using IDevelop.Desktop.Canvas;

namespace IDevelop.Desktop.Theme;

/// <summary>How many of a workflow's nodes are in each state.</summary>
/// <remarks>
/// Every surface reads these one definitions. Waiting counts every node that needs the person, for any reason.
/// </remarks>
public readonly record struct StateCounts(int Completed, int Running, int Waiting, int Failed, int Stale, int Total)
{
    public bool HasRun => Completed + Running + Waiting + Failed + Stale > 0;

    /// <summary>The counts of these nodes.</summary>
    public static StateCounts Of(IReadOnlyCollection<TaskNodeViewModel> nodes) => new(
        nodes.Count(node => node.State == NodeState.Completed), nodes.Count(node => node.State == NodeState.Running), nodes.Count(node => node.IsWaiting),
        nodes.Count(node => node.State == NodeState.Failed), nodes.Count(node => node.State == NodeState.Stale), nodes.Count);

    public static StateCounts operator +(StateCounts a, StateCounts b) => new(
        a.Completed + b.Completed, a.Running + b.Running, a.Waiting + b.Waiting, a.Failed + b.Failed, a.Stale + b.Stale, a.Total + b.Total);

    /// <summary>The one mark of a group: Needs you, Failed, Running, Stale, then Done when all are done, and otherwise none.</summary>
    public NodeState? Rollup =>
        Waiting > 0 ? NodeState.InputRequired
        : Failed > 0 ? NodeState.Failed
        : Running > 0 ? NodeState.Running
        : Stale > 0 ? NodeState.Stale
        : Total > 0 && Completed == Total ? NodeState.Completed
        : null;

    /// <summary>
    /// "5 of 9 · 1 running · 3 need you · 1 failed · 2 stale", with the counts that are not zero, or "9 nodes" before any node ran,
    /// or "No nodes yet".
    /// </summary>
    public string Progress => !HasRun ? Summary() : $"{Completed} of {Total}";

    /// <summary>"1 needs you" or "3 need you".</summary>
    public static string NeedYou(int count) => count == 1 ? "1 needs you" : $"{count} need you";

    /// <summary>A line of several parts with a no-break space in each part of " · ", so a wrap never parts a count from its word.</summary>
    public static string KeepCountsWhole(string line) =>
        line.Contains(" · ") ? string.Join(" · ", line.Split(" · ").Select(part => part.Replace(' ', '\u00a0'))) : line;

    public string Summary()
    {
        if (Total == 0)
        {
            return "No nodes yet";
        }

        if (!HasRun)
        {
            return Total == 1 ? "1 node" : $"{Total} nodes";
        }

        return string.Join(" · ", new[]
        {
            $"{Completed} of {Total}",
            Running > 0 ? $"{Running} running" : null,
            Waiting > 0 ? NeedYou(Waiting) : null,
            Failed > 0 ? $"{Failed} failed" : null,
            Stale > 0 ? $"{Stale} stale" : null,
        }.OfType<string>());
    }
}

