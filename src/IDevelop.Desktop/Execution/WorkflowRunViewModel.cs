using System.Windows.Input;
using Avalonia.Threading;
using IDevelop.Desktop.Mvvm;
using IDevelop.Execution;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Execution;

/// <summary>
/// A workflow run as the window shows it: its status on the toolbar, Stop Workflow, and Resume. It follows the run's
/// coordinator on the UI thread. While another window controls the run, it rereads the run's journal and asks for control
/// again on a timer, and once the other window lets go it follows the new coordinator, which controls the run.
/// </summary>
public sealed class WorkflowRunViewModel : ObservableObject, IDisposable
{
    private readonly ProjectRuns _runs;
    private readonly Func<TaskId, string> _title;
    private readonly RelayCommand _stop;
    private readonly RelayCommand _resume;
    private readonly OperationId _stopCommand = new(Guid.NewGuid());
    private readonly Dictionary<TaskId, OperationId> _joins = [];
    private Timer? _control;
    private int _asking;
    private bool _busy;
    private volatile bool _disposed;
    private string? _commandProblem;

    /// <param name="title">A task's title, as the workflow names it now.</param>
    internal WorkflowRunViewModel(ProjectRuns runs, WorkflowRunCoordinator coordinator, Func<TaskId, string> title)
    {
        _runs = runs;
        _title = title;
        Coordinator = coordinator;
        View = coordinator.View;
        _stop = new RelayCommand(() => _ = StopAsync(), () => CanStop);
        _resume = new RelayCommand(() => _ = ResumeAsync(), () => CanResume);
        coordinator.Changed += OnChanged;
        WatchControl();
    }

    /// <summary>How often a window that only reads the run asks for control again. Tests shorten it.</summary>
    internal static TimeSpan ControlRetry { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long a command waits before it tries a busy journal or lock again, up to <see cref="Tries"/> tries.</summary>
    internal static TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    internal const int Tries = 3;

    internal WorkflowRunCoordinator Coordinator { get; private set; }

    /// <summary>The coordinator's newest projection, as this view model last showed it.</summary>
    internal RunView View { get; private set; }

    internal RunAddress Address => Coordinator.Address;

    internal WorkflowId Workflow => Address.Workflow;

    /// <summary>Raised on the UI thread after <see cref="View"/> changes.</summary>
    internal event EventHandler? ViewChanged;

    /// <summary>Raised on the UI thread after a coordinator that controls the run replaced one that only read it.</summary>
    internal event EventHandler? Replaced;

    /// <summary>The run is approved or stopping, so it owns its tasks.</summary>
    public bool IsActive => View.Phase is RunPhase.Approved or RunPhase.StopRequested;

    public bool IsControlled => View.Controlled;

    /// <summary>The run's status, which counts the tasks whose clients run while there are several: "3 running".</summary>
    public string StatusLabel => WorkflowRunText.Status(View);

    public StatusTone Tone => WorkflowRunText.Tone(View.Status);

    /// <summary>"2 of 5 done".</summary>
    public string? Progress => WorkflowRunText.Progress(View);

    /// <summary>The pill's text for a screen reader: the status, then the progress.</summary>
    public string Summary => Progress is { } progress ? $"Workflow run: {StatusLabel}, {progress}" : $"Workflow run: {StatusLabel}";

    /// <summary>Why the run could not do something, from its journal or from the last command, or null.</summary>
    public string? Problem => _commandProblem ?? View.Problem;

    /// <summary>
    /// The bar's second line: the run's problem, else what goes on now, else what the person can do. Null for a run that
    /// says all in its status. A line wraps only between the titles it names, never inside one.
    /// </summary>
    public string? Activity => WorkflowRunText.Unbroken(Problem ?? Now());

    /// <summary>A client of the run starts, runs, or settles in this window.</summary>
    public bool HasActivity => IsActive && View.Tasks.Values.Any(WorkflowRunText.Busy);

    /// <summary>A task of the run waits for the person: a reply, a question, or an approval.</summary>
    public bool IsWaitingForPerson => IsActive && View.Tasks.Values.Any(task => task.State == TaskState.Waiting &&
        (task.Gate is { Status: GateStatus.Waiting } || task.Status == AttemptStatus.WaitingForInput));

    public bool CanStop => IsControlled && View.Phase == RunPhase.Approved && !_busy;

    public bool ShowsStop => IsActive && IsControlled;

    public bool CanResume => IsControlled && View.Phase == RunPhase.Approved && !View.Resumed && !_busy;

    public bool ShowsResume => CanResume || IsControlled && View.Phase == RunPhase.Approved && !View.Resumed;

    /// <summary>
    /// Records the run's stop. No task starts after it, running turns are cancelled, and waiting attempts close without
    /// sending queued text. Repeating it repeats one stop.
    /// </summary>
    public ICommand StopCommand => _stop;

    /// <summary>Authorizes the run's scheduling in this window, after a reopen or after taking control from another window.</summary>
    public ICommand ResumeCommand => _resume;

    internal Task<RunCommand> StopAsync() => Command(() => Coordinator.Stop(Address, _stopCommand));

    /// <summary>
    /// Adds <paramref name="task"/> to the run, as a node's Run does while the run is active (#90). One confirmation per task
    /// in this view model, so a repeat or a retry after a busy refusal adds it once.
    /// </summary>
    internal Task<RunCommand> JoinAsync(TaskId task)
    {
        var confirmation = _joins.TryGetValue(task, out var known) ? known : _joins[task] = new OperationId(Guid.NewGuid());
        return Retrying(() => Coordinator.Request(Address, task, confirmation),
            outcome => outcome is RunCommand.Refused refused && WorkflowRunText.Transient(refused.Reason));
    }

    internal Task<RunCommand> ResumeAsync() => Command(() => Coordinator.Resume(Address));

    public void Dispose()
    {
        _disposed = true;
        _control?.Dispose();
        _control = null;
        Coordinator.Changed -= OnChanged;
    }

    /// <summary>
    /// Runs a coordinator command, and tries it again while a busy journal or lock refuses it. The last refusal shows as the
    /// run's problem; a later success clears it.
    /// </summary>
    private async Task<RunCommand> Command(Func<Task<RunCommand>> command)
    {
        _busy = true;
        Raise();
        RunCommand outcome;
        try
        {
            outcome = await Retrying(command, result => result is RunCommand.Refused refused && WorkflowRunText.Transient(refused.Reason));
        }
        finally
        {
            _busy = false;
        }

        _commandProblem = outcome switch
        {
            RunCommand.Unavailable unavailable => unavailable.Message,
            RunCommand.Refused refused => WorkflowRunText.Problem(refused.Reason),
            _ => null,
        };
        Raise();
        return outcome;
    }

    /// <summary>
    /// Opens the run off the UI thread, because opening takes the run's control and reads and writes its journal, and tries
    /// again while a busy journal or lock refuses it. Null when the project closed meanwhile.
    /// </summary>
    internal static async Task<RunOpen?> OpenAsync(ProjectRuns runs, WorkflowId workflow, RunId run)
    {
        try
        {
            return await Retrying(() => Task.Run(() => runs.OpenRun(workflow, run)),
                outcome => outcome is RunOpen.Rejected rejected && WorkflowRunText.Transient(rejected.Reason));
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>Tries <paramref name="attempt"/> up to <see cref="Tries"/> times while its outcome is <paramref name="transient"/>.</summary>
    internal static async Task<T> Retrying<T>(Func<Task<T>> attempt, Func<T, bool> transient)
    {
        for (var tried = 1; ; tried++)
        {
            var outcome = await attempt();
            if (!transient(outcome) || tried >= Tries)
            {
                return outcome;
            }

            await Task.Delay(RetryDelay);
        }
    }

    private string? Now()
    {
        string Named(TaskView task) => $"\"{_title(task.Task)}\"";
        var tasks = View.Tasks.Values;
        switch (View.Status)
        {
            case RunStatus.Paused:
                return "Resume to start its next tasks in this window.";
            case RunStatus.Elsewhere:
                return "Another iDevelop window controls this run. This window shows it and takes over once that window closes.";
            case RunStatus.Stopping:
                // A turn whose end was never recorded keeps the run stopping until the person closes it (ADR 0005).
                return WorkflowRunText.Stopping(tasks, _title);
            case RunStatus.Stopped:
                return "Stopped. Finished work stays.";
            case RunStatus.Completed:
                // A run that a node's Run started completes once nothing more can start, which can leave tasks it never ran (#90).
                return tasks.All(task => task.State == TaskState.Done) ? "Every task has a current result." : "Every task it ran has a current result.";
        }

        if (WorkflowRunText.Working(tasks, _title) is { } working)
        {
            return working;
        }

        if (tasks.FirstOrDefault(task => task.State == TaskState.Waiting && task.Gate is { Status: GateStatus.Waiting }) is { } gate)
        {
            return $"{Named(gate)} waits for your approval.";
        }

        if (tasks.FirstOrDefault(task => task.State == TaskState.Waiting && task.Status == AttemptStatus.WaitingForInput) is { } waiting)
        {
            return $"{Named(waiting)} waits for you.";
        }

        return tasks.FirstOrDefault(task => task.State is TaskState.Failed or TaskState.Blocked or TaskState.Uncertain or TaskState.Refused or
                TaskState.Stale or TaskState.SentBack) is { } stuck
            ? $"{Named(stuck)}: {WorkflowRunText.Of(stuck, _title).Label}."
            : null;
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        if (sender == Coordinator)
        {
            Dispatcher.UIThread.Post(() => Show((WorkflowRunCoordinator)sender!));
        }
    }

    private void Show(WorkflowRunCoordinator from)
    {
        if (_disposed || from != Coordinator)
        {
            return;
        }

        View = from.View;
        Raise();
    }

    private void Raise()
    {
        foreach (var property in new[]
        {
            nameof(IsActive), nameof(IsControlled), nameof(StatusLabel), nameof(Tone), nameof(Progress), nameof(Summary), nameof(Problem), nameof(Activity),
            nameof(HasActivity), nameof(IsWaitingForPerson), nameof(CanStop), nameof(ShowsStop), nameof(CanResume), nameof(ShowsResume),
        })
        {
            OnPropertyChanged(property);
        }

        _stop.NotifyCanExecuteChanged();
        _resume.NotifyCanExecuteChanged();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A coordinator that only reads the run never sees the other window's decisions, so a timer rereads the journal
    /// and asks for control again until this window gets it.</summary>
    private void WatchControl()
    {
        if (Coordinator.Controlled || _disposed)
        {
            _control?.Dispose();
            _control = null;
            return;
        }

        _control ??= new Timer(_ => AskForControl(), null, ControlRetry, ControlRetry);
    }

    private void AskForControl()
    {
        var reader = Coordinator;
        // A tick that comes while the last one still asks skips its turn.
        if (_disposed || reader.Controlled || Interlocked.Exchange(ref _asking, 1) != 0)
        {
            return;
        }

        try
        {
            reader.Refresh();
            if (_runs.OpenRun(reader.Address.Workflow, reader.Address.Run) is RunOpen.Opened { Coordinator: { Controlled: true } owner } && owner != reader)
            {
                Dispatcher.UIThread.Post(() => Adopt(reader, owner));
            }
        }
        catch (ObjectDisposedException)
        {
            // The project is closing, and this view model goes with it.
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The run's records cannot be read now. The next tick asks again; a timer callback must not throw.
        }
        finally
        {
            Volatile.Write(ref _asking, 0);
        }
    }

    private void Adopt(WorkflowRunCoordinator reader, WorkflowRunCoordinator owner)
    {
        if (_disposed || Coordinator != reader)
        {
            return;
        }

        reader.Changed -= OnChanged;
        Coordinator = owner;
        owner.Changed += OnChanged;
        View = owner.View;
        WatchControl();
        Raise();
        Replaced?.Invoke(this, EventArgs.Empty);
    }
}
