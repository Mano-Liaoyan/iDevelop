using System.Windows.Input;
using IDevelop.Desktop.Mvvm;
using IDevelop.Execution;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Execution;

/// <summary>
/// A task of the run that a block holds or whose turn is unresolved, as the inspector's Recovery section shows it. It
/// shows the evidence: what the block names, the client's process, how its turn ended, the captures, and the cleanup.
/// <para>
/// A drifted checkout has Preserve and restore: Preserve retains the checkout as it is now, the preview names each move
/// back to the recorded baseline, and Restore makes them, bound to that preview. A turn whose end was never recorded has
/// Close as stopped, with the person's reason. Only the controlling window acts, and a busy journal or lock is tried
/// again with the same operation.
/// </para>
/// </summary>
public sealed class RecoveryViewModel : ObservableObject, IDisposable
{
    private readonly WorkflowRunViewModel _run;
    private readonly Func<TaskId, string> _title;
    private readonly Action<TaskId> _show;
    private readonly RelayCommand _preserve;
    private readonly RelayCommand _restore;
    private readonly RelayCommand _close;
    private readonly RelayCommand _showOwner;
    private readonly Dictionary<string, OperationId> _closures = [];
    private TaskView _view;
    private TaskEvidence? _evidence;
    private OperationId? _preserving;
    private OperationId? _preservation;
    private string? _retained;
    private RestorePreview? _preview;
    private OperationId? _restoring;
    private string? _notice;
    private string _reason = "";
    private bool _busy;
    private bool _disposed;

    internal RecoveryViewModel(WorkflowRunViewModel run, TaskView view, Func<TaskId, string> title, Action<TaskId> show)
    {
        _run = run;
        _view = view;
        _title = title;
        _show = show;
        _preserve = new RelayCommand(() => _ = PreserveAsync(), () => CanAct && ShowsPreserve);
        _restore = new RelayCommand(() => _ = RestoreAsync(), () => CanAct && _preview is not null);
        _close = new RelayCommand(() => _ = CloseAsync(Reason.Trim()), () => CanAct && CanClose && !string.IsNullOrWhiteSpace(Reason));
        _showOwner = new RelayCommand(() => _show(Owner));
        run.PropertyChanged += OnRunChanged;
        _ = LoadEvidenceAsync();
    }

    /// <summary>On macOS Restore cannot move files or remove index.lock, so the section says to change them by hand. Tests set it.</summary>
    internal static bool HandRepair { get; set; } = OperatingSystem.IsMacOS();

    /// <summary>The task's turn ended without a recorded end, so only the person's confirmation closes it.</summary>
    public bool IsUncertain => _view.State == TaskState.Uncertain;

    /// <summary>The block holds another task's checkout, which the person restores from that task.</summary>
    public bool IsElsewhere => _view.State == TaskState.Blocked && Owner != _view.Task;

    /// <summary>"The block is on the checkout of "A"."</summary>
    public string? OwnerNote => IsElsewhere ? $"The block is on the checkout of \"{_title(Owner)}\". Restore it there." : null;

    public string ShowOwnerLabel => $"Show \"{_title(Owner)}\"";

    /// <summary>Selects the task whose checkout the block is on.</summary>
    public ICommand ShowOwnerCommand => _showOwner;

    /// <summary>"Initial attempt · 01a11de4": the attempt the evidence is about.</summary>
    public string? AttemptText => _evidence is { Attempt: not null } evidence && !IsElsewhere ? RecoveryText.Attempt(evidence) : null;

    /// <summary>Every block that holds the task, oldest first, each with all the paths and refs it names.</summary>
    public IReadOnlyList<RecoveryBlock> Blocks => IsElsewhere ? [] :
        [.. (_evidence is { } evidence ? evidence.Blocks : Block is { } shown ? [shown] : []).Select(block => new RecoveryBlock(
            RecoveryText.Problem(block), block.Detail, RecoveryText.Paths(block), RecoveryText.Refs(block),
            RecoveryText.RefRepair(block, _evidence?.Refs ?? [], clears: !CaptureRejected), block.Attempt is { } attempt ? attempt.Value.ToString("D")[..8] : null))];

    /// <summary>The newest turn's client process, and whether it still runs.</summary>
    public string? ClientText => _evidence is { } evidence && !IsElsewhere ? RecoveryText.Client(evidence) : null;

    public string? TurnEndText => _evidence is { Attempt: not null } evidence && !IsElsewhere ? RecoveryText.TurnEnd(evidence) : null;

    public string? CapturesText => _evidence is { Attempt: not null } evidence && !IsElsewhere ? RecoveryText.Captures(evidence) : null;

    /// <summary>Every path in which the captures differ, one per line.</summary>
    public string? CapturePaths => _evidence is { Attempt: not null } evidence && !IsElsewhere ? RecoveryText.CapturePaths(evidence) : null;

    public string? CleanupText => _evidence is { } evidence && !IsElsewhere ? RecoveryText.Cleanup(evidence) : null;

    /// <summary>A drift or capture block on this task's own checkout, which Preserve and restore clears.</summary>
    public bool CanRestore => _view.State == TaskState.Blocked && !IsElsewhere && Attempt is not null &&
        Block is { Problem: MaterializationProblem.DirtyWorktree or MaterializationProblem.UncertainOwnership };

    /// <summary>A block on this task that Preserve and restore cannot clear, such as a conflicting join.</summary>
    public string? NoActionNote => _view.State == TaskState.Blocked && !IsElsewhere && !CanRestore
        ? "Preserve and restore cannot clear this block. Stop Workflow ends the run, and finished work stays." : null;

    public string? HandRepairNote => CanRestore && HandRepair ? RecoveryText.HandRepair : null;

    /// <summary>
    /// The attempt's newest turn-end capture was not accepted, or never taken, so restoring its checkout cannot give it a
    /// result: only a new attempt can.
    /// </summary>
    public bool CaptureRejected => _view.State == TaskState.Blocked && !IsElsewhere &&
        _evidence?.Dispositions.LastOrDefault()?.Disposition is CaptureDisposition.Diverged or CaptureDisposition.Failed;

    public string? NoSuccessNote => CaptureRejected ? RecoveryText.NoSuccess : null;

    /// <summary>Preserve shows until a preview waits for Restore.</summary>
    public bool ShowsPreserve => CanRestore && _preview is null;

    /// <summary>Retains the checkout as it is now, then previews Restore.</summary>
    public ICommand PreserveCommand => _preserve;

    public bool HasPreview => _preview is not null;

    /// <summary>"Restore puts the checkout back to the result it handed on."</summary>
    public string? PreviewTarget => _preview is { } preview ? RecoveryText.Target(preview) : null;

    /// <summary>Each path Restore moves.</summary>
    public IReadOnlyList<string> PreviewMoves => _preview is { } preview ? RecoveryText.Moves(preview) : [];

    /// <summary>Refs, the index lock, and the blocks it clears.</summary>
    public string? PreviewRest => _preview is { } preview ? RecoveryText.Rest(preview) : null;

    /// <summary>Where the checkout's current state stays retained.</summary>
    public string? Retained => _preview is not null && _retained is { } name ? $"What the checkout holds now stays retained under {name}." : null;

    /// <summary>Makes the previewed moves, each checked again first, and clears the blocks they repair.</summary>
    public ICommand RestoreCommand => _restore;

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
        ? $"Process {root.Id} still runs. Stop it first, or what it writes later blocks the checkout." : null;

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

    /// <summary>The task whose checkout the block is on.</summary>
    private TaskId Owner => Block?.Task ?? _view.Task;

    private AttemptId? Attempt => Block?.Attempt ?? _view.Attempt;

    /// <summary>
    /// Whether this section shows <paramref name="view"/> of <paramref name="run"/>: the same task, state, and checkout. A
    /// preservation records blocks of its own, so the block the task shows can change while a preview waits for Restore.
    /// </summary>
    internal bool Shows(WorkflowRunViewModel run, TaskView view) => run == _run && view.Task == _view.Task && view.State == _view.State &&
        (view.Block?.Attempt ?? view.Attempt) == Attempt && (view.Block?.Task ?? view.Task) == Owner;

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
        var (task, attempt) = (Owner, Attempt);
        var evidence = await Task.Run(() => coordinator.Evidence(task, attempt));
        if (!_disposed)
        {
            _evidence = evidence;
            Raise();
        }
    }

    private async Task PreserveAsync()
    {
        if (Attempt is not { } attempt)
        {
            return;
        }

        var (coordinator, address, task) = (_run.Coordinator, _run.Address, Owner);
        // A repeat after a busy refusal reuses the operation; a new click retains the checkout anew.
        var operation = _preserving ??= new OperationId(Guid.NewGuid());
        Busy(true);
        Notice = null;
        Preservation preserved;
        try
        {
            preserved = await WorkflowRunViewModel.Retrying(() => coordinator.Preserve(address, task, attempt, operation),
                outcome => outcome is Preservation.Rejected rejected && WorkflowRunText.Transient(rejected.Reason));
            if (preserved is not Preservation.Rejected { Reason: var reason } || !WorkflowRunText.Transient(reason))
            {
                _preserving = null;
            }

            switch (preserved)
            {
                case Preservation.Preserved receipt:
                    _preservation = operation;
                    _retained = receipt.Receipt.Ref;
                    await PreviewAsync(attempt);
                    break;
                case Preservation.Blocked blocked:
                    Notice = $"{blocked.Block.Detail} Nothing moved.";
                    break;
                case Preservation.Rejected rejected:
                    Notice = RecoveryText.Problem(rejected.Reason);
                    break;
                case Preservation.Unavailable unavailable:
                    Notice = unavailable.Message;
                    break;
            }
        }
        finally
        {
            Busy(false);
        }

        await LoadEvidenceAsync();
    }

    private async Task PreviewAsync(AttemptId attempt)
    {
        var (coordinator, address, task, preservation) = (_run.Coordinator, _run.Address, Owner, _preservation!.Value);
        var read = await WorkflowRunViewModel.Retrying(() => coordinator.PreviewRestore(address, task, attempt, preservation),
            outcome => outcome is RestorePreviewRead.Rejected rejected && WorkflowRunText.Transient(rejected.Reason));
        if (_disposed)
        {
            return;
        }

        switch (read)
        {
            case RestorePreviewRead.Previewed previewed:
                _preview = previewed.Preview;
                _restoring = new OperationId(Guid.NewGuid());
                break;
            case RestorePreviewRead.Refused refused:
                Notice = refused.Detail;
                break;
            case RestorePreviewRead.Rejected rejected:
                Notice = RecoveryText.Problem(rejected.Reason);
                break;
            case RestorePreviewRead.Unavailable unavailable:
                Notice = unavailable.Message;
                break;
        }

        Raise();
    }

    private async Task RestoreAsync()
    {
        if (_preview is not { } preview || Attempt is not { } attempt || _preservation is not { } preservation || _restoring is not { } operation)
        {
            return;
        }

        var (coordinator, address, task) = (_run.Coordinator, _run.Address, Owner);
        // Whether the attempt can still have a result is known before the restore; afterwards the run may already have
        // recorded its publication's block again.
        var unaccepted = CaptureRejected;
        Busy(true);
        Notice = null;
        Restoration restored;
        try
        {
            restored = await WorkflowRunViewModel.Retrying(() => coordinator.Restore(address, task, attempt, preservation, preview.Identity, operation),
                outcome => outcome is Restoration.Rejected rejected && WorkflowRunText.Transient(rejected.Reason));
        }
        finally
        {
            Busy(false);
        }

        if (_disposed)
        {
            return;
        }

        // Only a busy refusal keeps the preview, so trying again restores the same thing.
        if (restored is not Restoration.Rejected { Reason: var reason } || !WorkflowRunText.Transient(reason))
        {
            _preview = null;
        }

        Notice = restored switch
        {
            Restoration.Restored => null,
            Restoration.Blocked blocked => $"{blocked.Block.Detail} Nothing more moved.",
            Restoration.Refused refused => refused.Detail,
            Restoration.Rejected rejected => RecoveryText.Problem(rejected.Reason),
            Restoration.Unavailable unavailable => unavailable.Message,
            _ => null,
        };
        Raise();
        await LoadEvidenceAsync();
        // A restore resolves only what it repaired, and the blocks it rechecked whose refs are back, so the journal says
        // whether the task goes on.
        if (restored is Restoration.Restored && !_disposed)
        {
            Notice = unaccepted ? "Restored the checkout. This attempt still has no result, because its turn-end capture was not accepted."
                : _evidence is { Blocks.IsEmpty: false } still
                    ? $"Restore finished, but {(still.Blocks.Length == 1 ? "1 block still holds" : $"{still.Blocks.Length} blocks still hold")} this task. Each one says what it needs."
                : "Restored. The tasks this block held go on.";
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
            nameof(IsUncertain), nameof(IsElsewhere), nameof(OwnerNote), nameof(ShowOwnerLabel), nameof(AttemptText), nameof(Blocks), nameof(ClientText),
            nameof(TurnEndText), nameof(CapturesText), nameof(CapturePaths), nameof(CleanupText), nameof(CanRestore), nameof(NoActionNote), nameof(HandRepairNote),
            nameof(ShowsPreserve), nameof(HasPreview), nameof(PreviewTarget), nameof(PreviewMoves), nameof(PreviewRest), nameof(Retained), nameof(StillRuns),
            nameof(CanClose), nameof(UnsettledNote), nameof(CaptureRejected), nameof(NoSuccessNote),
        })
        {
            OnPropertyChanged(property);
        }

        Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(CanAct));
        _preserve.NotifyCanExecuteChanged();
        _restore.NotifyCanExecuteChanged();
        _close.NotifyCanExecuteChanged();
    }
}

/// <summary>One block that holds a task, as the Recovery section lists it: what went wrong, every path and ref it names, and how to repair a ref.</summary>
/// <param name="Attempt">The short id of the attempt the block is on, or null for a block on the task as a whole.</param>
public sealed record RecoveryBlock(string Problem, string Detail, string? Paths, string? Refs, string? RefRepair, string? Attempt);
