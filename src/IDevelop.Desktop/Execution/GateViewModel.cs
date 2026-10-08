using System.Windows.Input;
using IDevelop.Desktop.Mvvm;
using IDevelop.Execution;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Execution;

/// <summary>
/// An Approval node's request in the run, as the inspector shows it: what it hands on, Approve, and Send back with a reason.
/// Each answer names the request and inputs the person saw, so an answer to a request that changed meanwhile records
/// nothing. A busy journal or lock is tried again with the same operation, which records one answer at most.
/// </summary>
public sealed class GateViewModel : ObservableObject, IDisposable
{
    private readonly WorkflowRunViewModel _run;
    private readonly RelayCommand _approve;
    private readonly RelayCommand _sendBack;
    private readonly OperationId _approval = new(Guid.NewGuid());
    private readonly Dictionary<string, OperationId> _sendBacks = [];
    private GateView _gate;
    private string? _report;
    private string _reason = "";
    private string? _notice;
    private bool _busy;
    private bool _disposed;

    internal GateViewModel(WorkflowRunViewModel run, GateView gate)
    {
        _run = run;
        _gate = gate;
        _approve = new RelayCommand(() => _ = AnswerAsync(sendBack: null), () => CanAnswer);
        _sendBack = new RelayCommand(() => _ = AnswerAsync(Reason.Trim()), () => CanAnswer && !string.IsNullOrWhiteSpace(Reason));
        run.PropertyChanged += OnRunChanged;
        _ = LoadReportAsync();
    }

    public string StatusLabel => _gate.Label;

    public StatusTone Tone => _gate.Status switch
    {
        GateStatus.Waiting => StatusTone.Waiting,
        GateStatus.Approved => StatusTone.Complete,
        GateStatus.SentBack => StatusTone.Problem,
        GateStatus.Closed => StatusTone.Neutral,
        _ => StatusTone.Neutral,
    };

    /// <summary>"Request 2", which counts the node's requests in this run.</summary>
    public string RequestLabel => $"Request {_gate.Request.Sequence}";

    /// <summary>What approving hands on: each input's report under its task's title, once it is read.</summary>
    public string? Report
    {
        get => _report;
        private set => SetProperty(ref _report, value);
    }

    /// <summary>The person's reason for sending the request back, which they write before Send back.</summary>
    public string Reason
    {
        get => _reason;
        set
        {
            if (SetProperty(ref _reason, value))
            {
                _sendBack.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>The reason the person gave when they sent it back.</summary>
    public string? SentBackReason => _gate.Reason;

    public bool IsWaiting => _gate.Status == GateStatus.Waiting;

    /// <summary>Why the last answer recorded nothing, or null.</summary>
    public string? Notice
    {
        get => _notice;
        private set => SetProperty(ref _notice, value);
    }

    public bool CanAnswer => IsWaiting && _run.IsControlled && !_busy;

    /// <summary>Records the person's approval, which hands the inputs on as the node's result and releases its dependents.</summary>
    public ICommand ApproveCommand => _approve;

    /// <summary>Sends the request back with the reason. Its dependents stay held, and nothing runs again on its own.</summary>
    public ICommand SendBackCommand => _sendBack;

    internal GateRequest Request => _gate.Request;

    internal bool Shows(WorkflowRunViewModel run, GateId request) => run == _run && request == _gate.Request.Id;

    /// <summary>The same request's newer view, such as after its answer was recorded.</summary>
    internal void Show(GateView gate)
    {
        if (gate == _gate)
        {
            return;
        }

        _gate = gate;
        foreach (var property in new[] { nameof(StatusLabel), nameof(Tone), nameof(SentBackReason), nameof(IsWaiting), nameof(CanAnswer) })
        {
            OnPropertyChanged(property);
        }

        Refresh();
    }

    public void Dispose()
    {
        _disposed = true;
        _run.PropertyChanged -= OnRunChanged;
    }

    private void OnRunChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkflowRunViewModel.IsControlled))
        {
            OnPropertyChanged(nameof(CanAnswer));
            Refresh();
        }
    }

    private void Refresh()
    {
        _approve.NotifyCanExecuteChanged();
        _sendBack.NotifyCanExecuteChanged();
    }

    private async Task LoadReportAsync()
    {
        var coordinator = _run.Coordinator;
        var request = _gate.Request.Id;
        var report = await Task.Run(() => coordinator.GateReport(request));
        if (!_disposed)
        {
            Report = report;
        }
    }

    private async Task AnswerAsync(string? sendBack)
    {
        var response = new GateResponse(_gate.Request.Id, _gate.Request.Inputs);
        var coordinator = _run.Coordinator;
        var address = _run.Address;
        // A repeat of the same answer reuses its operation, so a retry or a second click records one answer.
        var operation = sendBack is null ? _approval
            : _sendBacks.TryGetValue(sendBack, out var known) ? known : _sendBacks[sendBack] = new OperationId(Guid.NewGuid());
        _busy = true;
        Notice = null;
        OnPropertyChanged(nameof(CanAnswer));
        Refresh();
        GateReply reply;
        try
        {
            reply = await WorkflowRunViewModel.Retrying(
                () => sendBack is null ? coordinator.Approve(address, response, operation) : coordinator.SendBack(address, response, sendBack, operation),
                outcome => outcome is GateReply.Refused refused && WorkflowRunText.Transient(refused.Reason));
        }
        finally
        {
            _busy = false;
        }

        if (_disposed)
        {
            return;
        }

        Notice = reply switch
        {
            GateReply.Recorded => null,
            GateReply.Stale { Current: not null } => "This request changed since you saw it. Check the new request, then answer it.",
            GateReply.Stale => "This request changed since you saw it, and the node waits for its inputs again.",
            GateReply.Unavailable unavailable => unavailable.Message,
            GateReply.Refused refused => WorkflowRunText.Problem(refused.Reason),
            _ => null,
        };
        if (reply is GateReply.Recorded && sendBack is not null)
        {
            Reason = "";
        }

        // The run's next decision shows the answer. A refusal leaves the request as it was, so the person can try again.
        OnPropertyChanged(nameof(CanAnswer));
        Refresh();
    }
}
