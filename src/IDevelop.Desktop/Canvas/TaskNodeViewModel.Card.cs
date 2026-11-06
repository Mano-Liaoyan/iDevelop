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

    /// <summary>The zoomed out card's last line: the model, after a status that its badge does not already say.</summary>
    public string CardLine => State is NodeState.Completed or NodeState.Idle && Role == NodeRole.None
        ? AgentModel ?? ""
        : ShowsCardModel ? string.Join(" · ", new[] { Subtitle, AgentModel }.Where(part => !string.IsNullOrEmpty(part))) : Subtitle;

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
