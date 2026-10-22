using System.Windows.Input;
using IDevelop.Desktop.Mvvm;
using IDevelop.Execution;

namespace IDevelop.Desktop.Execution;

/// <summary>
/// A task of the run that a block holds or whose turn is unresolved, as the inspector's Recovery section shows it. It
/// shows the evidence: the attempt, what holds it, the client's process, and how its turn ended.
/// <para>
/// A turn whose end was never recorded has Close as stopped, with the person's reason. Only the controlling window acts,
/// and a busy journal or lock is tried again with the same operation.
/// </para>
/// </summary>
public sealed class RecoveryViewModel : ObservableObject, IDisposable
{
    private readonly WorkflowRunViewModel _run;
    private readonly RelayCommand _close;
    private readonly Dictionary<string, OperationId> _closures = [];
    private TaskView _view;
    private TaskEvidence? _evidence;
    private string? _notice;
    private string _reason = "";
    private bool _busy;
    private bool _disposed;

    internal RecoveryViewModel(WorkflowRunViewModel run, TaskView view)
    {
        _run = run;
        _view = view;
        _close = new RelayCommand(() => _ = CloseAsync(Reason.Trim()), () => CanAct && CanClose && !string.IsNullOrWhiteSpace(Reason));
        run.PropertyChanged += OnRunChanged;
        _ = LoadEvidenceAsync();
    }

    /// <summary>The task's turn ended without a recorded end, so only the person's confirmation closes it.</summary>
    public bool IsUncertain => _view.State == TaskState.Uncertain;

    /// <summary>"Initial attempt · 01a11de4": the attempt the evidence is about.</summary>
    public string? AttemptText => _evidence is { Attempt: not null } evidence ? RecoveryText.Attempt(evidence) : null;

    /// <summary>Every block that holds the task, oldest first.</summary>
    public IReadOnlyList<RecoveryBlock> Blocks =>
        [.. (_evidence is { } evidence ? evidence.Blocks : Block is { } shown ? [shown] : []).Select(block => new RecoveryBlock(
            RecoveryText.Problem(block), block.Detail, block.Attempt is { } attempt ? attempt.Value.ToString("D")[..8] : null))];

    /// <summary>The newest turn's client process, and whether it still runs.</summary>
    public string? ClientText => _evidence is { } evidence ? RecoveryText.Client(evidence) : null;

    public string? TurnEndText => _evidence is { Attempt: not null } evidence ? RecoveryText.TurnEnd(evidence) : null;

    /// <summary>A block holds the task, and Stop Workflow is the way out.</summary>
    public string? NoActionNote => _view.State == TaskState.Blocked ? "Stop Workflow ends the run, and finished work stays." : null;

    /// <summary>Why the person closes the unresolved turn, which the closure records.</summary>
    public string Reason
    {
        get => _reason;
        set
        {
            if (SetProperty(ref _reason, value))
            {
                _close.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// The turn's client was never seen to exit, so the person may close it as stopped. A turn whose exit is recorded only
    /// waits for its settlement, which the person cannot close.
    /// </summary>
    public bool CanClose => IsUncertain && !_view.RootExited;

    /// <summary>Why an uncertain turn whose client exited cannot be closed, and what tries its settlement again.</summary>
    public string? UnsettledNote => IsUncertain && _view.RootExited ? RecoveryText.Unsettled(_view, _run.View.Phase == RunPhase.StopRequested) : null;

    /// <summary>The client still runs, which the person should stop before closing its turn.</summary>
    public string? StillRuns => CanClose && _evidence is { RootNow: ProcessMatch.Same, Root: { } root }
        ? $"Process {root.Id} still runs. Stop it first." : null;

    /// <summary>Closes the unresolved turn as stopped. It records no result, and the run does not start it again.</summary>
    public ICommand CloseCommand => _close;

    /// <summary>Why the last command recorded nothing, or what it did.</summary>
    public string? Notice
    {
        get => _notice;
        private set => SetProperty(ref _notice, value);
    }

    public bool CanAct => _run.IsControlled && _run.IsActive && !_busy;

    private MaterializationBlock? Block => _view.Block;

    private AttemptId? Attempt => Block?.Attempt ?? _view.Attempt;

    /// <summary>Whether this section shows <paramref name="view"/> of <paramref name="run"/>: the same task, state, and attempt.</summary>
    internal bool Shows(WorkflowRunViewModel run, TaskView view) => run == _run && view.Task == _view.Task && view.State == _view.State &&
        (view.Block?.Attempt ?? view.Attempt) == Attempt;

    internal void Show(TaskView view)
    {
        if (view == _view)
        {
            return;
        }

        _view = view;
        Raise();
    }

    public void Dispose()
    {
        _disposed = true;
        _run.PropertyChanged -= OnRunChanged;
    }

    private void OnRunChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkflowRunViewModel.IsControlled) or nameof(WorkflowRunViewModel.IsActive))
        {
            Refresh();
        }
        else if (e.PropertyName == nameof(WorkflowRunViewModel.StatusLabel))
        {
            OnPropertyChanged(nameof(UnsettledNote));
        }
    }

    private async Task LoadEvidenceAsync()
    {
        var coordinator = _run.Coordinator;
        var (task, attempt) = (_view.Task, Attempt);
        var evidence = await Task.Run(() => coordinator.Evidence(task, attempt));
        if (!_disposed)
        {
            _evidence = evidence;
            Raise();
        }
    }

    private async Task CloseAsync(string reason)
    {
        if (_view.Attempt is not { } attempt)
        {
            return;
        }

        var (coordinator, address, task) = (_run.Coordinator, _run.Address, _view.Task);
        // The same reason is one confirmation, so a second click or a retry records one closure.
        var operation = _closures.TryGetValue(reason, out var known) ? known : _closures[reason] = new OperationId(Guid.NewGuid());
        Busy(true);
        Notice = null;
        RunCommand closed;
        try
        {
            closed = await WorkflowRunViewModel.Retrying(() => coordinator.ConfirmStopped(address, task, attempt, reason, operation),
                outcome => outcome is RunCommand.Refused refused && WorkflowRunText.Transient(refused.Reason));
        }
        finally
        {
            Busy(false);
        }

        if (_disposed)
        {
            return;
        }

        Notice = closed switch
        {
            RunCommand.Refused refused => RecoveryText.Problem(refused.Reason),
            RunCommand.Unavailable unavailable => unavailable.Message,
            _ => null,
        };
    }

    private void Busy(bool busy)
    {
        _busy = busy;
        Refresh();
    }

    private void Raise()
    {
        foreach (var property in new[]
        {
            nameof(IsUncertain), nameof(AttemptText), nameof(Blocks), nameof(ClientText), nameof(TurnEndText), nameof(NoActionNote),
            nameof(StillRuns), nameof(CanClose), nameof(UnsettledNote),
        })
        {
            OnPropertyChanged(property);
        }

        Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(CanAct));
        _close.NotifyCanExecuteChanged();
    }
}

/// <summary>One block that holds a task, as the Recovery section lists it: what went wrong, and its detail.</summary>
/// <param name="Attempt">The short id of the attempt the block is on, or null for a block on the task as a whole.</param>
public sealed record RecoveryBlock(string Problem, string Detail, string? Attempt);
