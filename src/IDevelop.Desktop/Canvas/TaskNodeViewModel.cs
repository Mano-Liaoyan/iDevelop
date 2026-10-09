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
    private readonly RelayCommand _run;
    private readonly RelayCommand _cancel;
    private readonly RelayCommand _send;
    private readonly RelayCommand _stopAndSend;
    private readonly RelayCommand _openInTerminal;
    private readonly RelayCommand _markDone;
    private readonly RelayCommand _continueFix;
    private readonly RelayCommand _retryFix;
    private TaskDefinition _task;
    private Point _location;
    private AttemptRecord? _attempt;
    private (AttemptId? Continues, ImmutableArray<AttemptRecord> Attempts) _earlier = (null, []);
    private AttemptRecord? _proposed;
    private ProposalRead _proposalRead = new ProposalRead.None();
    private ProposalViewModel? _proposal;
    private NodeKind _kind;
    private bool _isLibrary;
    private StartProblem? _problem;
    private NodeState _state;
    private NodeRole _role;

    internal TaskNodeViewModel(WorkflowCanvasViewModel canvas, TaskDefinition task, CanvasPoint position)
    {
        _canvas = canvas;
        _task = task;
        _location = WorkflowCanvasViewModel.ToPoint(position);
        _attempt = canvas.Runs.Latest.GetValueOrDefault(task.Id);
        Fields = [.. task.Blueprint.Fields.Select(field => new FieldViewModel(this, field))];
        Input = new PortViewModel(this, PortSide.Input);
        Output = new PortViewModel(this, PortSide.Output);
        _run = new RelayCommand(Run, () => !RunsHere && (!IsRunOwned || JoinsRun) && RunRefusal is null);
        _cancel = new RelayCommand(
            async () => _canvas.Notice(await _canvas.Runs.CancelAsync(Id) is { } problem ? RunText.Describe(problem) : null),
            () => !IsRunOwned && (StandaloneWaiting || _attempt is { Status: AttemptStatus.InReview } ||
                _attempt is { Status: AttemptStatus.Running, Stopping: false } attempt && _canvas.Runs.Active.Any(run => run.Id == attempt.Id)));
        _send = new RelayCommand(() => Send(stopTurn: false), () => !IsRunOwned && !string.IsNullOrWhiteSpace(Draft) && _canvas.Runs.CheckSend(_task) is null);
        _stopAndSend = new RelayCommand(() => Send(stopTurn: true), () => CanStopAndSend && _send.CanExecute(null));
        _openInTerminal = new RelayCommand(OpenInTerminal, () => !IsRunOwned && _attempt is { Status: AttemptStatus.WaitingForInput, SessionId: not null });
        _markDone = new RelayCommand(MarkDone, () => StandaloneWaiting);
        _continueFix = new RelayCommand(() => ChooseFix(FixChoice.Continue), () => RunFix is { } fix
            ? CanChooseRunFix && fix.ContinueUnavailable is null : RunTask is null && _problem is StartProblem.FixInterrupted { CanContinue: true });
        _retryFix = new RelayCommand(() => ChooseFix(FixChoice.Retry), () => RunFix is not null ? CanChooseRunFix : RunTask is null && _problem is StartProblem.FixInterrupted);
        DeriveCommand = new RelayCommand(() => _canvas.Blueprints.Derive(_task.Blueprint));
        SaveAsBlueprintCommand = new RelayCommand(() => _canvas.Blueprints.SaveAs(_task));
        (_kind, _isLibrary) = (canvas.KindOf(task.Blueprint), !NodeKinds.IsBuiltIn(task.Blueprint));
        _state = NodeStates.Of(_attempt, RunsElsewhere, null);
        InitializeCard();
        InitializeActions();
        InitializeInspector();
        InitializeConversation();
        InitializeRun();
        InitializeStanding();
    }

    public TaskId Id => _task.Id;

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

    /// <summary>
    /// Why the task cannot start now, or null, also while this window runs it. It is kept rather than computed on each
    /// read, because the card shows it for every node and each check takes the runs' lock.
    /// </summary>
    public StartProblem? Problem => _problem;

    public NodeState State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }

    public NodeRole Role
    {
        get => _role;
        private set => SetProperty(ref _role, value);
    }

    /// <summary>How often the node asked the runs why it cannot start.</summary>
    internal int ProblemChecks { get; private set; }

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
        ? "Both agents read your guidance in their next message. Ctrl+Enter sends."
        : "Write to the agent. Ctrl+Enter sends.";

    /// <summary>"Round 2 · 1 open finding" for a review's latest attempt, or null.</summary>
    public string? ReviewSummary => _attempt is { Subject: not null } review ? RunText.ReviewSummary(review, ReviewLedger.Fold(review)) : null;

    /// <summary>A review's findings, with both sides' latest words.</summary>
    public IReadOnlyList<FindingViewModel> Findings => _attempt is { Subject: not null } review
        ? [.. ReviewLedger.Fold(review).Findings.Select(finding => new FindingViewModel(finding))]
        : [];

    public bool HasFindings => Findings.Count > 0;

    public IReadOnlyList<Choice> ConversationChoices =>
        [.. Enum.GetValues<ConversationMode>().Select(mode => new Choice(Id, mode.ToString(), RunText.ConversationChoice(mode)))];

    public Choice SelectedConversation => ConversationChoices.First(choice => choice.Id == _task.Conversation.ToString());

    public string ConversationNote => RunText.ConversationNote(_task.Conversation);

    /// <summary>The task waits for the person: its latest attempt, or its attempt or approval request in the canvas's run.</summary>
    public bool IsWaiting => RunTask is { } run ? WaitsInRun(run) : _attempt is { Status: AttemptStatus.WaitingForInput };

    /// <summary>The question or the reason the task's own latest attempt waits for the person, or null.</summary>
    public string? Waiting => StandaloneWaiting && _attempt is { Pending: { } pending } ? RunText.Waiting(pending) : null;

    /// <summary>The task's latest attempt outside a workflow run waits for the person, which its own commands answer.</summary>
    private bool StandaloneWaiting => RunTask is null && _attempt is { Status: AttemptStatus.WaitingForInput };

    /// <summary>Closing iDevelop interrupted the review's fix round, which waits for Continue fix or Retry fix.</summary>
    public bool ShowsFixChoice => RunTask is null ? _problem is StartProblem.FixInterrupted : RunFix is not null;

    /// <summary>Continues the interrupted fix round in its session, when that session can go on.</summary>
    public ICommand ContinueFixCommand => _continueFix;

    /// <summary>Starts the interrupted fix round again in a fresh session.</summary>
    public ICommand RetryFixCommand => _retryFix;

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
        ? IsReview ? RunText.ReviewerNote(settings.Client) : RunText.PermissionNote(settings.Client, _task.Blueprint.Work is WorkSpec.Agent { Access: AgentAccess.ReadOnly })
        : null;

    public string StatusLabel => Standing is { } standing ? standing.Card(TitleOf).Label
        : RunTask is { } run ? WorkflowRunText.Of(run, TitleOf, _runActive).Label : RunText.StatusLabel(_attempt, RunsElsewhere);

    /// <summary>
    /// Why this task cannot start now, shown under the Run button before any click: a fix round of its own that closing
    /// iDevelop interrupted, which its Continue fix and Retry fix answer, else the tasks before it that have no results
    /// yet, or what keeps it from starting in a workflow run as configured (#90).
    /// </summary>
    public string? StartProblem => WorkflowRunText.Unbroken(IsRunOwned && !JoinsRun ? RunOwnedProblem
        : RunsHere ? null
        : _problem is StartProblem.FixInterrupted interrupted ? RunText.Describe(interrupted)
        : RunRefusal);

    /// <summary>
    /// Why Run would start nothing, which keeps it off and is its tooltip: the tasks before this one that have no results
    /// yet, or what keeps it from starting in a workflow run as configured (#90). Null when Run can start it, or when Run
    /// does not show because this window runs the task or the canvas's run already holds it.
    /// </summary>
    public string? RunRefusal => RunsHere || IsRunOwned && !JoinsRun ? null : WorkflowRunText.Unbroken(_canvas.RunRefusal(this));

    /// <summary>
    /// Only the inspector shows it, so only the selected task reads the attempts that its last run continues. A task that
    /// the canvas's run owns shows the run's state instead, so its last attempt outside the run stays out of the way.
    /// </summary>
    public AttemptViewModel? LastAttempt => _attempt is null || ShowsRunState ? null : new AttemptViewModel(_attempt, Earlier(_attempt), RunsElsewhere);

    /// <summary>
    /// Runs the task in a workflow run, which then starts each task after it once all of that task's predecessors have
    /// results (#90). Off while this window runs the task on its own, while the canvas's active run already holds it, and
    /// while <see cref="RunRefusal"/> says why it would start nothing.
    /// </summary>
    public ICommand RunCommand => _run;

    public ICommand CancelCommand => _cancel;

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

    /// <summary>Why a message cannot go to the task's agent now, shown under the composer before any click.</summary>
    public string? SendProblem => _canvas.Runs.CheckSend(_task) is { } problem ? RunText.Describe(problem) : null;

    /// <summary>A review's guidance never stops the reviewer's turn.</summary>
    public bool CanStopAndSend => TurnRunsHere && !IsReview;

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

            OnPropertyChanged(nameof(StartProblem));
            OnPropertyChanged(nameof(RunRefusal));
            OnPropertyChanged(nameof(RunOwner));
            _run.NotifyCanExecuteChanged();
            OnConversationChanged();
        }

        Location = WorkflowCanvasViewModel.ToPoint(position);
    }

    /// <summary>
    /// Called on the UI thread with the task's newest attempt after any attempt of the project changes. A run anywhere in
    /// the project can keep this task from starting. Returns whether the attempt changed.
    /// </summary>
    internal bool ShowAttempt(AttemptRecord? attempt)
    {
        var changed = !ReferenceEquals(attempt, _attempt);
        if (changed)
        {
            _attempt = attempt;
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(LastAttempt));
            OnPropertyChanged(nameof(IsWaiting));
            OnPropertyChanged(nameof(Waiting));
            ShowProposal();
            OnPropertyChanged(nameof(Preview));
            OnPropertyChanged(nameof(ReviewSummary));
            OnPropertyChanged(nameof(Findings));
            OnPropertyChanged(nameof(HasFindings));
        }

        OnPropertyChanged(nameof(StartProblem));
        OnPropertyChanged(nameof(RunRefusal));
        _run.NotifyCanExecuteChanged();
        _cancel.NotifyCanExecuteChanged();
        _markDone.NotifyCanExecuteChanged();
        OnConversationChanged();
        return changed;
    }

    /// <summary>
    /// Asks the runs again why the task cannot start, and shows the state that follows. The canvas calls it only after a
    /// change that can alter the answer: the task, its agent, what the clients offer, or an attempt of the task or of a
    /// task connected to it by a dependency.
    /// </summary>
    internal void RecheckProblem()
    {
        ProblemChecks++;
        var problem = RunsHere ? null : _canvas.Runs.Check(_task);
        if (problem != _problem)
        {
            _problem = problem;
            OnPropertyChanged(nameof(Problem));
            OnPropertyChanged(nameof(ShowsFixChoice));
            _continueFix.NotifyCanExecuteChanged();
            _retryFix.NotifyCanExecuteChanged();
        }

        // A changed connection can change the tasks this one runs after.
        OnPropertyChanged(nameof(StartProblem));
        OnPropertyChanged(nameof(RunRefusal));
        OnPropertyChanged(nameof(RunOwner));
        _run.NotifyCanExecuteChanged();

        ShowState();
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
        OnPropertyChanged(nameof(RunRefusal));
        _run.NotifyCanExecuteChanged();
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

    private void ChooseFix(FixChoice choice)
    {
        if (RunFix is not null)
        {
            _ = ChooseRunFixAsync(choice);
            return;
        }

        _canvas.Notice(_canvas.Runs.ChooseFix(Id, choice) is StartResult.Refused refused ? RunText.Describe(refused.Problem) : null);
    }

    private void MarkDone() =>
        _canvas.Notice(_canvas.Runs.MarkDone(Id) is { } problem ? RunText.Describe(problem) : null);

    private void Run() => _canvas.RunNode(this);

    /// <summary>
    /// Runs the task on its own in the project folder, as Generate Workflow runs its planner. Its connections play no part,
    /// and a refused start says why in the notice.
    /// </summary>
    internal void RunOnItsOwn() =>
        _canvas.Notice(_canvas.Runs.Start(_task, _canvas.Planning(Id)) is StartResult.Refused refused ? RunText.Describe(refused.Problem) : null);

    private async void Send(bool stopTurn)
    {
        var submitted = Draft;
        if (await _canvas.Runs.SendAsync(_task, submitted, stopTurn) is SendResult.Refused refused)
        {
            _canvas.Notice(RunText.Describe(refused.Problem));
            return;
        }

        if (Draft == submitted) Draft = "";
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
        OnPropertyChanged(nameof(CanStopAndSend));
        _send.NotifyCanExecuteChanged();
        _stopAndSend.NotifyCanExecuteChanged();
        _openInTerminal.NotifyCanExecuteChanged();
    }

    private void ShowState()
    {
        State = Standing is { } standing ? standing.Card(TitleOf).State
            : RunTask is { } run ? WorkflowRunText.Of(run, TitleOf, _runActive).State : NodeStates.Of(_attempt, RunsElsewhere, _problem);
        Role = NodeStates.RoleOf(RunTask is null ? _problem : null, _proposal is { HasItems: true });
    }

    partial void InitializeCard();

    partial void InitializeActions();

    partial void InitializeInspector();

    partial void InitializeConversation();

    partial void InitializeRun();

    partial void InitializeStanding();

    private void SetExecution(ExecutionSettings? settings)
    {
        if (_canvas.Edit(new WorkflowEdit.SetExecution(Id, settings)) is EditResult.Rejected)
        {
            OnAgentChanged();
        }
    }
}
