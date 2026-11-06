namespace IDevelop.Desktop.Theme;

/// <summary>How many of a workflow's nodes are in each state.</summary>
public readonly record struct StateCounts(int Completed, int Running, int Waiting, int Failed, int Stale, int Total)
{
    public bool HasRun => Completed + Running + Waiting + Failed + Stale > 0;

    /// <summary>
    /// "5 of 9 · 1 running · 3 waiting · 1 failed · 2 stale", with the counts that are not zero, or "9 nodes" before any node ran,
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
            Failed > 0 ? $"{Failed} failed" : null,
            Stale > 0 ? $"{Stale} stale" : null,
        }.OfType<string>());
    }
}

