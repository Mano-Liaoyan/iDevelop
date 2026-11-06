using System.ComponentModel;
using System.Windows.Input;
using Avalonia;
using IDevelop.Clients;
using IDevelop.Desktop.Execution;
using IDevelop.Desktop.Mvvm;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// A node on the canvas. The card, the canvas actions, and the inspector each extend it in a file of their own. One table
/// here says which of its properties follow which.
/// </summary>
public sealed partial class TaskNodeViewModel : ObservableObject, IAgentPickerHost
{
    // Each computed property follows the node properties it reads, which the node raises on its own. The handler raises
    // each list in this order.
    private static readonly Dictionary<string, string[]> Dependents = new()
    {
        [nameof(State)] = [nameof(CardStatus), nameof(ShowsNextRunNote)],
        [nameof(Role)] = [nameof(CardStatus), nameof(IsUnderReview)],
        [nameof(StatusLabel)] = [nameof(CardStatus)],
        [nameof(Proposal)] = [nameof(CardStatus), nameof(ShowsAcceptAndFinish), nameof(Actions)],
        [nameof(EngineState)] = [nameof(ShowsAcceptAndFinish), nameof(Attention), nameof(HasAttention), nameof(AttentionLabel)],
        [nameof(AgentLabel)] = [nameof(AgentClient), nameof(CardClient), nameof(AgentModel), nameof(AgentBrief), nameof(ModelLines), nameof(HasModel), nameof(CardLines), nameof(ClientMark), nameof(AgentChanges), nameof(ClientDiffers), nameof(ModelDiffers), nameof(ReasoningDiffers), nameof(CanRevertAgent), nameof(PeekAgent)],
        [nameof(SelectedConversation)] = [nameof(AgentChanges), nameof(CanRevertConversation), nameof(ShowsAcceptAndFinish)],
        [nameof(Preview)] = [nameof(TaskChanges), nameof(SystemPrompt), nameof(HasSystemPrompt)],
        [nameof(Title)] = [nameof(SystemPrompt)],
        [nameof(PermissionNote)] = [nameof(PermissionSummary)],
        [nameof(ConversationNote)] = [nameof(ConversationSummary)],
        [nameof(Location)] = [nameof(DisplayLocation)],
        [nameof(IsRenaming)] = [nameof(ShowsPeek)],
        [nameof(Title)] = [nameof(ExpandName)],
    };

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
        Picker = new AgentPicker(task.Id, [null, .. ClientCatalog.All], () => _task.Execution, id => _canvas.Clients.Current[id], SetExecution,
            id => ExecutionChoices.ForClient(id, _canvas.Clients.Current[id]));
        Skills = new SkillPickerViewModel(this);
        DeriveCommand = new RelayCommand(() => _canvas.Blueprints.Derive(_task.Blueprint));
        SaveAsBlueprintCommand = new RelayCommand(() => _canvas.Blueprints.SaveAs(_task));
        (_kind, _isLibrary) = (canvas.KindOf(task.Blueprint), !NodeKinds.IsBuiltIn(task.Blueprint));
        InitializeRun();
        InitializeInspector();
        InitializeConversation();
        PropertyChanged += OnOwnPropertyChanged;
    }

    public TaskId Id => _task.Id;

    /// <summary>The canvas the node is on, whose clients and project the node's pickers read.</summary>
    public WorkflowCanvasViewModel Canvas => _canvas;

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
        private set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(ShowsStateBadge));
            }
        }
    }

    public NodeRole Role
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(ShowsStateBadge));
            }
        }
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

    public IReadOnlyList<ConversationChoice> ConversationChoices =>
        [.. Enum.GetValues<ConversationMode>().Select(mode => new ConversationChoice(Id, mode, RunText.ConversationChoice(mode)))];

    public ConversationChoice SelectedConversation => ConversationChoices.First(choice => choice.Mode == _task.Conversation);

    public string ConversationNote => RunText.ConversationNote(_task.Conversation);

    public string AgentLabel => RunText.AgentLabel(_task.Execution, Status);

    /// <summary>The card's first agent line: the client, or "No agent".</summary>
    public string AgentClient => RunText.AgentLines(_task.Execution, Status).Client;

    /// <summary>The card's second line in every state: the model and its level, or empty while the task names neither.</summary>
    public string AgentModel => RunText.AgentLines(_task.Execution, Status).Model ?? "";

    /// <summary>The pickers of the node's agent.</summary>
    public AgentPicker Picker { get; }

    public bool HasClient => _task.Execution is not null;

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
                // The planner's reply can come in through its log before the engine says the node changed, so the canvas
                // draws the proposal's ghosts now and not only at the engine's next change.
                _canvas.ShowGhosts();
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

    /// <summary>The task's agent changed, or what the clients offer did.</summary>
    internal void OnAgentChanged()
    {
        OnPropertyChanged(nameof(AgentLabel));
        Picker.Show();
        OnPropertyChanged(nameof(HasClient));
        OnPropertyChanged(nameof(PermissionNote));
        Skills.Refresh();
    }

    internal void ChooseConversation(ConversationChoice choice)
    {
        if (choice.Mode != _task.Conversation && _canvas.Edit(new WorkflowEdit.SetConversation(Id, choice.Mode)) is EditResult.Rejected)
        {
            OnPropertyChanged(nameof(SelectedConversation));
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

    partial void InitializeInspector();

    partial void InitializeConversation();

    private void OnOwnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is { } name && Dependents.TryGetValue(name, out var dependents))
        {
            foreach (var dependent in dependents)
            {
                OnPropertyChanged(dependent);
            }
        }

        switch (e.PropertyName)
        {
            case nameof(ShowsAcceptAndFinish):
                _acceptAndFinish?.NotifyCanExecuteChanged();
                break;
            case nameof(Draft):
                _detail?.OnDraftChanged();
                break;
            case nameof(Proposal):
                WatchProposal();
                _detail?.Refresh();
                break;
            case nameof(State) or nameof(EngineState) or nameof(Role) or nameof(StatusLabel) or nameof(Preview) or nameof(ReviewSummary)
                or nameof(RunRefusal) or nameof(LastReply) or nameof(Results) or nameof(AgentLabel):
                _detail?.Refresh();
                break;
        }
    }

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
