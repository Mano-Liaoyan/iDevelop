using System.Windows.Input;
using IDevelop.Desktop.Mvvm;
using IDevelop.Execution;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Execution;

/// <summary>
/// A task of the run whose result is stale, as the inspector's Updated inputs section shows it. Review updated inputs
/// previews a rebase of the task's recorded change onto the newer inputs: which tasks handed on newer results, what the
/// task changed, the report and artifacts the rebase carries forward, and the clean candidate or why there is none.
/// Approve rebase records the rebased result as the person saw it, with no client run. Only the controlling window acts.
/// </summary>
public sealed class UpdatedInputsViewModel : ObservableObject, IDisposable
{
    private readonly WorkflowRunViewModel _run;
    private readonly Func<TaskId, string> _title;
    private readonly RelayCommand _review;
    private readonly RelayCommand _approve;
    private readonly TaskId _task;
    private RebasePreview? _preview;
    private OperationId? _approval;
    private string? _notice;
    private bool _busy;
    private bool _disposed;

    internal UpdatedInputsViewModel(WorkflowRunViewModel run, TaskId task, Func<TaskId, string> title)
    {
        _run = run;
        _task = task;
        _title = title;
        _review = new RelayCommand(() => _ = ReviewAsync(), () => CanAct);
        _approve = new RelayCommand(() => _ = ApproveAsync(), () => CanAct && CanApprove);
        run.PropertyChanged += OnRunChanged;
    }

    /// <summary>What reviewing does, before the first preview.</summary>
    public string Intro => "A task before this one handed on a newer result after this one finished. Review updated inputs replays this task's change " +
        "onto the newer inputs as one commit, which you approve. No agent runs.";

    /// <summary>Builds the rebase's preview. It records nothing.</summary>
    public ICommand ReviewCommand => _review;

    public bool HasPreview => _preview is not null;

    /// <summary>"Newer results from "A"."</summary>
    public string? Updated => _preview is { } preview
        ? $"Newer results from {RecoveryText.List([.. preview.Updated.Select(task => $"\"{_title(task)}\"")])}." : null;

    /// <summary>The paths the task's recorded change touches.</summary>
    public string? Changes => _preview is { } preview ? preview.Changes.IsEmpty ? "None" : RecoveryText.List(preview.Changes) : null;

    /// <summary>The task's report, which the rebased result carries forward.</summary>
    public string? Report => _preview?.Report is { Length: > 0 } report ? report.TrimEnd() : null;

    /// <summary>The artifacts the rebased result carries forward.</summary>
    public string? Artifacts => _preview is { Artifacts.IsEmpty: false } preview ? RecoveryText.List([.. preview.Artifacts.Select(artifact => artifact.Name)]) : null;

    /// <summary>What the clean candidate updates, or why there is none.</summary>
    public string? Candidate => _preview is { } preview ? RecoveryText.Candidate(preview.Candidate) : null;

    /// <summary>The preview has a clean candidate to approve.</summary>
    public bool CanApprove => _preview is { Candidate: RebaseCandidate.Clean };

    /// <summary>Records the rebased result, as previewed, and moves the task's branch and checkout to it.</summary>
    public ICommand ApproveCommand => _approve;

    /// <summary>Why the last command recorded nothing, or null.</summary>
    public string? Notice
    {
        get => _notice;
        private set => SetProperty(ref _notice, value);
    }

    public bool CanAct => _run.IsControlled && _run.IsActive && !_busy;

    internal bool Shows(WorkflowRunViewModel run, TaskView view) => run == _run && view.Task == _task && view.State == TaskState.Stale;

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
    }

    private async Task ReviewAsync()
    {
        var (coordinator, address) = (_run.Coordinator, _run.Address);
        Busy(true);
        Notice = null;
        RebasePreviewRead read;
        try
        {
            // The preview holds the repository's lock, so another step that holds it now refuses it for a moment.
            read = await WorkflowRunViewModel.Retrying(() => coordinator.ReviewUpdatedInputs(address, _task),
                outcome => outcome is RebasePreviewRead.Rejected rejected && WorkflowRunText.Transient(rejected.Reason));
        }
        finally
        {
            Busy(false);
        }

        if (_disposed)
        {
            return;
        }

        _preview = null;
        switch (read)
        {
            case RebasePreviewRead.Previewed previewed:
                _preview = previewed.Preview;
                _approval = new OperationId(Guid.NewGuid());
                break;
            case RebasePreviewRead.Refused refused:
                Notice = refused.Detail;
                break;
            case RebasePreviewRead.Rejected rejected:
                Notice = RecoveryText.Problem(rejected.Reason);
                break;
            case RebasePreviewRead.Unavailable unavailable:
                Notice = unavailable.Message;
                break;
        }

        Raise();
    }

    private async Task ApproveAsync()
    {
        if (_preview is not { } preview || _approval is not { } approval)
        {
            return;
        }

        var (coordinator, address) = (_run.Coordinator, _run.Address);
        Busy(true);
        Notice = null;
        Rebasing outcome;
        try
        {
            outcome = await WorkflowRunViewModel.Retrying(() => coordinator.ApproveRebase(address, _task, preview.Identity, approval),
                result => result is Rebasing.Rejected rejected && WorkflowRunText.Transient(rejected.Reason));
        }
        finally
        {
            Busy(false);
        }

        if (_disposed)
        {
            return;
        }

        Notice = outcome switch
        {
            Rebasing.Rebased => null,
            Rebasing.Blocked blocked => $"{blocked.Block.Detail} Nothing was rebased.",
            Rebasing.Rejected rejected => RecoveryText.Problem(rejected.Reason),
            Rebasing.Unavailable unavailable => unavailable.Message,
            _ => null,
        };
        // A preview that no longer holds is reviewed again; only a busy refusal keeps it for another try.
        if (outcome is not Rebasing.Rejected { Reason: var reason } || !WorkflowRunText.Transient(reason))
        {
            _preview = null;
        }

        Raise();
    }

    private void Busy(bool busy)
    {
        _busy = busy;
        Refresh();
    }

    private void Raise()
    {
        foreach (var property in new[] { nameof(HasPreview), nameof(Updated), nameof(Changes), nameof(Report), nameof(Artifacts), nameof(Candidate), nameof(CanApprove) })
        {
            OnPropertyChanged(property);
        }

        Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(CanAct));
        _review.NotifyCanExecuteChanged();
        _approve.NotifyCanExecuteChanged();
    }
}
