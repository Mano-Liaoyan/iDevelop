using System.Windows.Input;
using IDevelop.Desktop.Execution;
using IDevelop.Desktop.Mvvm;
using IDevelop.Execution;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// Run Workflow: the preflight sheet, and the run that the toolbar, the cards, and the inspector follow. The canvas keeps
/// its run while the project stays open, so switching to another workflow or project stops nothing.
/// </summary>
public sealed partial class WorkflowCanvasViewModel
{
    private WorkflowRunViewModel? _run;
    private RunPreflightViewModel? _preflight;
    private RelayCommand? _runWorkflow;
    private RelayCommand? _dismissRun;
    private bool _standaloneRunning;

    /// <summary>The workflow's run in this window: the one it started, or the active one it found when the project opened.</summary>
    public WorkflowRunViewModel? Run
    {
        get => _run;
        private set => SetProperty(ref _run, value);
    }

    /// <summary>The open preflight sheet, or null.</summary>
    public RunPreflightViewModel? Preflight
    {
        get => _preflight;
        private set => SetProperty(ref _preflight, value);
    }

    /// <summary>Opens the preflight of the whole workflow. No node needs to be selected.</summary>
    public ICommand RunWorkflowCommand => _runWorkflow ??= new RelayCommand(OpenPreflight, () => Preflight is null && Sheet is null);

    /// <summary>Hides a run that has settled, so the cards show their tasks' own runs again. The run's records stay.</summary>
    public ICommand DismissRunCommand => _dismissRun ??= new RelayCommand(DismissRun);

    /// <summary>Run Workflow shows until a run of this workflow is active; then the run's own controls take its place.</summary>
    public bool ShowsRunWorkflow => Run is not { IsActive: true };

    /// <summary>Raised when the coordinator that the canvas's run goes through changes, so an open conversation can follow it.</summary>
    internal event EventHandler? RunRouteChanged;

    /// <summary>Shows <paramref name="coordinator"/>'s run. A run the canvas already shows stays as it is.</summary>
    internal void Adopt(WorkflowRunCoordinator coordinator)
    {
        if (Run is { } shown && (shown.Coordinator == coordinator || shown.Address == coordinator.Address && shown.IsControlled && !coordinator.Controlled))
        {
            return;
        }

        if (Run is { } old)
        {
            old.ViewChanged -= OnRunViewChanged;
            old.Replaced -= OnRunReplaced;
            old.Dispose();
        }

        var run = new WorkflowRunViewModel(Runs, coordinator, task => Workflow.Tasks.GetValueOrDefault(task)?.Title ?? "a removed task");
        run.ViewChanged += OnRunViewChanged;
        run.Replaced += OnRunReplaced;
        Run = run;
        ShowRun(adopted: true);
        ReadSettledRun();
        RunRouteChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void ClosePreflight()
    {
        Preflight?.Detach();
        Preflight = null;
        _runWorkflow?.NotifyCanExecuteChanged();
    }

    /// <summary>Gives every card its task's view in the run, and the sidebar the run's activity.</summary>
    internal void ShowRun(bool adopted = false)
    {
        var run = Run;
        foreach (var node in Nodes)
        {
            node.ShowRunTask(run?.View.Tasks.GetValueOrDefault(node.Id), run?.IsActive ?? false, adopted, Name);
        }

        ShowActivity();
        OnPropertyChanged(nameof(ShowsRunWorkflow));
    }

    /// <summary>Stops the canvas's run follow its coordinator, as the project does when it closes.</summary>
    internal void DisposeRun()
    {
        ClosePreflight();
        if (Run is { } run)
        {
            run.ViewChanged -= OnRunViewChanged;
            run.Replaced -= OnRunReplaced;
            run.Dispose();
        }
    }

    private void DismissRun()
    {
        if (Run is not { IsActive: false } run)
        {
            return;
        }

        run.ViewChanged -= OnRunViewChanged;
        run.Replaced -= OnRunReplaced;
        run.Dispose();
        Run = null;
        ShowRun();
        RunRouteChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OpenPreflight()
    {
        Preflight ??= new RunPreflightViewModel(this);
        _runWorkflow?.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// A node's Run (#90). It runs the node in a workflow run, as Run Workflow runs every root: without an active run it
    /// opens the node's preflight, and in the canvas's active run it adds the node, which starts once each task before it
    /// has a result. Each task after it starts by itself once all of its own predecessors have results. A node whose
    /// predecessors have no results, or that could not start as configured, starts nothing, and the notice says why.
    /// </summary>
    internal void RunNode(TaskNodeViewModel node)
    {
        if (RunsAfter(node.Id) is { } after)
        {
            Notice(after);
            return;
        }

        if (Runs.CheckRun(node.Definition) is { } problem)
        {
            Notice(RunText.Describe(problem));
            return;
        }

        if (Run is { IsActive: true } run)
        {
            if (run.View.Tasks.GetValueOrDefault(node.Id) is { State: TaskState.Unrequested })
            {
                _ = JoinRunAsync(run, node);
            }

            return;
        }

        if (Preflight is null && Sheet is null)
        {
            Preflight = new RunPreflightViewModel(this, node.Id);
            _runWorkflow?.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// "Runs after "A". Run "A" first." for a node whose dependency predecessors have no results yet, or null. In the
    /// canvas's active run that means a result in the run, and otherwise in a new run (#90). Either way a predecessor whose
    /// earlier run left it a result that still counts is complete, because the run carries that result.
    /// </summary>
    internal string? RunsAfter(TaskId task)
    {
        IReadOnlyCollection<TaskId> waiting = Run is { IsActive: true } run && run.View.Tasks.GetValueOrDefault(task) is { } view
            ? view.State == TaskState.Unrequested ? view.HeldBy : []
            : [.. Workflow.Connections.Where(connection => connection.Key.To == task && connection.Value.Blocks()).Select(connection => connection.Key.From).Order()];
        return WorkflowRunText.RunsAfter([.. waiting.Where(predecessor => History?.CurrentOf(predecessor) is null)],
            id => Workflow.Tasks.GetValueOrDefault(id)?.Title ?? "a removed task");
    }

    /// <summary>Adds the node to the active run, once per run, and says why when the run refuses.</summary>
    private async Task JoinRunAsync(WorkflowRunViewModel run, TaskNodeViewModel node)
    {
        var outcome = await run.JoinAsync(node.Id);
        Notice(outcome switch
        {
            RunCommand.Unavailable unavailable => unavailable.Message,
            RunCommand.Refused { Reason: { Problem: RunProblem.MissingDependencyResult, Task: { } missing } } =>
                WorkflowRunText.RunsAfter([missing], id => Workflow.Tasks.GetValueOrDefault(id)?.Title ?? "a removed task"),
            RunCommand.Refused refused => WorkflowRunText.Problem(refused.Reason),
            _ => null,
        });
    }

    private void OnRunViewChanged(object? sender, EventArgs e)
    {
        ShowRun();
        ReadSettledRun();
    }

    private void OnRunReplaced(object? sender, EventArgs e)
    {
        ShowRun();
        RunRouteChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The workflow row's running dot follows this window's own runs of its tasks and its workflow run's clients.</summary>
    private void ShowActivity()
    {
        IsRunning = _standaloneRunning || Run is { HasActivity: true };
        OnWaitingChanged();
    }
}
