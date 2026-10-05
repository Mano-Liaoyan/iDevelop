using System.ComponentModel;
using System.Windows.Input;
using IDevelop.Desktop.Execution;
using IDevelop.Desktop.Mvvm;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

/// <summary>What the inspector shows of a node beyond the card: section titles, changed counts, reverts, and Accept and Finish.</summary>
public sealed partial class TaskNodeViewModel
{
    // Each inspector property follows the node properties it is computed from, which the node raises on its own.
    private static readonly Dictionary<string, string[]> InspectorDependents = new()
    {
        [nameof(Preview)] = [nameof(TaskChanges)],
        [nameof(AgentLabel)] = [nameof(AgentChanges), nameof(CanRevertAgent)],
        [nameof(SelectedConversation)] = [nameof(AgentChanges), nameof(CanRevertConversation), nameof(ShowsAcceptAndFinish)],
        [nameof(IsWaiting)] = [nameof(ShowsAcceptAndFinish)],
        [nameof(Proposal)] = [nameof(ShowsAcceptAndFinish)],
        [nameof(PermissionNote)] = [nameof(PermissionSummary)],
        [nameof(ConversationNote)] = [nameof(ConversationSummary)],
    };

    private RelayCommand _acceptAndFinish = null!;
    private ProposalViewModel? _watchedProposal;
    private AttemptViewModel? _lastRun;

    public string AgentSectionTitle => IsReview ? "Reviewer" : "Agent";

    public string ComposerSectionTitle => IsReview ? "Guide the Review" : "Talk to the Agent";

    /// <summary>The permission note as one short line, under the pickers. The client's info glyph holds the whole note.</summary>
    public string? PermissionSummary => _task.Execution is { } settings
        ? IsReview ? RunText.ReviewerSummary(settings.Client) : RunText.PermissionSummary(settings.Client, _task.Blueprint.Work is WorkSpec.Agent { Access: AgentAccess.ReadOnly })
        : null;

    /// <summary>The conversation note as one short line. The conversation's info glyph holds the whole note.</summary>
    public string ConversationSummary => RunText.ConversationSummary(_task.Conversation);

    /// <summary>What the blueprint is for, which the header's help glyph shows.</summary>
    public string Description => _task.Blueprint.Description;

    public bool HasFields => Fields.Count > 0;

    /// <summary>How many fields differ from the blueprint's defaults.</summary>
    public int TaskChanges => Fields.Count(entry => entry.CanRevert);

    /// <summary>Whether the agent and the conversation mode each differ from the blueprint's defaults, counted.</summary>
    public int AgentChanges =>
        (_task.Execution != _task.Blueprint.Defaults.Execution ? 1 : 0) +
        (HasConversation && _task.Conversation != _task.Blueprint.Defaults.Conversation ? 1 : 0);

    /// <summary>A blueprint without a default agent has nothing to revert to.</summary>
    public bool CanRevertAgent => _task.Blueprint.Defaults.Execution is { } agent && _task.Execution != agent;

    public ICommand RevertAgentCommand { get; private set; } = null!;

    public bool CanRevertConversation => _task.Conversation != _task.Blueprint.Defaults.Conversation;

    public ICommand RevertConversationCommand { get; private set; } = null!;

    /// <summary>
    /// The last run as the inspector shows it, built once for each new attempt record, because the run, conversation,
    /// and activity sections each read it.
    /// </summary>
    public AttemptViewModel? LastRun => _lastRun ??= LastAttempt;

    /// <summary>The blueprint's version, and that it is built in.</summary>
    public string BlueprintVersion => _task.Blueprint.IsBuiltIn ? $"{_task.Blueprint.Key.Version} · Built-in" : $"{_task.Blueprint.Key.Version}";

    /// <summary>The blueprint this one was derived from, such as "Implement, version 1", or null.</summary>
    public string? DerivedFrom => _task.Blueprint.DerivedFrom is { } key
        ? $"{_canvas.FindBlueprint(key)?.Name ?? BuiltInBlueprints.Find(key)?.Name ?? key.Id}, version {key.Version}"
        : null;

    /// <summary>
    /// A Chat planner waits for a reply after each proposal, so accepting is how its conversation ends, and the
    /// inspector offers both in one button.
    /// </summary>
    public bool ShowsAcceptAndFinish => _task.Conversation == ConversationMode.Chat && IsWaiting && Proposal is { HasItems: true };

    /// <summary>Accepts the proposal, and once it is accepted, marks the planner done.</summary>
    public ICommand AcceptAndFinishCommand => _acceptAndFinish;

    /// <summary>Selects the node, as clicking it in the connection inspector does.</summary>
    public ICommand SelectCommand { get; private set; } = null!;

    partial void InitializeInspector()
    {
        RevertAgentCommand = new RelayCommand(() => SetExecution(_task.Blueprint.Defaults.Execution));
        RevertConversationCommand = new RelayCommand(() => _canvas.Edit(new WorkflowEdit.SetConversation(Id, _task.Blueprint.Defaults.Conversation)));
        SelectCommand = new RelayCommand(() => _canvas.Inspect(this));
        _acceptAndFinish = new RelayCommand(
            AcceptAndFinish,
            () => ShowsAcceptAndFinish && Proposal!.AcceptCommand.CanExecute(null) && _markDone.CanExecute(null));
        PropertyChanged += OnInspectedChanged;
    }

    private void OnInspectedChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LastAttempt))
        {
            _lastRun = null;
            OnPropertyChanged(nameof(LastRun));
        }
        else if (e.PropertyName == nameof(Proposal))
        {
            WatchProposal();
        }

        if (e.PropertyName is { } name && InspectorDependents.TryGetValue(name, out var dependents))
        {
            foreach (var dependent in dependents)
            {
                OnPropertyChanged(dependent);
            }

            _acceptAndFinish.NotifyCanExecuteChanged();
        }
    }

    private void WatchProposal()
    {
        if (_watchedProposal is not null)
        {
            _watchedProposal.AcceptCommand.CanExecuteChanged -= OnAcceptChanged;
        }

        _watchedProposal = Proposal;
        if (_watchedProposal is not null)
        {
            _watchedProposal.AcceptCommand.CanExecuteChanged += OnAcceptChanged;
        }
    }

    private void OnAcceptChanged(object? sender, EventArgs e) => _acceptAndFinish.NotifyCanExecuteChanged();

    // The notice keeps what Accept added, so only a refused Mark Done replaces it.
    private void AcceptAndFinish()
    {
        var proposal = Proposal!;
        proposal.AcceptCommand.Execute(null);
        if (!ReferenceEquals(Proposal, proposal) && _canvas.Runs.MarkDone(Id) is { } problem)
        {
            _canvas.Notice(RunText.Describe(problem));
        }
    }
}
