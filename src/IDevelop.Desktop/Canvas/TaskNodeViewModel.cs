using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia;
using IDevelop.Desktop.Execution;
using IDevelop.Desktop.Mvvm;
using IDevelop.Execution;
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
    private TaskDefinition _task;
    private Point _location;
    private AttemptRecord? _attempt;
    private (AttemptId? Continues, ImmutableArray<AttemptRecord> Attempts) _earlier = (null, []);
    private string _draft = "";

    internal TaskNodeViewModel(WorkflowCanvasViewModel canvas, TaskDefinition task, CanvasPoint position)
    {
        _canvas = canvas;
        _task = task;
        _location = WorkflowCanvasViewModel.ToPoint(position);
        _attempt = canvas.Runs.Latest.GetValueOrDefault(task.Id);
        Input = new PortViewModel(this, PortSide.Input);
        Output = new PortViewModel(this, PortSide.Output);
        _run = new RelayCommand(Run, () => !RunsHere);
        _cancel = new RelayCommand(
            () => _canvas.Runs.Cancel(Id),
            () => _attempt is { Status: AttemptStatus.Running, Stopping: false } attempt && _canvas.Runs.Active.Any(run => run.Id == attempt.Id));
        _send = new RelayCommand(() => Send(stopTurn: false), () => !string.IsNullOrWhiteSpace(_draft) && _canvas.Runs.CheckSend(_task) is null);
        _stopAndSend = new RelayCommand(() => Send(stopTurn: true), () => TurnRunsHere && _send.CanExecute(null));
        _openInTerminal = new RelayCommand(OpenInTerminal, () => _attempt is { Status: not AttemptStatus.Running, SessionId: not null });
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
        set => RequestEdit(TaskField.Title, value);
    }

    public string Instructions
    {
        get => _task.Instructions;
        set => RequestEdit(TaskField.Instructions, value);
    }

    public string AcceptanceCriteria
    {
        get => _task.AcceptanceCriteria;
        set => RequestEdit(TaskField.AcceptanceCriteria, value);
    }

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

    public string? PermissionNote => _task.Execution is { } settings ? RunText.PermissionNote(settings.Client) : null;

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
        var continues = attempt.Continues?.Attempt;
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

            if (old.Instructions != task.Instructions)
            {
                OnPropertyChanged(nameof(Instructions));
            }

            if (old.AcceptanceCriteria != task.AcceptanceCriteria)
            {
                OnPropertyChanged(nameof(AcceptanceCriteria));
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
        }

        OnPropertyChanged(nameof(StartProblem));
        _run.NotifyCanExecuteChanged();
        _cancel.NotifyCanExecuteChanged();
        OnConversationChanged();
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

    private void Run() =>
        _canvas.Notice(_canvas.Runs.Start(_task) is StartResult.Refused refused ? RunText.Describe(refused.Problem) : null);

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

    private void RequestEdit(TaskField field, string text, [CallerMemberName] string? property = null)
    {
        if (_canvas.Edit(new WorkflowEdit.EditTask(Id, field, text)) is EditResult.Rejected)
        {
            OnPropertyChanged(property);
        }
    }
}
