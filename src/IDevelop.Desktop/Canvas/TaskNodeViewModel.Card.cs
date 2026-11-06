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

    /// <summary>
    /// One rule for every card: its first line under its title is its client while it is idle and nothing marks it, and
    /// its status in every other state. The second line is its model and level in every state, so every card has the same
    /// three lines whatever its names.
    /// </summary>
    public bool ShowsAgent => State == NodeState.Idle && Role == NodeRole.None;

    /// <summary>Where the part of the title that tells this node from a similar one starts, which a cut title keeps in view.</summary>
    public int TitleKeep
    {
        get;
        internal set => SetProperty(ref field, value);
    }

    /// <summary>The status leaves the model its place on the card's last line. What a node waits for takes the whole line.</summary>
    public bool ShowsCardModel => State != NodeState.InputRequired;

    /// <summary>The model and level without the maker's name, which the client's name says, so they fit a card's line.</summary>
    public string AgentBrief => AgentModel.StartsWith("Claude ", StringComparison.Ordinal) ? AgentModel["Claude ".Length..] : AgentModel;

    /// <summary>
    /// The card's model line, the fullest first: the model and level, without the maker's name, then the model, then the part of
    /// its name that tells its version, so a short line reads "Gemini 4" and not a cut name. The peek and the inspector hold the rest.
    /// </summary>
    public IReadOnlyList<string> ModelLines
    {
        get
        {
            var model = AgentBrief.Split(" · ")[0];
            var words = model.Split(' ');
            var version = Array.FindIndex(words, word => word.Any(char.IsDigit));
            return [.. new[] { AgentModel, AgentBrief, model, version < 0 ? model : string.Join(' ', words[..(version + 1)]) }.Distinct()];
        }
    }

    /// <summary>
    /// The zoomed out card's last line. It says what a node that needs the person needs or why it stopped, because its badge
    /// alone does not. Every other node shows its model, or its type when it has no agent.
    /// </summary>
    public IReadOnlyList<string> CardLines => Role == NodeRole.Proposing ? [Subtitle]
        : State == NodeState.InputRequired ? [NodeStates.Need(WaitReason)]
        : State is NodeState.Failed or NodeState.Stale ? [StatusLabel]
        : AgentModel.Length == 0 ? [TypeName]
        : ModelLines.Skip(1).DefaultIfEmpty(AgentModel).ToArray();

    /// <summary>The idle line names the client only when it has no model to name, so every card's line reads the same.</summary>
    public bool ShowsCardClient => AgentModel.Length == 0;

    /// <summary>The first line when it is not the client: the proposal, else the engine's state.</summary>
    public string Subtitle => Role == NodeRole.Proposing && Proposal is { } proposal ? CardText.Proposal(proposal.Items.Count) : StatusLabel;

    /// <summary>The first line's texts when it is not the client, of which the card shows the first that fits whole.</summary>
    public IReadOnlyList<string> SubtitleChoices => [Subtitle];

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
