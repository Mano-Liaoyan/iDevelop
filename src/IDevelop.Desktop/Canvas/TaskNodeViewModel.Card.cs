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

    /// <summary>
    /// One rule for every card: its first line under its title is its client while nothing about its task needs saying,
    /// which is while it is idle and neither a run nor where it stands between runs says more than that it has not started.
    /// Every other card's first line is its status (#90). The second line is its model and level in every state, so every
    /// card has the same three lines whatever its names.
    /// </summary>
    public bool ShowsAgent => State == NodeState.Idle && Role == NodeRole.None && Standing is null &&
        (RunTask is not { } run || WorkflowRunText.NotStarted(run, _runActive));

    /// <summary>The first line when it is not the client: the proposal, else a missing setting, else the run's status.</summary>
    public string Subtitle => Role == NodeRole.Proposing && Proposal is { } proposal ? CardText.Proposal(proposal.Items.Count)
        : State == NodeState.NeedsSetup && Problem is { } problem && CardText.ShortReason(problem) is { } reason ? reason
        : StatusLabel;

    /// <summary>
    /// The first line's texts when it is not the client, the fullest first, of which the card shows the first that fits
    /// whole: a task of the run that waits for one task names it, and counts it when its title is too long (#90).
    /// </summary>
    public IReadOnlyList<string> SubtitleChoices => RunTask is { State: TaskState.Pending, HeldBy.Count: 1 } && Subtitle == StatusLabel
        ? [Subtitle, "Waits for 1 task"]
        : [Subtitle];

    /// <summary>The kind tile's help text, such as "Implement version 1".</summary>
    public string KindHelp => $"{TypeName} version {_task.Blueprint.Key.Version}";

    public bool IsUnderReview => Role == NodeRole.UnderReview;

    /// <summary>"Under review by Check the diff", on the review badge of a node that a review goes on with.</summary>
    public string? ReviewerTip => Problem is StartProblem.UnderReview underReview ? $"Under review by {underReview.Review}" : null;

    /// <summary>
    /// The card's tooltip: the title, the blueprint and agent, a review's summary, the start of the first field, and why the
    /// node cannot start. None while the title is renamed, so it never covers the box.
    /// </summary>
    public string? CardTip => IsRenaming ? null : string.Join("\n", new[]
    {
        Title,
        HasAgent ? $"{TypeName} · {AgentLabel}" : TypeName,
        // What a task of the run waits for, by name, which the card may only count.
        RunTask is { State: TaskState.Pending } ? RunDetail?.Replace('\u00A0', ' ') : null,
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
                OnPropertyChanged(nameof(SubtitleChoices));
                break;
            case nameof(Role):
                OnPropertyChanged(nameof(ShowsAgent));
                OnPropertyChanged(nameof(Subtitle));
                OnPropertyChanged(nameof(IsUnderReview));
                break;
            case nameof(Proposal) or nameof(StatusLabel):
                OnPropertyChanged(nameof(Subtitle));
                OnPropertyChanged(nameof(SubtitleChoices));
                OnPropertyChanged(nameof(CardTip));
                break;
            case nameof(Problem):
                OnPropertyChanged(nameof(Subtitle));
                OnPropertyChanged(nameof(SubtitleChoices));
                OnPropertyChanged(nameof(ReviewerTip));
                OnPropertyChanged(nameof(CardTip));
                break;
            case nameof(AgentLabel):
                OnPropertyChanged(nameof(AgentClient));
                OnPropertyChanged(nameof(AgentModel));
                OnPropertyChanged(nameof(CardTip));
                break;
            case nameof(Title) or nameof(ReviewSummary) or nameof(IsRenaming):
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
