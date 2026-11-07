namespace IDevelop.Desktop.Theme;

/// <summary>How many of a workflow's nodes are in each state.</summary>
/// <remarks>
/// Every surface reads these one definitions. Waiting counts the nodes that need the person's word: a reply, a question, an
/// approval, a conflict, or a round limit. Interrupted counts the nodes that iDevelop's closing stopped, which wait for nobody's word.
/// </remarks>
public readonly record struct StateCounts(int Completed, int Running, int Waiting, int Interrupted, int Failed, int Stale, int Total)
{
    public bool HasRun => Completed + Running + Waiting + Interrupted + Failed + Stale > 0;

    /// <summary>
    /// "5 of 9 · 1 running · 3 waiting · 1 interrupted · 1 failed · 2 stale", with the counts that are not zero, or "9 nodes" before any node ran,
    /// or "No nodes yet".
    /// </summary>
    public string Progress => !HasRun ? Summary() : $"{Completed} of {Total}";

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
            Waiting > 0 ? $"{Waiting} waiting" : null,
            Interrupted > 0 ? $"{Interrupted} interrupted" : null,
            Failed > 0 ? $"{Failed} failed" : null,
            Stale > 0 ? $"{Stale} stale" : null,
        }.OfType<string>());
    }
}

