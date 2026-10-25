using System.ComponentModel;
using System.Windows.Input;
using IDevelop.Desktop.Execution;
using IDevelop.Desktop.Mvvm;
using IDevelop.Execution;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// Retry of a task the canvas's run owns, once its newest attempt ended without a result or it is done. The card, the card menu,
/// and the inspector offer it; the inspector asks for the confirmation, which says what Retry does before anything moves, and
/// takes an optional note to the agent.
/// </summary>
public sealed partial class TaskNodeViewModel
{
    private static readonly string[] RetryDependents =
        [nameof(ShowsRetry), nameof(ShowsRetryButton), nameof(ConfirmingRetry), nameof(RetryConfirmation), nameof(RetryNote), nameof(TakesRetryNote)];

    private readonly Dictionary<(AttemptId, string), OperationId> _retries = [];
    private string _retryNote = "";
    private RelayCommand _retry = null!;
    private RelayCommand _confirmRetry = null!;
    private RelayCommand _keepRetry = null!;
    private bool _confirmingRetry;
    private bool _retrying;

    /// <summary>
    /// The active run, which this window controls, holds the task's attempt that ended without a result, failed, cancelled,
    /// or closed by recovery, or the task is done. Only a person starts a Retry, and only while the run is active (ADR 0005).
    /// </summary>
    public bool ShowsRetry => _runActive && _canvas.Run?.View is { Phase: RunPhase.Approved, Controlled: true, Snapshot: { } snapshot } &&
        WorkflowRunCoordinator.OffersRetry(snapshot, RunTask);

    /// <summary>Retry, until the inspector asks for its confirmation.</summary>
    public bool ShowsRetryButton => ShowsRetry && !_confirmingRetry;

    /// <summary>The inspector asks whether to retry the task.</summary>
    public bool ConfirmingRetry => ShowsRetry && _confirmingRetry;

    /// <summary>What Retry does, which the person confirms. A done task's Retry warns that the tasks after it go out of date (ADR 0034).</summary>
    public string RetryConfirmation => RunTask switch
    {
        { State: TaskState.Done } => $"Retry runs \"{Title}\" again in a fresh session, with the newest work of the tasks before it. The tasks " +
            "after it go out of date and run again once it has a new result.",
        // The run refused a step before an attempt ended, such as its start, so Retry tries that step again.
        var task when WorkflowRunCoordinator.RetriesRefusedStep(task) =>
            $"Retry tries again to start \"{Title}\", or the step the run refused. The tasks after it run once it has a result.",
        _ => $"Retry runs \"{Title}\" again in a fresh session, with the files its last attempt left. The tasks after it run once it has a result.",
    };

    /// <summary>The confirmation takes a note to the agent: Retry starts a new attempt, whose first prompt the note ends.</summary>
    public bool TakesRetryNote => ConfirmingRetry && !WorkflowRunCoordinator.RetriesRefusedStep(RunTask);

    /// <summary>
    /// The person's optional note to the agent, which ends its first prompt. When the task feeds a sent-back Approval node,
    /// the confirmation opens with the send back's reason.
    /// </summary>
    public string RetryNote
    {
        get => _retryNote;
        set => SetProperty(ref _retryNote, value);
    }

    /// <summary>Shows the task in the inspector, which asks for the confirmation.</summary>
    public ICommand RetryCommand => _retry;

    /// <summary>Starts the retry as confirmed.</summary>
    public ICommand ConfirmRetryCommand => _confirmRetry;

    /// <summary>Closes the confirmation, and nothing moves.</summary>
    public ICommand KeepRetryCommand => _keepRetry;

    partial void InitializeRetry()
    {
        _retry = new RelayCommand(AskRetry, () => ShowsRetry);
        _confirmRetry = new RelayCommand(() => _ = RetryAsync(), () => ConfirmingRetry && !_retrying);
        _keepRetry = new RelayCommand(() => SetConfirmingRetry(false), () => ConfirmingRetry);
        PropertyChanged += OnRetryInputChanged;
    }

    private void OnRetryInputChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RunTask))
        {
            return;
        }

        if (!ShowsRetry)
        {
            _confirmingRetry = false;
        }

        NotifyRetry();
    }

    private void AskRetry()
    {
        _canvas.Inspect(this);
        if (!_confirmingRetry)
        {
            RetryNote = _canvas.Run?.View is { Snapshot: { } snapshot } view ? WorkflowRunCoordinator.SentBackNote(snapshot, view, Id) ?? "" : "";
        }

        SetConfirmingRetry(true);
    }

    private void SetConfirmingRetry(bool confirming)
    {
        _confirmingRetry = confirming;
        NotifyRetry();
    }

    private void NotifyRetry()
    {
        foreach (var property in RetryDependents)
        {
            OnPropertyChanged(property);
        }

        _retry.NotifyCanExecuteChanged();
        _confirmRetry.NotifyCanExecuteChanged();
        _keepRetry.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// One confirmation per ended attempt and note, so a repeat, or a retry after a busy refusal, reserves one attempt; the
    /// run's next decision starts it.
    /// </summary>
    private async Task RetryAsync()
    {
        if (RunTask is not { } task || task.Attempt is null && !WorkflowRunCoordinator.RetriesRefusedStep(task) || _canvas.Run is not { } run)
        {
            return;
        }

        // A step refused before any attempt was reserved retries under one confirmation of its own.
        var previous = task.Attempt ?? default;
        var note = TakesRetryNote ? RetryNote.Trim() : "";
        var confirmation = _retries.TryGetValue((previous, note), out var known) ? known : _retries[(previous, note)] = new OperationId(Guid.NewGuid());
        var (coordinator, address) = (run.Coordinator, run.Address);
        _retrying = true;
        _confirmRetry.NotifyCanExecuteChanged();
        RetryReply reply;
        try
        {
            reply = await WorkflowRunViewModel.Retrying(() => coordinator.Retry(address, Id, confirmation, note),
                outcome => outcome is RetryReply.Refused refused && WorkflowRunText.Transient(refused.Reason));
        }
        finally
        {
            _retrying = false;
        }

        if (reply is RetryReply.Reserved or RetryReply.Restarted)
        {
            _confirmingRetry = false;
            RetryNote = "";
        }

        NotifyRetry();
        _canvas.Notice(reply switch
        {
            RetryReply.Blocked blocked => $"{blocked.Block.Detail} The retry did not start.",
            RetryReply.Refused refused => WorkflowRunText.Problem(refused.Reason),
            RetryReply.Unavailable unavailable => unavailable.Message,
            _ => null,
        });
    }
}
