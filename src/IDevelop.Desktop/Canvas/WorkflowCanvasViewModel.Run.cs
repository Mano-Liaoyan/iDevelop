using System.Windows.Input;
using IDevelop.Desktop.Execution;
using IDevelop.Desktop.Mvvm;
using IDevelop.Execution;

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

    private void OnRunViewChanged(object? sender, EventArgs e) => ShowRun();

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
