using IDevelop.Desktop.Execution;

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

    /// <summary>The words of the title that tell this node from every other node in the workflow, which a cut title keeps in view.</summary>
    public Theme.TitleKey TitleKey
    {
        get;
        internal set => SetProperty(ref field, value);
    }

    /// <summary>
    /// The card's model text: the end of the model's name as the inspector's Model field shows it, without the family word that
    /// the client's badge gives, so "Claude Opus 5.5" reads "Opus 5.5" beside CC (ADR 0063). The tooltip and the inspector keep
    /// the full name.
    /// </summary>
    public string CardModel => _task.Execution is { } settings && RunText.AgentNames(settings, Status).Model is { } model
        ? CardText.ModelName(settings.Client, model) : "";

    /// <summary>Flow's card line: the model in every state, or the type of a node with no agent.</summary>
    public string CardLine => AgentModel.Length == 0 ? TypeName : CardModel;

    /// <summary>The node names a model, which the card's last line shows after its client.</summary>
    public bool HasModel => AgentModel.Length > 0;

    /// <summary>The agent row's start: the client's name without the words that every client's name would carry, or Approval for a person's node.</summary>
    public string CardClient => HasAgent ? AgentClient.Replace(" CLI", "", StringComparison.Ordinal) : "Approval";

    /// <summary>The socket row's left label, "Input", or "Input 3" with more than one connection.</summary>
    public string CardInput => _connectionCount > 1 ? $"Input {_connectionCount}" : "Input";

    /// <summary>The socket row's right label: "Result" while a result exists, else "No result".</summary>
    public string CardResult => HasResults ? "Result" : "No result";

    /// <summary>The commit's seven characters, or "Report" for a result with no commit, or empty with no result.</summary>
    public string CardResultDetail => LatestResult is not { } result ? "" : result.Commit is { Length: >= 7 } commit ? commit[..7] : "Report";

    /// <summary>The socket row's detail is a commit, so it draws in the mono font.</summary>
    public bool CardResultIsCommit => LatestResult?.Commit is { Length: >= 7 };

    private int _connectionCount;

    /// <summary>The node's connection count, in and out of the canvas, which names its input socket.</summary>
    internal void ShowConnectionCount(int count)
    {
        if (_connectionCount != count)
        {
            _connectionCount = count;
            OnPropertyChanged(nameof(CardInput));
        }
    }

    /// <summary>The state's word. A card shows it as its State Mark, and the mark's glyph tells a waiting node's reason.</summary>
    public string CardStatus => NodeStates.Word(State);

    /// <summary>The tooltip of the card's State Mark: the state and its reason, and who reviews the node while a review goes on.</summary>
    public string MarkTip => ReviewerTip is { } reviewer ? $"{StatusLabel}\n{reviewer}" : StatusLabel;

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
