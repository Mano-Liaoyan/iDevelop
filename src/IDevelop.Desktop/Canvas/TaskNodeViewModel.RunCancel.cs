using System.ComponentModel;
using System.Windows.Input;
using IDevelop.Execution;
using IDevelop.Desktop.Mvvm;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// Cancel of one task that the canvas's run owns, while its attempt runs or waits. The expanded card, the card menu, and
/// the inspector offer it, and it goes through the run as the conversation's Cancel does. The task then shows Cancelled
/// with Retry, the tasks after it wait, the others go on, and the run needs attention until Retry or Stop Workflow.
/// </summary>
public sealed partial class TaskNodeViewModel
{
    private RelayCommand _cancelInRun = null!;
    private bool _cancellingInRun;

    /// <summary>
    /// The active run, which this window controls, holds the task's attempt while it runs or waits. Cancelling a review also
    /// cancels its fix (ADR 0013).
    /// </summary>
    public bool ShowsRunCancel => HasAgent && _runActive &&
        _canvas.Run?.View is { Phase: RunPhase.Approved, Controlled: true } &&
        RunTask is { State: TaskState.Running or TaskState.Waiting, Attempt: not null };

    /// <summary>Cancels the task's attempt in the run.</summary>
    public ICommand CancelInRunCommand => _cancelInRun;

    partial void InitializeRunCancel()
    {
        _cancelInRun = new RelayCommand(() => _ = CancelInRunAsync(), () => ShowsRunCancel && !_cancellingInRun);
        PropertyChanged += OnRunCancelInputChanged;
    }

    private void OnRunCancelInputChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RunTask) or nameof(HasAgent))
        {
            OnPropertyChanged(nameof(ShowsRunCancel));
            _cancelInRun.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Asks the run's conversation of the task which turn it is on, and cancels that turn, so a Cancel that arrives after
    /// the task moved on is refused as stale and cancels nothing it did not see.
    /// </summary>
    private async Task CancelInRunAsync()
    {
        if (_canvas.Run is not { } run)
        {
            return;
        }

        _cancellingInRun = true;
        _cancelInRun.NotifyCanExecuteChanged();
        ConversationCommandResult result;
        try
        {
            using var session = _canvas.Runs.OpenConversation(run.Coordinator, Id);
            result = session.Snapshot is { Current: { } turn, Actions.Cancel.Enabled: true }
                ? await session.CancelAsync(turn, CancellationToken.None)
                : new ConversationCommandResult(CommandOutcome.Refused, session.Snapshot.Actions.Cancel.Reason);
        }
        finally
        {
            _cancellingInRun = false;
        }

        _cancelInRun.NotifyCanExecuteChanged();
        _canvas.Notice(result.Outcome == CommandOutcome.Applied ? null : result.Detail);
    }
}
