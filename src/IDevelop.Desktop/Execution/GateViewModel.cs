using System.Windows.Input;
using IDevelop.Desktop.Mvvm;
using IDevelop.Execution;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Execution;

/// <summary>
/// An Approval node's request in the run, as the inspector shows it: what it hands on, Approve, and Send back with a reason,
/// and once sent back, Ask again after a confirmation. Each answer names the request and inputs the person saw, so an answer
/// to a request that changed meanwhile records nothing. A busy journal or lock is tried again with the same operation, which
/// records one answer at most.
/// </summary>
public sealed class GateViewModel : ObservableObject, IDisposable
{
    private readonly WorkflowRunViewModel _run;
    private readonly RelayCommand _approve;
    private readonly RelayCommand _sendBack;
    private readonly RelayCommand _askAgain;
    private readonly RelayCommand _confirmAskAgain;
    private readonly RelayCommand _keepAskAgain;
    private readonly OperationId _asking = new(Guid.NewGuid());
    private readonly OperationId _approval = new(Guid.NewGuid());
    private readonly Dictionary<string, OperationId> _sendBacks = [];
    private GateView _gate;
    private IReadOnlyList<GateInputViewModel>? _inputs;
    private string _reason = "";
    private string? _notice;
    private bool _busy;
    private bool _confirmingAskAgain;
    private bool _asked;
    private bool _disposed;

    internal GateViewModel(WorkflowRunViewModel run, GateView gate)
    {
        _run = run;
        _gate = gate;
        _approve = new RelayCommand(() => _ = AnswerAsync(sendBack: null), () => CanAnswer);
        _sendBack = new RelayCommand(() => _ = AnswerAsync(Reason.Trim()), () => CanAnswer && !string.IsNullOrWhiteSpace(Reason));
        _askAgain = new RelayCommand(() => SetConfirmingAskAgain(true), () => ShowsAskAgainButton);
        _confirmAskAgain = new RelayCommand(() => _ = AskAgainAsync(), () => ConfirmingAskAgain && !_busy);
        _keepAskAgain = new RelayCommand(() => SetConfirmingAskAgain(false), () => ConfirmingAskAgain);
        run.PropertyChanged += OnRunChanged;
        _ = LoadInputsAsync();
    }

    /// <summary>"Waiting for approval", "Approved", "Sent back", or "Closed".</summary>
    public string StatusLabel => _gate.Label;

    /// <summary>"1 of this run", which counts the node's requests in its run.</summary>
    public string RequestLabel => $"{_gate.Request.Sequence} of this run";

    /// <summary>What approving hands on, input by input, once it is read: each input's report, or a review's conclusion.</summary>
    public IReadOnlyList<GateInputViewModel>? Inputs
    {
        get => _inputs;
        private set
        {
            if (SetProperty(ref _inputs, value))
            {
                OnPropertyChanged(nameof(HasInputs));
            }
        }
    }

    public bool HasInputs => Inputs is { Count: > 0 };

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

    /// <summary>
    /// The person sent the request back and has not asked again, and this window controls the run, so Ask again can open a new
    /// request on the same inputs.
    /// </summary>
    public bool ShowsAskAgain => _gate.CanAskAgain && !_asked && _run.IsControlled;

    /// <summary>Ask again, until the person is asked to confirm it.</summary>
    public bool ShowsAskAgainButton => ShowsAskAgain && !_confirmingAskAgain;

    /// <summary>The inspector asks whether to ask again.</summary>
    public bool ConfirmingAskAgain => ShowsAskAgain && _confirmingAskAgain;

    /// <summary>What Ask again does, which the person confirms.</summary>
    public string AskAgainConfirmation =>
        "Ask again opens a new request on the same inputs. The sent-back request and its reason stay in the run's history.";

    /// <summary>Asks for the confirmation of Ask again.</summary>
    public ICommand AskAgainCommand => _askAgain;

    /// <summary>Opens a new request on the same inputs, as confirmed.</summary>
    public ICommand ConfirmAskAgainCommand => _confirmAskAgain;

    /// <summary>Closes the confirmation, and nothing moves.</summary>
    public ICommand KeepAskAgainCommand => _keepAskAgain;

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
        if (!ShowsAskAgain)
        {
            _confirmingAskAgain = false;
        }

        foreach (var property in new[] { nameof(StatusLabel), nameof(SentBackReason), nameof(IsWaiting), nameof(CanAnswer) })
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

    private void SetConfirmingAskAgain(bool confirming)
    {
        _confirmingAskAgain = confirming;
        Refresh();
    }

    private void Refresh()
    {
        foreach (var property in new[] { nameof(ShowsAskAgain), nameof(ShowsAskAgainButton), nameof(ConfirmingAskAgain) })
        {
            OnPropertyChanged(property);
        }

        _approve.NotifyCanExecuteChanged();
        _sendBack.NotifyCanExecuteChanged();
        _askAgain.NotifyCanExecuteChanged();
        _confirmAskAgain.NotifyCanExecuteChanged();
        _keepAskAgain.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Records Ask again for the request the person saw. One operation per request, so a repeat, or a try after a busy
    /// refusal, records it once; the run's next decision opens the new request.
    /// </summary>
    private async Task AskAgainAsync()
    {
        var coordinator = _run.Coordinator;
        var address = _run.Address;
        var request = _gate.Request.Id;
        _busy = true;
        Notice = null;
        Refresh();
        GateReply reply;
        try
        {
            reply = await WorkflowRunViewModel.Retrying(() => coordinator.AskAgain(address, request, _asking),
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

        if (reply is GateReply.Recorded)
        {
            // The run's next view shows the request asked again; until then, nothing offers it twice.
            _asked = true;
            _confirmingAskAgain = false;
        }

        Notice = reply switch
        {
            GateReply.Recorded => null,
            GateReply.Stale => "This request changed since you saw it, so asking again recorded nothing.",
            GateReply.Unavailable unavailable => unavailable.Message,
            GateReply.Refused refused => WorkflowRunText.Problem(refused.Reason),
            _ => null,
        };
        OnPropertyChanged(nameof(CanAnswer));
        Refresh();
    }

    private async Task LoadInputsAsync()
    {
        var coordinator = _run.Coordinator;
        var request = _gate.Request.Id;
        var inputs = await Task.Run(() => coordinator.GateInputs(request));
        if (!_disposed && inputs is { } read)
        {
            Inputs = [.. read.Select(input => new GateInputViewModel(input))];
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
