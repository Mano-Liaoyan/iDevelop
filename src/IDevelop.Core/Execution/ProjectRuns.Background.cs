using System.Collections.Immutable;

namespace IDevelop.Execution;

public sealed partial class ProjectRuns
{
    /// <summary>A workflow turn's client now does this work in the background, which the turn waits for (#113).</summary>
    internal event Action<ExecutionAddress, ImmutableArray<BackgroundTask>>? TurnBackgroundChanged;

    private void NotifyBackground(ExecutionAddress address, ImmutableArray<BackgroundTask> tasks) => TurnBackgroundChanged?.Invoke(address, tasks);
}
