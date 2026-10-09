using IDevelop.Desktop.Execution;
using IDevelop.Execution;
using IDevelop.Nodes;
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
        nameof(RunTask), nameof(ShowsRunState), nameof(IsRunOwned), nameof(JoinsRun), nameof(RunOwner), nameof(StatusLabel), nameof(IsWaiting), nameof(Waiting), nameof(StartProblem), nameof(RunRefusal), nameof(LastAttempt),
        nameof(RunStatusLabel), nameof(RunTone), nameof(RunDetail), nameof(RunHasGlyph), nameof(ShowsAgent), nameof(Subtitle), nameof(SendProblem),
        nameof(ShowsFixChoice), nameof(AttendCommand), nameof(AttendHelp),
    ];

    private TaskView? _runTask;
    private TaskView? _shownRun;
    private bool _runActive;
    private AttemptId? _runBaseline;
    private string? _runWorkflow;
    private GateViewModel? _gate;
    private RecoveryViewModel? _recovery;
    private UpdatedInputsViewModel? _updatedInputs;
    private readonly Dictionary<(AttemptId Fix, FixChoice Choice), OperationId> _fixChoices = [];
    private bool _choosingFix;

    /// <summary>The run's view of the task while the canvas's run shows it, or null.</summary>
    internal TaskView? RunTask => _runTask is not null && (_runActive || _attempt?.Id == _runBaseline) ? _runTask : null;

    /// <summary>The card, the inspector, and the conversation show the task as the canvas's run sees it.</summary>
    public bool ShowsRunState => RunTask is not null;

    /// <summary>The canvas's active run owns the task, so its own Run, Cancel, and composer stand aside.</summary>
    public bool IsRunOwned => RunTask is not null && _runActive;

    /// <summary>
    /// The canvas's active run started from another node and has not taken this task in, so its Run adds the task to that
    /// run (#90).
    /// </summary>
    public bool JoinsRun => IsRunOwned && RunTask is { State: TaskState.Unrequested } && _canvas.Run is { View.Phase: RunPhase.Approved };

    /// <summary>Which run owns the task, or ran it last, while the inspector shows the run's state.</summary>
    public string? RunOwner => ShowsRunState ? WorkflowRunText.Unbroken(RunOwnedProblem) : null;

    /// <summary>The task's state in the run, such as "Waits for "A"" or "Waiting for approval".</summary>
    public string? RunStatusLabel => RunTask is { } run ? WorkflowRunText.Of(run, TitleOf, _runActive).Label : null;

    public StatusTone RunTone => RunTask is { } run ? Tone(WorkflowRunText.Of(run, TitleOf, _runActive).State) : StatusTone.Neutral;

    /// <summary>Why the task stands where it does in the run, or null when its status says enough.</summary>
    public string? RunDetail => WorkflowRunText.Unbroken(RunFix is { } fix ? FixDetail(fix)
        : RunTask is { } run ? WorkflowRunText.Detail(run, TitleOf, _runActive, _canvas.Run?.View.Tasks) : null);

    /// <summary>The run's status pill has a glyph, except for a task that has not started, which has nothing to mark.</summary>
    public bool RunHasGlyph => RunTask is { } run && WorkflowRunText.Of(run, TitleOf, _runActive).State != NodeState.Idle;

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

    /// <summary>A block or an unresolved turn of the task in the run, with its evidence and the person's recovery, or null.</summary>
    public RecoveryViewModel? Recovery
    {
        get => _recovery;
        private set
        {
            var old = _recovery;
            if (SetProperty(ref _recovery, value))
            {
                old?.Dispose();
            }
        }
    }

    /// <summary>The task's stale result in the run, with Review updated inputs and Approve rebase, or null.</summary>
    public UpdatedInputsViewModel? UpdatedInputs
    {
        get => _updatedInputs;
        private set
        {
            var old = _updatedInputs;
            if (SetProperty(ref _updatedInputs, value))
            {
                old?.Dispose();
            }
        }
    }

    /// <summary>The run needs the person's choice in this task's inspector rather than in its conversation.</summary>
    private bool NeedsRunPanel => Recovery is not null || UpdatedInputs is not null || RunFix is not null;

    /// <summary>The review's fix round that closing iDevelop interrupted in the canvas's active run, or null.</summary>
    private FixRecovery? RunFix => _runActive ? RunTask?.Fix : null;

    private bool CanChooseRunFix => !_choosingFix && _canvas.Run is { IsControlled: true, IsActive: true };

    private string RunOwnedProblem => (_runActive, HasAgent) switch
    {
        (true, true) when JoinsRun => $"A run of the \"{RunWorkflowName}\" workflow is active. Run this task to add it to that run.",
        (true, true) => $"A run of the \"{RunWorkflowName}\" workflow owns this task. Talk to it through its conversation, or stop the run.",
        (true, false) => $"A run of the \"{RunWorkflowName}\" workflow owns this approval. " + (_runTask?.Gate?.Status switch
        {
            GateStatus.Waiting => "Answer its request below, or stop the run.",
            GateStatus.Approved => "You approved its request.",
            GateStatus.SentBack => "You sent its request back. It asks again once its inputs change.",
            _ => "It asks for your approval once the tasks before it hand on.",
        }),
        (false, true) when RunTask is { State: TaskState.Pending or TaskState.Ready or TaskState.Unrequested } =>
            $"The last run of the \"{RunWorkflowName}\" workflow did not start this task.",
        (false, true) => $"A run of the \"{RunWorkflowName}\" workflow ran this task last.",
        (false, false) => $"A run of the \"{RunWorkflowName}\" workflow asked for this approval last.",
    };

    private string RunWorkflowName => _runWorkflow ?? Workflow.UnnamedName;

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
        ShowPanels();
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
        _continueFix.NotifyCanExecuteChanged();
        _retryFix.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// The Recovery and Updated inputs sections follow the task's state while the run is active. A shown section stays
    /// while the task settles, as it does while the section's own command or a reconciliation runs, so what the person
    /// typed and the command's outcome stay with it.
    /// </summary>
    private void ShowPanels()
    {
        var run = _canvas.Run;
        var task = _runActive ? RunTask : null;
        var settling = task is { State: TaskState.Settling };
        if (_recovery is null || !settling)
        {
            if (run is null || task is not { State: TaskState.Blocked or TaskState.Uncertain })
            {
                Recovery = null;
            }
            else if (_recovery is { } shown && shown.Shows(run, task))
            {
                shown.Show(task);
            }
            else
            {
                Recovery = new RecoveryViewModel(run, task, TitleOf, Show);
            }
        }

        if (_updatedInputs is null || !settling)
        {
            if (run is null || task is not { State: TaskState.Stale })
            {
                UpdatedInputs = null;
            }
            else if (_updatedInputs is not { } current || !current.Shows(run, task))
            {
                UpdatedInputs = new UpdatedInputsViewModel(run, task.Task, TitleOf, task.CarriesCode);
            }
        }

        OnPropertyChanged(nameof(AttendCommand));
        OnPropertyChanged(nameof(AttendHelp));
    }

    /// <summary>How the review's fix round ended, by a closed app or a person's closure after a crash, and what each choice can do.</summary>
    private string FixDetail(FixRecovery fix)
    {
        var subject = _canvas.Workflow.SubjectOf(Id);
        var end = subject is { } owner && _canvas.Run?.View.Tasks.GetValueOrDefault(owner) is { } view && view.Attempt == fix.Fix ? view.End : null;
        return WorkflowRunText.FixChoice(fix, subject is { } id ? TitleOf(id) : "the task", end);
    }

    /// <summary>Selects another task of the canvas, such as the one whose checkout a block is on.</summary>
    private void Show(TaskId task)
    {
        if (_canvas.Nodes.FirstOrDefault(node => node.Id == task) is { } node)
        {
            _canvas.Inspect(node);
        }
    }

    /// <summary>
    /// Continue fix or Retry fix of the run's review. One confirmation per round and choice, so a repeat or a retry after a
    /// busy refusal reserves one attempt; the run's next decision shows it.
    /// </summary>
    private async Task ChooseRunFixAsync(FixChoice choice)
    {
        if (RunFix is not { } fix || _canvas.Run is not { } run)
        {
            return;
        }

        var key = (fix.Fix, choice);
        var confirmation = _fixChoices.TryGetValue(key, out var known) ? known : _fixChoices[key] = new OperationId(Guid.NewGuid());
        var (coordinator, address) = (run.Coordinator, run.Address);
        _choosingFix = true;
        _continueFix.NotifyCanExecuteChanged();
        _retryFix.NotifyCanExecuteChanged();
        FixReply reply;
        try
        {
            reply = await WorkflowRunViewModel.Retrying(
                () => choice == FixChoice.Continue ? coordinator.ContinueFix(address, Id, confirmation) : coordinator.RetryFix(address, Id, confirmation),
                outcome => outcome is FixReply.Refused refused && WorkflowRunText.Transient(refused.Reason));
        }
        finally
        {
            _choosingFix = false;
            _continueFix.NotifyCanExecuteChanged();
            _retryFix.NotifyCanExecuteChanged();
        }

        _canvas.Notice(reply switch
        {
            FixReply.Blocked blocked => $"{blocked.Block.Detail} The fix did not start.",
            FixReply.Refused refused => RecoveryText.Problem(refused.Reason),
            FixReply.Unavailable unavailable => unavailable.Message,
            _ => null,
        });
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
        (run.Gate is { Status: GateStatus.Waiting } || run.Fix is not null || run.Status == AttemptStatus.WaitingForInput);

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
