using System.Runtime.CompilerServices;
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
    private TaskDefinition _task;
    private Point _location;

    internal TaskNodeViewModel(WorkflowCanvasViewModel canvas, TaskDefinition task, CanvasPoint position)
    {
        _canvas = canvas;
        _task = task;
        _location = WorkflowCanvasViewModel.ToPoint(position);
        Input = new PortViewModel(this, PortSide.Input);
        Output = new PortViewModel(this, PortSide.Output);
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
        }

        Location = WorkflowCanvasViewModel.ToPoint(position);
    }

    /// <summary>The task's agent changed, or what the clients offer did.</summary>
    internal void OnAgentChanged()
    {
        OnPropertyChanged(nameof(AgentLabel));
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
