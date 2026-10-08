using IDevelop.Desktop.Execution;
using IDevelop.Execution;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// The node as a task of the canvas's workflow run. While the run is approved or stopping it owns the task, so the card,
/// the inspector, and the conversation show the run's view of it. Once the run settles they keep showing it until the
/// task runs on its own again.
/// </summary>
public sealed partial class TaskNodeViewModel
{
    private static readonly string[] RunDependents =
    [
        nameof(RunTask), nameof(ShowsRunState), nameof(IsRunOwned), nameof(RunOwner), nameof(StatusLabel), nameof(IsWaiting), nameof(Waiting), nameof(StartProblem), nameof(LastAttempt),
        nameof(RunStatusLabel), nameof(RunTone), nameof(RunDetail), nameof(ShowsAgent), nameof(Subtitle), nameof(SendProblem),
    ];

    private TaskView? _runTask;
    private TaskView? _shownRun;
    private bool _runActive;
    private AttemptId? _runBaseline;
    private string? _runWorkflow;
    private GateViewModel? _gate;

    /// <summary>The run's view of the task while the canvas's run shows it, or null.</summary>
    internal TaskView? RunTask => _runTask is not null && (_runActive || _attempt?.Id == _runBaseline) ? _runTask : null;

    /// <summary>The card, the inspector, and the conversation show the task as the canvas's run sees it.</summary>
    public bool ShowsRunState => RunTask is not null;

    /// <summary>The canvas's active run owns the task, so its own Run, Cancel, and composer stand aside.</summary>
    public bool IsRunOwned => RunTask is not null && _runActive;

    /// <summary>Which run owns the task, or ran it last, while the inspector shows the run's state.</summary>
    public string? RunOwner => ShowsRunState ? RunOwnedProblem : null;

    /// <summary>The task's state in the run, such as "Waits for "A"" or "Waiting for approval".</summary>
    public string? RunStatusLabel => RunTask is { } run ? WorkflowRunText.Of(run, TitleOf, _runActive).Label : null;

    public StatusTone RunTone => RunTask is { } run ? Tone(WorkflowRunText.Of(run, TitleOf, _runActive).State) : StatusTone.Neutral;

    /// <summary>Why the task stands where it does in the run, or null when its status says enough.</summary>
    public string? RunDetail => RunTask is { } run ? WorkflowRunText.Detail(run, TitleOf) : null;

    /// <summary>An Approval node's request in the run, with Approve and Send back, or null.</summary>
    public GateViewModel? Gate
    {
        get => _gate;
        private set
        {
            var old = _gate;
            if (SetProperty(ref _gate, value))
            {
                old?.Dispose();
            }
        }
    }

    private string RunOwnedProblem => _runActive
        ? $"A run of the \"{_runWorkflow ?? Workflow.UnnamedName}\" workflow owns this task. Talk to it through its conversation, or stop the run."
        : $"A run of the \"{_runWorkflow ?? Workflow.UnnamedName}\" workflow ran this task last. Run it on its own to show its own result again.";

    /// <summary>
    /// Called on the UI thread with the task's view in the canvas's run, or null when the run does not hold it. A new run
    /// remembers the task's own latest attempt, so a later attempt of its own, after the run settled, shows again.
    /// </summary>
    /// <param name="active">The run is approved or stopping.</param>
    /// <param name="adopted">The canvas shows this run for the first time.</param>
    internal void ShowRunTask(TaskView? task, bool active, bool adopted, string workflow)
    {
        if (adopted)
        {
            _runBaseline = _attempt?.Id;
        }

        var wasActive = _runActive;
        _runTask = task;
        _runActive = active;
        _runWorkflow = workflow;
        if (Equals(_shownRun, RunTask) && wasActive == active)
        {
            return;
        }

        OnRunChanged();
    }

    /// <summary>The task's own latest attempt changed, which ends the overlay of a settled run once the task ran on its own.</summary>
    partial void InitializeRun()
    {
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LastAttempt) && !Equals(_shownRun, RunTask))
            {
                OnRunChanged();
            }
        };
    }

    private void OnRunChanged()
    {
        _shownRun = RunTask;
        ShowGate();
        foreach (var property in RunDependents)
        {
            OnPropertyChanged(property);
        }

        ShowState();
        _run.NotifyCanExecuteChanged();
        _cancel.NotifyCanExecuteChanged();
        _markDone.NotifyCanExecuteChanged();
        _send.NotifyCanExecuteChanged();
        _stopAndSend.NotifyCanExecuteChanged();
        _openInTerminal.NotifyCanExecuteChanged();
    }

    private void ShowGate()
    {
        if (RunTask is not { Gate: { } gate } || _canvas.Run is not { } run)
        {
            Gate = null;
            return;
        }

        if (_gate is { } shown && shown.Shows(run, gate.Request.Id))
        {
            shown.Show(gate);
            return;
        }

        Gate = new GateViewModel(run, gate);
    }

    private static bool WaitsInRun(TaskView run) => run.State == TaskState.Waiting &&
        (run.Gate is { Status: GateStatus.Waiting } || run.Status == AttemptStatus.WaitingForInput);

    /// <summary>A task of the run by the title the workflow gives it now.</summary>
    private string TitleOf(TaskId task) => _canvas.Workflow.Tasks.GetValueOrDefault(task)?.Title ?? "a removed task";

    private static StatusTone Tone(NodeState state) => state switch
    {
        NodeState.Running or NodeState.RunningElsewhere or NodeState.Stopping or NodeState.InReview => StatusTone.Running,
        NodeState.Succeeded => StatusTone.Complete,
        NodeState.Failed or NodeState.Interrupted or NodeState.NeedsSetup => StatusTone.Problem,
        NodeState.Waiting => StatusTone.Waiting,
        NodeState.Idle or NodeState.Cancelled => StatusTone.Neutral,
        _ => StatusTone.Neutral,
    };
}
