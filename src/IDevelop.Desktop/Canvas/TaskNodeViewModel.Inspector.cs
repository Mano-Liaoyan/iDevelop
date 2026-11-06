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
        RevertAgentCommand = new RelayCommand(() => SetExecution(_task.Blueprint.Defaults.Execution));
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
