using System.ComponentModel;
using IDevelop.Desktop.Execution;
using IDevelop.Execution;

namespace IDevelop.Desktop.Canvas;

/// <summary>What the node's card shows: its kind tile, its title, and one subtitle line, with the rest in its tooltip.</summary>
public sealed partial class TaskNodeViewModel
{
    private const int TipFieldLines = 3;

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

    /// <summary>An idle node's subtitle is its agent. Every other node's is its status.</summary>
    public bool ShowsAgent => State == NodeState.Idle && Role == NodeRole.None;

    /// <summary>The subtitle when it is not the agent: the proposal, else a missing setting, else the run's status.</summary>
    public string Subtitle => Role == NodeRole.Proposing && Proposal is { } proposal ? CardText.Proposal(proposal.Items.Count)
        : State == NodeState.NeedsSetup && Problem is { } problem && CardText.ShortReason(problem) is { } reason ? reason
        : StatusLabel;

    /// <summary>The kind tile's help text, such as "Implement version 1".</summary>
    public string KindHelp => $"{TypeName} version {_task.Blueprint.Key.Version}";

    public bool IsUnderReview => Role == NodeRole.UnderReview;

    /// <summary>"Under review by Check the diff", on the review badge of a node that a review goes on with.</summary>
    public string? ReviewerTip => Problem is StartProblem.UnderReview underReview ? $"Under review by {underReview.Review}" : null;

    /// <summary>
    /// The card's tooltip: the title, the blueprint and agent, a review's summary, the start of the first field, and why the
    /// node cannot start.
    /// </summary>
    public string CardTip => string.Join("\n", new[]
    {
        Title,
        HasAgent ? $"{TypeName} · {AgentLabel}" : TypeName,
        ReviewSummary,
        FirstFieldLines(),
        Problem is { } problem ? RunText.Describe(problem) : null,
    }.Where(line => !string.IsNullOrWhiteSpace(line)));

    partial void InitializeCard()
    {
        PropertyChanged += (_, e) => OnCardChanged(e.PropertyName);
        if (Fields.Count > 0)
        {
            Fields[0].PropertyChanged += (_, _) => OnPropertyChanged(nameof(CardTip));
        }
    }

    private void OnCardChanged(string? property)
    {
        switch (property)
        {
            case nameof(State):
                OnPropertyChanged(nameof(ShowsAgent));
                OnPropertyChanged(nameof(Subtitle));
                break;
            case nameof(Role):
                OnPropertyChanged(nameof(ShowsAgent));
                OnPropertyChanged(nameof(Subtitle));
                OnPropertyChanged(nameof(IsUnderReview));
                break;
            case nameof(Proposal) or nameof(StatusLabel):
                OnPropertyChanged(nameof(Subtitle));
                break;
            case nameof(Problem):
                OnPropertyChanged(nameof(Subtitle));
                OnPropertyChanged(nameof(ReviewerTip));
                OnPropertyChanged(nameof(CardTip));
                break;
            case nameof(Title) or nameof(AgentLabel) or nameof(ReviewSummary):
                OnPropertyChanged(nameof(CardTip));
                break;
        }
    }

    private string? FirstFieldLines()
    {
        if (Fields.Count == 0)
        {
            return null;
        }

        var lines = Fields[0].Text.Trim().Split('\n');
        return lines.Length <= TipFieldLines ? string.Join("\n", lines) : string.Join("\n", lines.Take(TipFieldLines)) + " …";
    }
}
