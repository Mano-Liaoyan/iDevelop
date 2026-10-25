namespace IDevelop.Desktop.Canvas;

/// <summary>What the node's card shows: its kind tile, its title, and its two lines under it. Its peek and its details show the rest.</summary>
public sealed partial class TaskNodeViewModel
{
    private bool _isRenaming;

    /// <summary>The card's title is a box that takes a new title.</summary>
    public bool IsRenaming
    {
        get => _isRenaming;
        private set => SetProperty(ref _isRenaming, value);
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

    /// <summary>The first line when it is not the client: the proposal, else the engine's state.</summary>
    public string Subtitle => Role == NodeRole.Proposing && Proposal is { } proposal ? CardText.Proposal(proposal.Items.Count) : StatusLabel;

    /// <summary>The first line's texts when it is not the client, of which the card shows the first that fits whole.</summary>
    public IReadOnlyList<string> SubtitleChoices => [Subtitle];

    /// <summary>The kind tile's help text, such as "Implement version 1".</summary>
    public string KindHelp => $"{TypeName} version {_task.Blueprint.Key.Version}";

    public bool IsUnderReview => Role == NodeRole.UnderReview;

    /// <summary>"Under review by Check the diff", on the review badge of a node that a review goes on with.</summary>
    public string? ReviewerTip => _reviewer is { } reviewer ? $"Under review by {reviewer}" : null;

    partial void InitializeCard()
    {
        PropertyChanged += (_, e) => OnCardChanged(e.PropertyName);
    }

    private void OnCardChanged(string? property)
    {
        switch (property)
        {
            case nameof(State):
                OnPropertyChanged(nameof(ShowsAgent));
                OnPropertyChanged(nameof(Subtitle));
                OnPropertyChanged(nameof(SubtitleChoices));
                break;
            case nameof(Role):
                OnPropertyChanged(nameof(ShowsAgent));
                OnPropertyChanged(nameof(Subtitle));
                OnPropertyChanged(nameof(SubtitleChoices));
                OnPropertyChanged(nameof(IsUnderReview));
                break;
            case nameof(Proposal) or nameof(StatusLabel):
                OnPropertyChanged(nameof(Subtitle));
                OnPropertyChanged(nameof(SubtitleChoices));
                break;
            case nameof(AgentLabel):
                OnPropertyChanged(nameof(AgentClient));
                OnPropertyChanged(nameof(AgentModel));
                break;
        }
    }
}
