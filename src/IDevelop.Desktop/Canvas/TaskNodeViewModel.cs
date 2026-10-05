using System.Collections.Immutable;
using System.Windows.Input;
using Avalonia;
using IDevelop.Desktop.Execution;
using IDevelop.Desktop.Mvvm;
using IDevelop.Execution;
using IDevelop.Nodes;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

// A picker's UI Automation value is its chosen entry's text, so each entry's text is its label. The inspector reuses
// its pickers for every task, and a picker keeps its selected entry when the new list holds an equal one, so an entry
// names its task.

/// <summary>An entry in the inspector's client picker. A null client is "None".</summary>
public sealed record ClientChoice(TaskId Task, ClientId? Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>An entry in the model or reasoning picker: the client's own id and what the picker shows.</summary>
public sealed record Choice(TaskId Task, string Id, string Label)
{
    public override string ToString() => Label;
}

public sealed class TaskNodeViewModel : ObservableObject
{
    // A new list clears its picker's selection, so each list is raised before its selection.
    private static readonly string[] PickerProperties =
    [
        nameof(ClientChoices), nameof(SelectedClient), nameof(HasClient), nameof(ModelChoices), nameof(SelectedModel),
        nameof(ReasoningChoices), nameof(SelectedReasoning), nameof(HasReasoning), nameof(PermissionNote),
    ];

    private readonly WorkflowCanvasViewModel _canvas;
    private readonly RelayCommand _run;
    private readonly RelayCommand _cancel;
    private readonly RelayCommand _send;
    private readonly RelayCommand _stopAndSend;
    private readonly RelayCommand _openInTerminal;
    private readonly RelayCommand _markDone;
    private TaskDefinition _task;
    private Point _location;
    private AttemptRecord? _attempt;
    private (AttemptId? Continues, ImmutableArray<AttemptRecord> Attempts) _earlier = (null, []);
    private string _draft = "";
    private AttemptRecord? _proposed;
    private ProposalRead _proposalRead = new ProposalRead.None();
    private ProposalViewModel? _proposal;

    internal TaskNodeViewModel(WorkflowCanvasViewModel canvas, TaskDefinition task, CanvasPoint position)
    {
        _canvas = canvas;
        _task = task;
        _location = WorkflowCanvasViewModel.ToPoint(position);
        _attempt = canvas.Runs.Latest.GetValueOrDefault(task.Id);
        Fields = [.. task.Blueprint.Fields.Select(field => new FieldViewModel(this, field))];
        Input = new PortViewModel(this, PortSide.Input);
        Output = new PortViewModel(this, PortSide.Output);
        _run = new RelayCommand(Run, () => !RunsHere);
        _cancel = new RelayCommand(
            () => _canvas.Notice(_canvas.Runs.Cancel(Id) is { } problem ? RunText.Describe(problem) : null),
            () => IsWaiting || _attempt is { Status: AttemptStatus.Running, Stopping: false } attempt && _canvas.Runs.Active.Any(run => run.Id == attempt.Id));
        _send = new RelayCommand(() => Send(stopTurn: false), () => !string.IsNullOrWhiteSpace(_draft) && _canvas.Runs.CheckSend(_task) is null);
        _stopAndSend = new RelayCommand(() => Send(stopTurn: true), () => TurnRunsHere && _send.CanExecute(null));
        _openInTerminal = new RelayCommand(OpenInTerminal, () => _attempt is { Status: AttemptStatus.WaitingForInput, SessionId: not null });
        _markDone = new RelayCommand(MarkDone, () => IsWaiting);
        DeriveCommand = new RelayCommand(() => _canvas.Blueprints.Derive(_task.Blueprint));
        SaveAsBlueprintCommand = new RelayCommand(() => _canvas.Blueprints.SaveAs(_task));
    }

    public TaskId Id => _task.Id;

    public PortViewModel Input { get; }

    public PortViewModel Output { get; }

    public Point Location
    {
        get => _location;
        set => SetProperty(ref _location, value);
    }

    public string Title
    {
        get => _task.Title;
        set
        {
            if (_canvas.Edit(new WorkflowEdit.EditTitle(Id, value)) is EditResult.Rejected)
            {
                OnPropertyChanged();
            }
        }
    }

    /// <summary>The name of the task's blueprint, such as Implement.</summary>
    public string TypeName => _task.Blueprint.Name;

    /// <summary>The version the node was placed from, which it keeps whatever its library does later.</summary>
    public string TypeVersion => $"{(_task.Blueprint.IsBuiltIn ? "Built-in, version" : "Version")} {_task.Blueprint.Key.Version}";

    /// <summary>Opens the blueprint editor on a new blueprint with this node's structure.</summary>
    public ICommand DeriveCommand { get; }

    /// <summary>Opens the blueprint editor on a new blueprint whose defaults are this node's values and settings.</summary>
    public ICommand SaveAsBlueprintCommand { get; }

    /// <summary>The blueprint's fields, in its order.</summary>
    public IReadOnlyList<FieldViewModel> Fields { get; }

    /// <summary>The card previews the first field.</summary>
    public string Preview => Fields.Count > 0 ? Fields[0].Text : "";

    public string PreviewPlaceholder => Fields.Count > 0 ? $"No {Fields[0].Label.ToLowerInvariant()} yet." : "";

    public IReadOnlyList<Choice> ConversationChoices =>
        [.. Enum.GetValues<ConversationMode>().Select(mode => new Choice(Id, mode.ToString(), RunText.ConversationChoice(mode)))];

    public Choice SelectedConversation => ConversationChoices.First(choice => choice.Id == _task.Conversation.ToString());

    public string ConversationNote => RunText.ConversationNote(_task.Conversation);

    /// <summary>The task's latest attempt waits for the person.</summary>
    public bool IsWaiting => _attempt is { Status: AttemptStatus.WaitingForInput };

    /// <summary>The question or the reason the task waits for the person, or null.</summary>
    public string? Waiting => _attempt is { Status: AttemptStatus.WaitingForInput, Pending: { } pending } ? RunText.Waiting(pending) : null;

    /// <summary>Records the waiting task as done, with the agent's last reply as its result.</summary>
    public ICommand MarkDoneCommand => _markDone;

    public string AgentLabel => RunText.AgentLabel(_task.Execution, Status);

    public IReadOnlyList<ClientChoice> ClientChoices =>
        [new(Id, null, "None"), .. Clients.All.Select(id => new ClientChoice(Id, id, RunText.ClientChoice(id, _canvas.Clients.Current[id])))];

    public ClientChoice SelectedClient => ClientChoices.First(choice => choice.Id == _task.Execution?.Client);

    public bool HasClient => _task.Execution is not null;

    /// <summary>The models this machine offers, then the task's model when it is not one of them, marked.</summary>
    public IReadOnlyList<Choice> ModelChoices => _task.Execution is { } settings
        ? [.. ExecutionChoices.Models(settings, Status).Select(choice => new Choice(Id, choice.Model.Id, RunText.ModelChoice(choice.Model, choice.Offered, Status)))]
        : [];

    public Choice? SelectedModel => ModelChoices.FirstOrDefault(choice => choice.Id == _task.Execution?.Model);

    /// <summary>The levels the task's model offers, then the task's level when the model does not offer it, marked.</summary>
    public IReadOnlyList<Choice> ReasoningChoices => _task.Execution is { } settings
        ? [.. ExecutionChoices.Reasoning(settings, Status).Select(choice => new Choice(Id, choice.Level, RunText.ReasoningChoice(choice.Level, choice.Offered)))]
        : [];

    public Choice? SelectedReasoning => ReasoningChoices.FirstOrDefault(choice => choice.Id == _task.Execution?.Reasoning);

    /// <summary>False when the model takes no reasoning level, such as an Antigravity model without a level suffix.</summary>
    public bool HasReasoning => ReasoningChoices.Count > 0;

    public string? PermissionNote => _task.Execution is { } settings
        ? RunText.PermissionNote(settings.Client, _task.Blueprint.Work is WorkSpec.Agent { Access: AgentAccess.ReadOnly })
        : null;

    public string StatusLabel => RunText.StatusLabel(_attempt, RunsElsewhere);

    /// <summary>The card and its status pill take it as a style class.</summary>
    public StatusTone Tone => RunText.Tone(_attempt);

    /// <summary>Why this task cannot start now, shown under the Run button before any click.</summary>
    public string? StartProblem => !RunsHere && _canvas.Runs.Check(_task) is { } problem ? RunText.Describe(problem) : null;

    /// <summary>Only the inspector shows it, so only the selected task reads the attempts that its last run continues.</summary>
    public AttemptViewModel? LastAttempt => _attempt is null ? null : new AttemptViewModel(_attempt, Earlier(_attempt), RunsElsewhere);

    /// <summary>
    /// Enabled unless this window runs the task. A task that cannot start shows why instead of launching, which is also
    /// how a task that another window runs, or ran, finds out.
    /// </summary>
    public ICommand RunCommand => _run;

    public ICommand CancelCommand => _cancel;

    /// <summary>The planner's latest proposal while it is open, or null.</summary>
    public ProposalViewModel? Proposal
    {
        get => _proposal;
        private set => SetProperty(ref _proposal, value);
    }

    /// <summary>The message the person is writing to the task's agent. Each task keeps its own.</summary>
    public string Draft
    {
        get => _draft;
        set
        {
            if (SetProperty(ref _draft, value))
            {
                _send.NotifyCanExecuteChanged();
                _stopAndSend.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Why a message cannot go to the task's agent now, shown under the composer before any click.</summary>
    public string? SendProblem => _canvas.Runs.CheckSend(_task) is { } problem ? RunText.Describe(problem) : null;

    /// <summary>A turn of the task runs in this window, which Stop and send can stop.</summary>
    public bool TurnRunsHere =>
        _attempt is { Status: AttemptStatus.Running, Turns: [.., { Outcome: TurnOutcome.Running }] } attempt && _canvas.Runs.StartedHere(attempt.Id);

    /// <summary>Queues the draft for the next turn, or continues the latest session in a new attempt.</summary>
    public ICommand SendCommand => _send;

    public ICommand StopAndSendCommand => _stopAndSend;

    /// <summary>Copies the client's own command for the latest session, while no turn of the task runs.</summary>
    public ICommand OpenInTerminalCommand => _openInTerminal;

    private ClientStatus Status => _task.Execution is { } settings ? _canvas.Clients.Current[settings.Client] : new ClientStatus.Checking();

    private bool RunsHere => _attempt is { Status: AttemptStatus.Running } attempt && _canvas.Runs.StartedHere(attempt.Id);

    private bool RunsElsewhere => _attempt is { Status: AttemptStatus.Running } && !RunsHere;

    /// <summary>
    /// The attempts that <paramref name="attempt"/> continues. Their logs are read again only when it continues another
    /// attempt than before, because a continued attempt never changes.
    /// </summary>
    private ImmutableArray<AttemptRecord> Earlier(AttemptRecord attempt)
    {
        var continues = attempt.Continues;
        if (continues != _earlier.Continues)
        {
            _earlier = (continues, continues is null ? [] : _canvas.Runs.EarlierAttempts(attempt));
        }

        return _earlier.Attempts;
    }

    internal void Update(TaskDefinition task, CanvasPoint position)
    {
        if (!ReferenceEquals(task, _task))
        {
            var old = _task;
            _task = task;
            if (old.Title != task.Title)
            {
                OnPropertyChanged(nameof(Title));
            }

            if (!ReferenceEquals(old.Fields, task.Fields))
            {
                foreach (var field in Fields.Where(field => old.Field(field.Spec.Key) != task.Field(field.Spec.Key)))
                {
                    field.Refresh();
                }

                OnPropertyChanged(nameof(Preview));
            }

            if (old.Conversation != task.Conversation)
            {
                OnPropertyChanged(nameof(ConversationChoices));
                OnPropertyChanged(nameof(SelectedConversation));
                OnPropertyChanged(nameof(ConversationNote));
            }

            if (old.Execution != task.Execution)
            {
                OnAgentChanged();
            }

            OnPropertyChanged(nameof(StartProblem));
            OnConversationChanged();
        }

        Location = WorkflowCanvasViewModel.ToPoint(position);
    }

    /// <summary>
    /// Called on the UI thread with the task's newest attempt after any attempt of the project changes. A run anywhere in
    /// the project can keep this task from starting.
    /// </summary>
    internal void ShowAttempt(AttemptRecord? attempt)
    {
        if (!ReferenceEquals(attempt, _attempt))
        {
            _attempt = attempt;
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(Tone));
            OnPropertyChanged(nameof(LastAttempt));
            OnPropertyChanged(nameof(IsWaiting));
            OnPropertyChanged(nameof(Waiting));
            ShowProposal();
        }

        OnPropertyChanged(nameof(StartProblem));
        _run.NotifyCanExecuteChanged();
        _cancel.NotifyCanExecuteChanged();
        _markDone.NotifyCanExecuteChanged();
        OnConversationChanged();
    }

    /// <summary>
    /// Shows the latest proposal of the task's session, unless it is closed: from its latest attempt, or else from the
    /// attempts that one continues, newest first. The attempt is read again only when it changed, and an open proposal
    /// keeps the person's choices and checks them against the workflow again.
    /// </summary>
    internal void ShowProposal()
    {
        if (!ReferenceEquals(_proposed, _attempt))
        {
            _proposed = _attempt;
            _proposalRead = _attempt is null ? new ProposalRead.None()
                : Earlier(_attempt).Reverse().Prepend(_attempt)
                    .Select(attempt => Nodes.Proposal.Read(attempt, _canvas.FindBlueprint))
                    .FirstOrDefault(read => read is not ProposalRead.None) ?? new ProposalRead.None();
        }

        var shown = _proposalRead is ProposalRead.None ? null
            : _proposal is { } current && current.Identity.Equals(ProposalViewModel.IdentityOf(_proposalRead, _proposed!.Id)) ? current
            : new ProposalViewModel(_canvas, _proposalRead, _proposed!.Id);
        if (shown is not null && _canvas.IsClosed(shown))
        {
            shown = null;
        }

        shown?.Refresh();
        Proposal = shown;
    }

    /// <summary>The task's agent changed, or what the clients offer did.</summary>
    internal void OnAgentChanged()
    {
        OnPropertyChanged(nameof(AgentLabel));
        OnPropertyChanged(nameof(StartProblem));
        foreach (var property in PickerProperties)
        {
            OnPropertyChanged(property);
        }

        OnConversationChanged();
    }

    internal void ChooseClient(ClientChoice choice)
    {
        if (choice.Id != _task.Execution?.Client)
        {
            SetExecution(choice.Id is { } id ? ExecutionChoices.ForClient(id, _canvas.Clients.Current[id]) : null);
        }
    }

    internal void ChooseModel(Choice choice)
    {
        if (_task.Execution is { } settings && choice.Id != settings.Model
            && ExecutionChoices.OfferedModel(Status, choice.Id) is { } model)
        {
            SetExecution(ExecutionChoices.ForModel(settings, model));
        }
    }

    internal void ChooseReasoning(Choice choice)
    {
        if (_task.Execution is { } settings && choice.Id != settings.Reasoning
            && ExecutionChoices.OfferedModel(Status, settings.Model) is { } model && model.ReasoningLevels.Contains(choice.Id))
        {
            SetExecution(settings with { Reasoning = choice.Id });
        }
    }

    internal void ChooseConversation(Choice choice)
    {
        if (Enum.TryParse<ConversationMode>(choice.Id, out var mode) && mode != _task.Conversation)
        {
            if (_canvas.Edit(new WorkflowEdit.SetConversation(Id, mode)) is EditResult.Rejected)
            {
                OnPropertyChanged(nameof(SelectedConversation));
            }
        }
    }

    internal string FieldText(string key) => _task.Field(key);

    internal void SetField(string key, string text)
    {
        if (_canvas.Edit(new WorkflowEdit.SetField(Id, key, text)) is EditResult.Rejected)
        {
            Fields.First(field => field.Spec.Key == key).Refresh();
        }
    }

    private void MarkDone() =>
        _canvas.Notice(_canvas.Runs.MarkDone(Id) is { } problem ? RunText.Describe(problem) : null);

    private void Run() =>
        _canvas.Notice(_canvas.Runs.Start(_task, _canvas.Planning(Id)) is StartResult.Refused refused ? RunText.Describe(refused.Problem) : null);

    private void Send(bool stopTurn)
    {
        if (_canvas.Runs.Send(_task, _draft, stopTurn) is SendResult.Refused refused)
        {
            _canvas.Notice(RunText.Describe(refused.Problem));
            return;
        }

        Draft = "";
        _canvas.Notice(null);
    }

    /// <summary>The hand-off is recorded either way, because the notice shows the command and the folder.</summary>
    private async void OpenInTerminal()
    {
        switch (_canvas.Runs.OpenInTerminal(Id))
        {
            case TerminalResult.HandedOff handedOff:
                _canvas.Notice(await Copied(handedOff.Command) ? RunText.HandedOff(handedOff) : RunText.NotCopied(handedOff));
                break;
            case TerminalResult.Refused refused:
                _canvas.Notice(RunText.Describe(refused.Problem));
                break;
        }
    }

    /// <summary>The platform's clipboard can fail, such as while another program holds it on Windows.</summary>
    private async Task<bool> Copied(string text)
    {
        try
        {
            await _canvas.Copy(text);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void OnConversationChanged()
    {
        OnPropertyChanged(nameof(SendProblem));
        OnPropertyChanged(nameof(TurnRunsHere));
        _send.NotifyCanExecuteChanged();
        _stopAndSend.NotifyCanExecuteChanged();
        _openInTerminal.NotifyCanExecuteChanged();
    }

    private void SetExecution(ExecutionSettings? settings)
    {
        if (_canvas.Edit(new WorkflowEdit.SetExecution(Id, settings)) is EditResult.Rejected)
        {
            OnAgentChanged();
        }
    }
}
