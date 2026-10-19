using System.ComponentModel;
using System.Windows.Input;
using IDevelop.Desktop.Execution;
using IDevelop.Desktop.Mvvm;
using IDevelop.Execution;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// Retry of a task the canvas's run owns, once its newest attempt ended without a result. The card, the card menu,
/// and the inspector offer it; the inspector asks for the confirmation, which says what Retry does before anything moves.
/// </summary>
public sealed partial class TaskNodeViewModel
{
    private static readonly string[] RetryDependents =
        [nameof(ShowsRetry), nameof(ShowsRetryButton), nameof(ConfirmingRetry), nameof(RetryConfirmation)];

    private readonly Dictionary<AttemptId, OperationId> _retries = [];
    private RelayCommand _retry = null!;
    private RelayCommand _confirmRetry = null!;
    private RelayCommand _keepRetry = null!;
    private bool _confirmingRetry;
    private bool _retrying;

    /// <summary>
    /// The active run, which this window controls, holds the task's attempt that ended without a result: failed, cancelled,
    /// or closed by recovery. Only a person starts a Retry, and only while the run is active (ADR 0005).
    /// </summary>
    public bool ShowsRetry => _runActive && _canvas.Run?.View is { Phase: RunPhase.Approved, Controlled: true, Snapshot: { } snapshot } &&
        WorkflowRunCoordinator.OffersRetry(snapshot, RunTask);

    /// <summary>Retry, until the inspector asks for its confirmation.</summary>
    public bool ShowsRetryButton => ShowsRetry && !_confirmingRetry;

    /// <summary>The inspector asks whether to retry the task.</summary>
    public bool ConfirmingRetry => ShowsRetry && _confirmingRetry;

    /// <summary>What Retry does, which the person confirms.</summary>
    public string RetryConfirmation => $"Retry runs \"{Title}\" again in a fresh session, with the files its last attempt left. " +
        "The tasks after it run once it has a result.";

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
    /// One confirmation per failed attempt, so a repeat, or a retry after a busy refusal, reserves one attempt; the run's
    /// next decision starts it.
    /// </summary>
    private async Task RetryAsync()
    {
        if (RunTask is not { Attempt: { } previous } || _canvas.Run is not { } run)
        {
            return;
        }

        var confirmation = _retries.TryGetValue(previous, out var known) ? known : _retries[previous] = new OperationId(Guid.NewGuid());
        var (coordinator, address) = (run.Coordinator, run.Address);
        _retrying = true;
        _confirmRetry.NotifyCanExecuteChanged();
        RetryReply reply;
        try
        {
            reply = await WorkflowRunViewModel.Retrying(() => coordinator.Retry(address, Id, confirmation),
                outcome => outcome is RetryReply.Refused refused && WorkflowRunText.Transient(refused.Reason));
        }
        finally
        {
            _retrying = false;
        }

        if (reply is RetryReply.Reserved)
        {
            _confirmingRetry = false;
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
