namespace IDevelop.Desktop.Canvas;

/// <summary>What the node's card shows: its kind tile, its title, and its two lines under it. Its peek and its details show the rest.</summary>
public sealed partial class TaskNodeViewModel
{
    /// <summary>The card's title is a box that takes a new title.</summary>
    public bool IsRenaming
    {
        get;
        private set => SetProperty(ref field, value);
    }

    internal void BeginRename() => IsRenaming = true;

    /// <summary>Ends a rename with the new title, or with null to keep the old one. A blank title keeps the old one too.</summary>
    internal void EndRename(string? title)
    {
        if (!IsRenaming)
        {
            return;
        }

        IsRenaming = false;
        if (!string.IsNullOrWhiteSpace(title) && title.Trim() != Title)
        {
            Title = title.Trim();
        }
    }

    /// <summary>Where the part of the title that tells this node from a similar one starts, which a cut title keeps in view.</summary>
    public int TitleKeep
    {
        get;
        internal set => SetProperty(ref field, value);
    }

    /// <summary>The model and level without the maker's name, which the client's name says, so they fit a card's line.</summary>
    public string AgentBrief => AgentModel.StartsWith("Claude ", StringComparison.Ordinal) ? AgentModel["Claude ".Length..] : AgentModel;

    /// <summary>
    /// The card's model line, the fullest first: the model and level, without the maker's name, then the model, then the part of
    /// its name that tells its version, so a short line reads "Gemini 4" and not a cut name. The peek and the inspector hold the rest.
    /// </summary>
    public IReadOnlyList<string> ModelLines => CardText.ModelLines(AgentModel, AgentBrief);

    /// <summary>
    /// The zoomed out card's last line: the model in every state, or the type of a node with no agent. State never takes the
    /// agent's place, and the badge at the line's end shows the state.
    /// </summary>
    public IReadOnlyList<string> CardLines => AgentModel.Length == 0 ? [TypeName] : CardText.FarLines(ModelLines, AgentModel);

    /// <summary>The node names a model, which the card's last line shows after its client.</summary>
    public bool HasModel => AgentModel.Length > 0;

    /// <summary>The client's name on a card's last line, without the words that every client's name would carry.</summary>
    public string CardClient => AgentClient.Replace(" CLI", "", StringComparison.Ordinal);

    /// <summary>The one letter that names the client on a zoomed out card, whose line has no room for the name.</summary>
    public string ClientMark => _task.Execution is { } settings ? CardText.ClientMark(settings.Client) : "";

    /// <summary>The state in a word, at the end of the card's last line: what a waiting node needs, or the proposal it holds.</summary>
    public string CardStatus => Role == NodeRole.Proposing && Proposal is { } proposal ? CardText.Proposal(proposal.Items.Count)
        : State == NodeState.InputRequired ? NodeStates.Need(WaitReason)
        : State == NodeState.Running ? "Running"
        : StatusLabel;

    /// <summary>How many dependency predecessors feed the node, on the expanded card.</summary>
    public string InputLine => $"Input: {_inputCount}";

    /// <summary>The latest result the node hands on, on the expanded card and the output port's tooltip.</summary>
    public string OutputLine => _history.Count > 0 ? $"Output: Result {_history.Count}" : "Output: No result yet";

    internal void ShowInputCount(int count)
    {
        if (_inputCount != count)
        {
            _inputCount = count;
            OnPropertyChanged(nameof(InputLine));
        }
    }

    private int _inputCount;

    /// <summary>The kind tile's help text, such as "Implement version 1".</summary>
    public string KindHelp => $"{TypeName} version {_task.Blueprint.Key.Version}";

    public bool IsUnderReview => Role == NodeRole.UnderReview;

    /// <summary>"Under review by Check the diff", on the review badge of a node that a review goes on with.</summary>
    public string? ReviewerTip => _reviewer is { } reviewer ? $"Under review by {reviewer}" : null;
}
