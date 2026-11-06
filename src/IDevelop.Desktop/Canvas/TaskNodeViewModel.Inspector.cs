using System.Windows.Input;
using IDevelop.Desktop.Execution;
using IDevelop.Desktop.Inspector;
using IDevelop.Desktop.Mvvm;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

/// <summary>A node at the other end of a connection, in the inspector's Input and Output sections.</summary>
/// <param name="Node">The node, which a click selects.</param>
/// <param name="IsContext">The connection is context, so the row draws a dashed link in place of the node's tile.</param>
public sealed record NodeLink(TaskNodeViewModel Node, bool IsContext);

/// <summary>What the inspector shows of a node beyond the card: section titles, changed counts, reverts, and Accept and Finish.</summary>
public sealed partial class TaskNodeViewModel
{
    private RelayCommand? _acceptAndFinish;
    private ProposalViewModel? _watchedProposal;

    public string AgentSectionTitle => IsReview ? "Reviewer" : "Agent";

    /// <summary>The pinned header's buttons for the node's state. A row that came out the same stays the very row, so a button keeps the focus.</summary>
    public IReadOnlyList<NodeAction> Actions
    {
        get
        {
            var now = NodeActions.Of(this);
            return now.SequenceEqual(field) ? field : field = now;
        }
    } = [];

    /// <summary>The header's row leaves Retry out, so its More menu offers it.</summary>
    public bool RetryIsInMenu => ShowsRetry && Actions.All(action => action.Command != RetryCommand);

    /// <summary>The header's row leaves the conversation out, so its More menu offers it.</summary>
    public bool ConversationIsInMenu => HasAgent && Actions.All(action => action.Command != OpenConversationCommand);

    /// <summary>The nodes this node reads, one for each incoming connection.</summary>
    public IReadOnlyList<NodeLink> Inputs => [.. _canvas.Connections.Where(connection => connection.To == this).Select(connection => new NodeLink(connection.From, connection.Kind == ConnectionKind.Context))];

    public bool HasNoInputs => Inputs.Count == 0;

    /// <summary>The nodes that read this node's output, one for each outgoing connection.</summary>
    public IReadOnlyList<NodeLink> Outputs => [.. _canvas.Connections.Where(connection => connection.From == this).Select(connection => new NodeLink(connection.To, connection.Kind == ConnectionKind.Context))];

    public bool HasOutputs => Outputs.Count > 0;

    /// <summary>The node has no result and no verdict to show yet.</summary>
    public bool HasNoOutput => !HasResults && ReviewSummary is null;

    /// <summary>The permission note as one short line, which names the client's info glyph. The glyph's tooltip holds the whole note.</summary>
    public string? PermissionSummary => _task.Execution is { } settings
        ? IsReview ? RunText.ReviewerSummary(settings.Client) : RunText.PermissionSummary(settings.Client, _task.Blueprint.Work is WorkSpec.Agent { Access: AgentAccess.ReadOnly })
        : null;

    /// <summary>The conversation note as one short line, which names the conversation's info glyph. The glyph's tooltip holds the whole note.</summary>
    public string ConversationSummary => RunText.ConversationSummary(_task.Conversation);

    /// <summary>What the blueprint is for, and what it was derived from, which the header's help glyph shows.</summary>
    public string Description => string.Join("\n", new[] { _task.Blueprint.Description, DerivedFrom is { } source ? $"Derived from {source}." : null }.Where(line => !string.IsNullOrEmpty(line)));

    /// <summary>The header's help glyph: the blueprint's version, then what it is for.</summary>
    public string KindTip => string.Join("\n", new[] { TypeVersion, Description }.Where(line => !string.IsNullOrEmpty(line)));

    /// <summary>The node's step in the dependency order, which its row in the left area shows.</summary>
    public int Step
    {
        get;
        internal set => SetProperty(ref field, value);
    }

    public bool HasFields => Fields.Count > 0;

    /// <summary>How many fields differ from the blueprint's defaults.</summary>
    public int TaskChanges => Fields.Count(entry => entry.CanRevert);

    /// <summary>How many of the Agent section's rows show a revert arrow.</summary>
    public int AgentChanges =>
        new[] { ClientChanged, ModelChanged, ReasoningChanged, Skills.CanRevert, HasConversation && CanRevertConversation }.Count(row => row);

    /// <summary>The client differs from the default or the saved workflow, so its row shows an arrow.</summary>
    public bool ClientChanged => ClientDiffers || ClientEdited;

    /// <summary>The model differs from the default or the saved workflow, so its row shows an arrow.</summary>
    public bool ModelChanged => ModelDiffers || ModelEdited;

    /// <summary>The reasoning level differs from the default or the saved workflow, so its row shows an arrow.</summary>
    public bool ReasoningChanged => ReasoningDiffers || ReasoningEdited;

    /// <summary>The client differs from the saved workflow's, so its row says so where it has no default to go back to.</summary>
    private bool ClientEdited => SavedAgent is { } saved && saved.Client != _task.Execution?.Client;

    /// <summary>The model differs from the saved workflow's.</summary>
    private bool ModelEdited => SavedAgent is { } saved && saved.Model != _task.Execution?.Model;

    /// <summary>The reasoning level differs from the saved workflow's.</summary>
    private bool ReasoningEdited => SavedAgent is { } saved && saved.Reasoning != _task.Execution?.Reasoning;

    private ExecutionSettings? SavedAgent => _canvas.Document.Saved?.Tasks.GetValueOrDefault(Id)?.Execution;

    /// <summary>A save or an edit moved what the rows compare with.</summary>
    internal void ShowAgentEdits()
    {
        OnPropertyChanged(nameof(ClientChanged));
        OnPropertyChanged(nameof(ModelChanged));
        OnPropertyChanged(nameof(ReasoningChanged));
        OnPropertyChanged(nameof(AgentChanges));
    }

    /// <summary>The client differs from a default that it can go back to.</summary>
    public bool ClientDiffers => Default is { } agent && _task.Execution?.Client != agent.Client;

    /// <summary>The model differs from a default that it can go back to.</summary>
    public bool ModelDiffers => Default is { } agent && _task.Execution?.Model != agent.Model;

    /// <summary>The reasoning level differs from a default that it can go back to.</summary>
    public bool ReasoningDiffers => Default is { } agent && _task.Execution?.Reasoning != agent.Reasoning;

    private ExecutionSettings? Default => _task.Blueprint.Defaults.Execution;

    /// <summary>
    /// What the blueprint tells the node's agent, with the node's title and fields in place, or null for a node with no agent.
    /// A value that only a run knows, such as the input from earlier nodes, stays a name in brackets.
    /// </summary>
    public string? SystemPrompt => (_task.Blueprint.Work switch
    {
        WorkSpec.Agent agent => agent.Template,
        WorkSpec.Review review => review.Reviewer,
        WorkSpec.Merge merge => merge.Template,
        _ => null,
    })?.Render(name => name == "title" ? _task.Title
        : Fields.FirstOrDefault(row => row.Spec.Key == name) is { } value ? value.Text
        : $"[{name}]").Trim();

    public bool HasSystemPrompt => !string.IsNullOrEmpty(SystemPrompt);

    /// <summary>The conversation shows this node's agent now, so the header does not offer to open it.</summary>
    public bool IsConversationShown
    {
        get;
        internal set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(Actions));
                OnPropertyChanged(nameof(ShowsStateDetail));
            }
        }
    }

    /// <summary>The header states what the node needs, unless the open conversation asks it in full. A failure's reason always shows.</summary>
    public bool ShowsStateDetail => !string.IsNullOrEmpty(StateDetail) && !(IsConversationShown && Proposal is null && State is NodeState.InputRequired or NodeState.Failed or NodeState.Stale);

    /// <summary>A blueprint without a default agent has nothing to revert to.</summary>
    public bool CanRevertAgent => _task.Blueprint.Defaults.Execution is { } agent && _task.Execution != agent;

    /// <summary>Puts the blueprint's agent back, or the saved workflow's where the agent already matches the blueprint or the blueprint has none.</summary>
    public ICommand RevertAgentCommand { get; private set; } = null!;

    public bool CanRevertConversation => _task.Conversation != _task.Blueprint.Defaults.Conversation;

    public ICommand RevertConversationCommand { get; private set; } = null!;

    /// <summary>The blueprint this one was derived from, such as "Implement, version 1", or null.</summary>
    private string? DerivedFrom => _task.Blueprint.DerivedFrom is { } key
        ? $"{_canvas.FindBlueprint(key)?.Name ?? BuiltInBlueprints.Find(key)?.Name ?? key.Id}, version {key.Version}"
        : null;

    /// <summary>
    /// A Chat planner waits for a reply after each proposal, so accepting is how its conversation ends, and the inspector
    /// offers both in one button.
    /// </summary>
    public bool ShowsAcceptAndFinish => _task.Conversation == ConversationMode.Chat && Proposal is { HasItems: true } && Waits(Engine.NodeState.Reply);

    /// <summary>Accepts the proposal, and once it is accepted, approves the planner's reply as its result.</summary>
    public ICommand AcceptAndFinishCommand => _acceptAndFinish!;

    /// <summary>Selects the node, as clicking it in the connection inspector does.</summary>
    public ICommand SelectCommand { get; private set; } = null!;

    partial void InitializeInspector()
    {
        RevertAgentCommand = new RelayCommand(() => SetExecution(Default is { } agent && _task.Execution != agent ? agent : SavedAgent));
        RevertConversationCommand = new RelayCommand(() => _canvas.Edit(new WorkflowEdit.SetConversation(Id, _task.Blueprint.Defaults.Conversation)));
        SelectCommand = new RelayCommand(() => _canvas.Inspect(this));
        _acceptAndFinish = new RelayCommand(
            AcceptAndFinish,
            () => ShowsAcceptAndFinish && Proposal!.AcceptCommand.CanExecute(null));
    }

    /// <summary>Tells the inspector that a connection of this node came, went, or changed kind.</summary>
    internal void OnLinksChanged()
    {
        OnPropertyChanged(nameof(Inputs));
        OnPropertyChanged(nameof(HasNoInputs));
        OnPropertyChanged(nameof(Outputs));
        OnPropertyChanged(nameof(HasOutputs));
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

    private void OnAcceptChanged(object? sender, EventArgs e) => _acceptAndFinish?.NotifyCanExecuteChanged();

    // The notice keeps what Accept added, so only a refused approval replaces it.
    private void AcceptAndFinish()
    {
        var proposal = Proposal!;
        proposal.AcceptCommand.Execute(null);
        if (ReferenceEquals(Proposal, proposal))
        {
            return;
        }

        try
        {
            _canvas.Engine.Approve(Id);
        }
        catch (InvalidOperationException e)
        {
            _canvas.Notice(e.Message);
        }

        _canvas.ShowEngine();
    }
}
