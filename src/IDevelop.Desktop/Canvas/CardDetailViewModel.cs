using System.Windows.Input;
using IDevelop.Desktop.Mvvm;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// What an expanded card and a collapsed card's peek show beyond the card's three lines (#94, ADR 0024). It reads the node
/// as the engine holds it, and an answer from the card continues the node's session as the conversation's composer does.
/// </summary>
public sealed class CardDetailViewModel : ObservableObject, IDisposable
{
    private static readonly string[] Shown =
    [
        nameof(Now), nameof(Heading), nameof(Text), nameof(HasText), nameof(Meta), nameof(Kind), nameof(IsMessage), nameof(IsAnswerable),
        nameof(IsApproval), nameof(IsResult), nameof(IsStale), nameof(IsStopped), nameof(IsTask), nameof(IsProposal), nameof(Problem),
        nameof(HasProblem), nameof(IsProblem), nameof(IsQuietStop), nameof(IsInspectorRequest), nameof(IsCallout), nameof(NeedsInspector),
        nameof(AnswerWatermark),
    ];

    private readonly TaskNodeViewModel _node;
    private CardNow _now;
    private bool _disposed;

    internal CardDetailViewModel(TaskNodeViewModel node)
    {
        _node = node;
        _now = CardNow.Of(node.CardFacts());
    }

    public CardNow Now => _now;

    public CardNowKind Kind => _now.Kind;

    public string Heading => _now.Heading;

    public string? Text => _now.Text;

    public bool HasText => !string.IsNullOrEmpty(_now.Text);

    public string? Meta => _now.Meta;

    public bool IsMessage => Kind == CardNowKind.Message;

    /// <summary>The agent waits for the person's words: an answer to its question, or a reply to its message.</summary>
    public bool IsAnswerable => Kind is CardNowKind.Question or CardNowKind.Reply;

    public bool IsApproval => Kind == CardNowKind.Approval;

    public bool IsResult => Kind == CardNowKind.Result;

    /// <summary>Its result rests on a basis that changed, which the warning colour of its state marks.</summary>
    public bool IsStale => Kind == CardNowKind.Stale;

    public bool IsStopped => Kind == CardNowKind.Stopped;

    public bool IsTask => Kind == CardNowKind.Task;

    public bool IsProposal => Kind == CardNowKind.Proposal;

    /// <summary>The node failed, which its state's tint marks.</summary>
    public bool IsProblem => IsStopped && _node.State == NodeState.Failed;

    /// <summary>An approval or a planner's proposal, which the person answers in the inspector or with the card's buttons.</summary>
    public bool IsInspectorRequest => Kind is CardNowKind.Approval or CardNowKind.Proposal;

    /// <summary>The node waits for another reason, such as an interruption or a conflict.</summary>
    public bool IsQuietStop => IsStopped && !IsProblem;

    /// <summary>What needs the person, or a failure, sits in a box of its state's tint.</summary>
    public bool IsCallout => IsAnswerable || IsProblem;

    /// <summary>A planner's proposal and a send back's note are written in the inspector, which the card offers to show.</summary>
    public bool NeedsInspector => IsProposal || _node.ShowsSendBack;

    /// <summary>The person's words to the agent, which the conversation's composer shares.</summary>
    public string Draft
    {
        get => _node.Draft;
        set => _node.Draft = value;
    }

    public string AnswerWatermark => Kind == CardNowKind.Question ? "Answer the agent" : "Reply to the agent";

    /// <summary>The peek's last line: why the node cannot run, unless the first section says it already.</summary>
    public string? Problem => _node.RunRefusal is { } problem && problem != Text ? problem : null;

    public bool HasProblem => Problem is not null;

    /// <summary>Under the answer box: how to send.</summary>
    public string AnswerNote => "Ctrl+Enter sends";

    /// <summary>Sends the node's draft as the next turn of its session.</summary>
    public ICommand SendCommand => _node.SendCommand;

    /// <summary>Reads the node again, after its state or its log changed.</summary>
    internal void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        _now = CardNow.Of(_node.CardFacts());
        foreach (var property in Shown)
        {
            OnPropertyChanged(property);
        }
    }

    /// <summary>The draft changed, which decides whether there is anything to send.</summary>
    internal void OnDraftChanged() => OnPropertyChanged(nameof(Draft));

    public void Dispose() => _disposed = true;
}
