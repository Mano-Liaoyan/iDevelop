using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia;
using Avalonia.Threading;
using IDevelop.Desktop.Execution;
using IDevelop.Desktop.Mvvm;
using IDevelop.Execution;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

/// <summary>An entry in the inspector's client picker. A null client is "None".</summary>
public sealed record ClientChoice(ClientId? Id, string Label);

/// <summary>An entry in the model or reasoning picker: the client's own id and what the picker shows.</summary>
public sealed record Choice(string Id, string Label);

public sealed class TaskNodeViewModel : ObservableObject
{
    // A new list clears its picker's selection, so each list is raised before its selection, and a picker's null is
    // never an edit.
    private static readonly string[] PickerProperties =
    [
        nameof(ClientChoices), nameof(SelectedClient), nameof(HasClient), nameof(ModelChoices), nameof(SelectedModel),
        nameof(ReasoningChoices), nameof(SelectedReasoning), nameof(HasReasoning), nameof(PermissionNote),
    ];

    private readonly WorkflowCanvasViewModel _canvas;
    private readonly RelayCommand _run;
    private readonly RelayCommand _cancel;
    private TaskDefinition _task;
    private Point _location;
    private AttemptRecord? _attempt;

    internal TaskNodeViewModel(WorkflowCanvasViewModel canvas, TaskDefinition task, CanvasPoint position)
    {
        _canvas = canvas;
        _task = task;
        _location = WorkflowCanvasViewModel.ToPoint(position);
        _attempt = canvas.Runs.Latest.GetValueOrDefault(task.Id);
        Input = new PortViewModel(this, PortSide.Input);
        Output = new PortViewModel(this, PortSide.Output);
        _run = new RelayCommand(Run, () => !IsRunning);
        _cancel = new RelayCommand(() => _canvas.Runs.Cancel(Id), () => _attempt is { Status: AttemptStatus.Running, Stopping: false });
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
        [new(null, "None"), .. Clients.All.Select(id => new ClientChoice(id, RunText.ClientChoice(id, _canvas.Clients.Current[id])))];

    public ClientChoice? SelectedClient
    {
        get => ClientChoices.First(choice => choice.Id == _task.Execution?.Client);
        set
        {
            if (value is not null && value.Id != _task.Execution?.Client)
            {
                SetExecution(value.Id is { } id ? ExecutionChoices.ForClient(id, _canvas.Clients.Current[id]) : null);
            }
        }
    }

    public bool HasClient => _task.Execution is not null;

    /// <summary>The models this machine offers, then the task's model when it is not one of them, marked.</summary>
    public IReadOnlyList<Choice> ModelChoices => _task.Execution is { } settings
        ? [.. ExecutionChoices.Models(settings, Status).Select(choice => new Choice(choice.Model.Id, RunText.ModelChoice(choice.Model, choice.Offered)))]
        : [];

    public Choice? SelectedModel
    {
        get => ModelChoices.FirstOrDefault(choice => choice.Id == _task.Execution?.Model);
        set
        {
            if (value is not null && _task.Execution is { } settings && value.Id != settings.Model
                && RunText.Offered(Status).FirstOrDefault(model => model.Id == value.Id) is { } model)
            {
                SetExecution(ExecutionChoices.ForModel(settings, model));
            }
        }
    }

    /// <summary>The levels the task's model offers, then the task's level when the model does not offer it, marked.</summary>
    public IReadOnlyList<Choice> ReasoningChoices
    {
        get
        {
            if (_task.Execution is not { } settings)
            {
                return [];
            }

            var model = RunText.Offered(Status).FirstOrDefault(option => option.Id == settings.Model);
            List<Choice> choices = [.. (model?.ReasoningLevels ?? []).Select(level => new Choice(level, level))];
            if (settings.Reasoning is { } stored && !choices.Any(choice => choice.Id == stored))
            {
                choices.Add(new Choice(stored, model is null ? stored : $"{stored} (not offered)"));
            }

            return choices;
        }
    }

    public Choice? SelectedReasoning
    {
        get => ReasoningChoices.FirstOrDefault(choice => choice.Id == _task.Execution?.Reasoning);
        set
        {
            if (value is not null && _task.Execution is { } settings && value.Id != settings.Reasoning
                && RunText.Offered(Status).FirstOrDefault(model => model.Id == settings.Model) is { } model && model.ReasoningLevels.Contains(value.Id))
            {
                SetExecution(settings with { Reasoning = value.Id });
            }
        }
    }

    /// <summary>False when the model takes no reasoning level, such as an Antigravity model without a level suffix.</summary>
    public bool HasReasoning => ReasoningChoices.Count > 0;

    public string? PermissionNote => _task.Execution is { } settings ? RunText.PermissionNote(settings.Client) : null;

    public string StatusLabel => RunText.StatusLabel(_attempt);

    /// <summary>The card and its status pill take it as a style class.</summary>
    public StatusTone Tone => RunText.Tone(_attempt);

    public bool IsRunning => _attempt is { Status: AttemptStatus.Running };

    /// <summary>Why this task cannot start now, shown under the Run button before any click.</summary>
    public string? StartProblem => !IsRunning && _canvas.Runs.Check(_task) is { } problem ? RunText.Describe(problem) : null;

    public AttemptViewModel? LastAttempt => _attempt is null ? null : new AttemptViewModel(_attempt);

    /// <summary>Enabled for a task that is not running. A task that cannot start shows why instead of launching.</summary>
    public ICommand RunCommand => _run;

    public ICommand CancelCommand => _cancel;

    private ClientStatus Status => _task.Execution is { } settings ? _canvas.Clients.Current[settings.Client] : new ClientStatus.Checking();

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
        }

        Location = WorkflowCanvasViewModel.ToPoint(position);
    }

    /// <summary>Called on the UI thread with each new state of this task's attempt.</summary>
    internal void ShowAttempt(AttemptRecord attempt)
    {
        _attempt = attempt;
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(Tone));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(LastAttempt));
        _run.NotifyCanExecuteChanged();
        _cancel.NotifyCanExecuteChanged();
    }

    /// <summary>A run started or ended somewhere in the project, which can keep this task from starting.</summary>
    internal void OnRunsChanged() => OnPropertyChanged(nameof(StartProblem));

    /// <summary>The task's agent changed, or what the clients offer did.</summary>
    internal void OnAgentChanged()
    {
        OnPropertyChanged(nameof(AgentLabel));
        OnPropertyChanged(nameof(StartProblem));
        // A picker whose choice made this edit is still committing it. A new list now would make Avalonia's selection
        // model restore the choice before it, and write that back as another edit, so the pickers update after.
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var property in PickerProperties)
            {
                OnPropertyChanged(property);
            }
        });
    }

    private void Run() =>
        _canvas.Notice(_canvas.Runs.Start(_task) is StartResult.Refused refused ? RunText.Describe(refused.Problem) : null);

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
