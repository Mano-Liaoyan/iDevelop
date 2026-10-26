using System.Windows.Input;
using Avalonia;
using IDevelop.Clients;
using IDevelop.Desktop.Execution;
using IDevelop.Desktop.Mvvm;
using IDevelop.Workflows;
using JetBrains.Annotations;

namespace IDevelop.Desktop.Canvas;

// A picker's UI Automation value is its chosen entry's text, so each entry's text is its label. The inspector reuses
// its pickers for every task, and a picker keeps its selected entry when the new list holds an equal one, so an entry
// names its task.

/// <summary>An entry in the inspector's client picker. A null client is "None".</summary>
public sealed record ClientChoice([UsedImplicitly] TaskId Task, ClientId? Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>An entry in the model or reasoning picker: the client's own id and what the picker shows.</summary>
public sealed record Choice([UsedImplicitly] TaskId Task, string Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// A node on the canvas. The card, the canvas actions, and the inspector each extend it in a file of their own and react
/// to its changes through its own <see cref="ObservableObject.PropertyChanged"/>.
/// </summary>
public sealed partial class TaskNodeViewModel : ObservableObject
{
    // A new list clears its picker's selection, so each list is raised before its selection.
    private static readonly string[] PickerProperties =
    [
        nameof(ClientChoices), nameof(SelectedClient), nameof(HasClient), nameof(ModelChoices), nameof(SelectedModel),
        nameof(ReasoningChoices), nameof(SelectedReasoning), nameof(HasReasoning), nameof(PermissionNote),
    ];

    private readonly WorkflowCanvasViewModel _canvas;
    private TaskDefinition _task;
    private Point _location;
    private ProposalViewModel? _proposal;
    private NodeKind _kind;
    private bool _isLibrary;

    internal TaskNodeViewModel(WorkflowCanvasViewModel canvas, TaskDefinition task, CanvasPoint position)
    {
        _canvas = canvas;
        _task = task;
        _location = WorkflowCanvasViewModel.ToPoint(position);
        Fields = [.. task.Blueprint.Fields.Select(field => new FieldViewModel(this, field))];
        Input = new PortViewModel(this, PortSide.Input);
        Output = new PortViewModel(this, PortSide.Output);
        Skills = new SkillPickerViewModel(this);
        DeriveCommand = new RelayCommand(() => _canvas.Blueprints.Derive(_task.Blueprint));
        SaveAsBlueprintCommand = new RelayCommand(() => _canvas.Blueprints.SaveAs(_task));
        (_kind, _isLibrary) = (canvas.KindOf(task.Blueprint), !NodeKinds.IsBuiltIn(task.Blueprint));
        InitializeRun();
        InitializeCard();
        InitializeInspector();
        InitializeConversation();
        InitializeExpansion();
    }

    public TaskId Id => _task.Id;

    /// <summary>The canvas the node is on, whose clients and project the node's pickers read.</summary>
    internal WorkflowCanvasViewModel Canvas => _canvas;

    /// <summary>The task as the workflow document holds it now.</summary>
    internal TaskDefinition Definition => _task;

    public NodeKind Kind
    {
        get => _kind;
        private set => SetProperty(ref _kind, value);
    }

    /// <summary>The node's blueprint comes from a project or personal library, not from the built-ins.</summary>
    public bool IsLibrary
    {
        get => _isLibrary;
        private set => SetProperty(ref _isLibrary, value);
    }

    public NodeState State
    {
        get;
        private set => SetProperty(ref field, value);
    }

    public NodeRole Role
    {
        get;
        private set => SetProperty(ref field, value);
    }

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

    /// <summary>The card previews the first field, or a review's round and open findings once it ran.</summary>
    public string Preview => ReviewSummary ?? (Fields.Count > 0 ? Fields[0].Text : "");

    public bool IsReview => _task.Blueprint.Work is WorkSpec.Review;

    /// <summary>An approval has no agent.</summary>
    public bool HasAgent => _task.Blueprint.Work is not WorkSpec.Person;

    /// <summary>Only an agent work waits for the person as its conversation mode says.</summary>
    public bool HasConversation => _task.Blueprint.Work is WorkSpec.Agent;

    public string ComposerHint => IsReview
        ? "The reviewer reads your guidance in its next turn. Ctrl+Enter sends."
        : "Write to the agent. Ctrl+Enter sends.";

    public IReadOnlyList<Choice> ConversationChoices =>
        [.. Enum.GetValues<ConversationMode>().Select(mode => new Choice(Id, mode.ToString(), RunText.ConversationChoice(mode)))];

    public Choice SelectedConversation => ConversationChoices.First(choice => choice.Id == _task.Conversation.ToString());

    public string ConversationNote => RunText.ConversationNote(_task.Conversation);

    public string AgentLabel => RunText.AgentLabel(_task.Execution, Status);

    /// <summary>The card's first agent line: the client, or "No agent".</summary>
    public string AgentClient => RunText.AgentLines(_task.Execution, Status).Client;

    /// <summary>The card's second line in every state: the model and its level, or empty while the task names neither.</summary>
    public string AgentModel => RunText.AgentLines(_task.Execution, Status).Model ?? "";

    public IReadOnlyList<ClientChoice> ClientChoices
    {
        get => Kept(ref field,
            [new(Id, null, "None"), .. ClientCatalog.All.Select(id => new ClientChoice(Id, id, RunText.ClientChoice(id, _canvas.Clients.Current[id])))]);
    } = [];

    public ClientChoice SelectedClient => ClientChoices.First(choice => choice.Id == _task.Execution?.Client);

    public bool HasClient => _task.Execution is not null;

    /// <summary>The models this machine offers, then the task's model when it is not one of them, marked.</summary>
    public IReadOnlyList<Choice> ModelChoices
    {
        get => Kept(ref field, _task.Execution is { } settings
            ? [.. ExecutionChoices.Models(settings, Status).Select(choice => new Choice(Id, choice.Model.Id, RunText.ModelChoice(choice.Model, choice.Offered, Status)))]
            : []);
    } = [];

    public Choice? SelectedModel => ModelChoices.FirstOrDefault(choice => choice.Id == _task.Execution?.Model);

    /// <summary>The levels the task's model offers, then the task's level when the model does not offer it, marked.</summary>
    public IReadOnlyList<Choice> ReasoningChoices
    {
        get => Kept(ref field, _task.Execution is { } settings
            ? [.. ExecutionChoices.Reasoning(settings, Status).Select(choice => new Choice(Id, choice.Level, RunText.ReasoningChoice(choice.Level, choice.Offered)))]
            : []);
    } = [];

    public Choice? SelectedReasoning => ReasoningChoices.FirstOrDefault(choice => choice.Id == _task.Execution?.Reasoning);

    /// <summary>False when the model takes no reasoning level, such as an Antigravity model without a level suffix.</summary>
    public bool HasReasoning => ReasoningChoices.Count > 0;

    /// <summary>The skills the node's agent may use (#93), which the inspector, and any other view of the node, picks.</summary>
    public SkillPickerViewModel Skills { get; }

    public string? PermissionNote => _task.Execution is { } settings
        ? IsReview ? RunText.ReviewerNote(settings.Client) : RunText.PermissionNote(settings.Client, _task.Blueprint.Work is WorkSpec.Agent { Access: AgentAccess.ReadOnly })
        : null;

    /// <summary>The planner's latest proposal while it is open, or null.</summary>
    public ProposalViewModel? Proposal
    {
        get => _proposal;
        private set
        {
            if (SetProperty(ref _proposal, value))
            {
                ShowState();
            }
        }
    }

    /// <summary>
    /// The message the person is writing to the task's agent. Each task keeps its own, which the conversation view's
    /// composer shares.
    /// </summary>
    public string Draft
    {
        get => ConversationState.Draft;
        set => ConversationState.Draft = value;
    }

    private ClientStatus Status => _task.Execution is { } settings ? _canvas.Clients.Current[settings.Client] : new ClientStatus.Checking();

    internal void Update(TaskDefinition task, CanvasPoint position)
    {
        if (!ReferenceEquals(task, _task))
        {
            var old = _task;
            _task = task;
            Kind = _canvas.KindOf(task.Blueprint);
            IsLibrary = !NodeKinds.IsBuiltIn(task.Blueprint);
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
        }

        Location = WorkflowCanvasViewModel.ToPoint(position);
    }

    // A picker whose list is replaced rebuilds its entries, which can swallow a click or key the person gives it at that
    // moment, so a list that came out the same, as after a skill listing lands, stays the very list the picker shows.
    private static IReadOnlyList<T> Kept<T>(ref IReadOnlyList<T> shown, IReadOnlyList<T> now) => now.SequenceEqual(shown) ? shown : shown = now;

    /// <summary>The task's agent changed, or what the clients offer did.</summary>
    internal void OnAgentChanged()
    {
        OnPropertyChanged(nameof(AgentLabel));
        foreach (var property in PickerProperties)
        {
            OnPropertyChanged(property);
        }

        Skills.Refresh();
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

    partial void InitializeRun();

    partial void InitializeCard();

    partial void InitializeInspector();

    partial void InitializeConversation();

    partial void InitializeExpansion();

    /// <summary>Keeps the agent with another skill choice, as the node's skill picker makes it.</summary>
    internal void SetSkills(ExecutionSettings settings) => SetExecution(settings);

    private void SetExecution(ExecutionSettings? settings)
    {
        if (_canvas.Edit(new WorkflowEdit.SetExecution(Id, settings)) is EditResult.Rejected)
        {
            OnAgentChanged();
        }
    }
}
