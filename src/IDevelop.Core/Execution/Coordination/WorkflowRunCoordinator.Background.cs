using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

internal sealed partial class WorkflowRunCoordinator
{
    // Owned by the loop: the background work each running turn's client reported last, by the turn's launch.
    private readonly Dictionary<TaskId, (LaunchKey Launch, ImmutableArray<BackgroundTask> Tasks)> _background = [];

    private void OnTurnBackground(ExecutionAddress address, ImmutableArray<BackgroundTask> tasks)
    {
        if (address.Workflow != Address.Workflow || address.Run != Address.Run) return;
        Post(() => _background[address.Task] = (address.Launch, tasks));
    }

    /// <summary>Names the background work a running task's turn waits for, as its card shows it.</summary>
    private RunView WithBackground(RunView view)
    {
        var tasks = view.Tasks;
        foreach (var (task, (launch, background)) in _background)
        {
            if (background.IsEmpty || _live.GetValueOrDefault(task) is not { Stage: LiveStage.Running, Turn: { } turn } || turn.Address.Launch != launch ||
                tasks.GetValueOrDefault(task) is not { State: TaskState.Running } shown)
                continue;
            tasks = tasks.SetItem(task, shown with { Background = background });
        }
        return view with { Tasks = tasks };
    }
}
