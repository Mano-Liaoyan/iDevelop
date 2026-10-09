using System.Windows.Input;
using Avalonia.Threading;
using IDevelop.Desktop.Execution;
using IDevelop.Desktop.Mvvm;
using IDevelop.Execution;

namespace IDevelop.Desktop.Canvas;

/// <summary>A task the run holds back for, with where that task stands now.</summary>
public sealed record CardWait(string Title, string Status, NodeKind Kind);

/// <summary>
/// What an expanded card and a collapsed card's peek show beyond the card's three lines (#94, ADR 0024). It reads the
/// task through the same conversation session the conversation view opens, through the run while a run shows the task,
/// so the card shows the attempt the conversation shows and an answer from the card goes the conversation's way.
/// </summary>
public sealed class CardDetailViewModel : ObservableObject, IDisposable
{
    private static readonly string[] Shown =
    [
        nameof(Now), nameof(Heading), nameof(Text), nameof(HasText), nameof(ShowsBodyText), nameof(HasWaitsNote), nameof(Meta), nameof(Kind), nameof(IsMessage), nameof(IsAnswerable),
        nameof(IsApproval), nameof(IsResult), nameof(IsOutOfDate), nameof(IsStopped), nameof(IsWaits), nameof(IsTask), nameof(IsProposal), nameof(Waits),
        nameof(RunNote), nameof(HasRunNote), nameof(Problem), nameof(HasProblem),
        nameof(AnswerNote), nameof(Latest), nameof(IsProblem), nameof(IsQuietStop), nameof(IsInspectorRequest), nameof(IsCallout), nameof(NeedsInspector), nameof(AnswerWatermark),
    ];

    private readonly TaskNodeViewModel _node;
    private readonly IConversationSession? _session;
    private readonly RelayCommand _send;
    private ConversationSnapshot? _snapshot;
    private CardNow _now;
    private string? _notice;
    private bool _busy;
    private int _refreshPosted;
    private bool _disposed;

    /// <param name="session">The task's conversation, or null for a node without an agent.</param>
    internal CardDetailViewModel(TaskNodeViewModel node, IConversationSession? session)
    {
        _node = node;
        _session = session;
        _send = new RelayCommand(() => _ = SendAsync(), () => CanSend);
        if (session is not null)
        {
            session.Changed += OnSessionChanged;
        }

        _now = Read();
    }

    /// <summary>The coordinator the session goes through, or null for the task's own attempts.</summary>
    internal object? Route { get; init; }

    public CardNow Now => _now;

    public CardNowKind Kind => _now.Kind;

    public string Heading => _now.Heading;

    public string? Text => _now.Text;

    public bool HasText => !string.IsNullOrEmpty(_now.Text);

    /// <summary>The text sits under the heading, except a waiting task's, which goes under the tasks it waits for.</summary>
    public bool ShowsBodyText => HasText && !IsWaits;

    public bool HasWaitsNote => HasText && IsWaits;

    public string? Meta => _now.Meta;

    public bool IsMessage => Kind == CardNowKind.Message;

    /// <summary>The agent waits for the person's words: an answer to its question, or a reply to its message.</summary>
    public bool IsAnswerable => Kind is CardNowKind.Question or CardNowKind.Reply;

    public bool IsApproval => Kind == CardNowKind.Approval;

    public bool IsResult => Kind == CardNowKind.Result;

    /// <summary>Its result from an earlier run no longer counts, which the warning colour of its state marks (#90).</summary>
    public bool IsOutOfDate => Kind == CardNowKind.OutOfDate;

    public bool IsStopped => Kind == CardNowKind.Stopped;

    public bool IsWaits => Kind == CardNowKind.Waits;

    public bool IsTask => Kind == CardNowKind.Task;

    public bool IsProposal => Kind == CardNowKind.Proposal;

    /// <summary>The task stopped on a failure or an interruption, which its state's tint marks.</summary>
    public bool IsProblem => IsStopped && _node.State is NodeState.Failed or NodeState.Interrupted;

    /// <summary>An approval's request or a planner's proposal, which the person answers in the inspector.</summary>
    public bool IsInspectorRequest => Kind is CardNowKind.Approval or CardNowKind.Proposal;

    /// <summary>The task ended in another way, such as cancelled, or the run holds it for a reason of its own.</summary>
    public bool IsQuietStop => IsStopped && !IsProblem;

    /// <summary>What needs the person, or a problem, sits in a box of its state's tint.</summary>
    public bool IsCallout => IsAnswerable || IsProblem;

    /// <summary>
    /// An approval's request, a planner's proposal, and a run's recovery choices are answered in the inspector, which the
    /// card offers to show.
    /// </summary>
    public bool NeedsInspector => IsInspectorRequest || _node.AnswersInInspector;

    /// <summary>The person's words to the agent, which the conversation's composer shares.</summary>
    public string Draft
    {
        get => _node.Draft;
        set => _node.Draft = value;
    }

    public string AnswerWatermark => Kind == CardNowKind.Question ? "Answer the agent" : "Reply to the agent";

    /// <summary>The tasks it waits for, in the active run or between runs.</summary>
    public IReadOnlyList<CardWait> Waits => _node.Waits;

    /// <summary>Under the expanded card's Run: why Run would start nothing, unless the first section says it already.</summary>
    public string? RunNote => _node.ShowsCardRun && _node.RunRefusal is { } refusal && refusal != Text ? refusal : null;

    public bool HasRunNote => RunNote is not null;

    /// <summary>The peek's last line: why the task cannot start, unless the first section says it already.</summary>
    public string? Problem => _node.PeekProblem is { } problem && problem != Text ? problem : null;

    public bool HasProblem => Problem is not null;

    /// <summary>The attempt the card shows, as the conversation reads it.</summary>
    public AttemptRecord? Latest => _snapshot?.Latest;

    /// <summary>Under the answer box: why the last answer did not go, else why the agent cannot take one now, else the key.</summary>
    public string AnswerNote => _notice ?? (_snapshot?.Actions.Send is { Enabled: false, Reason: var reason } ? reason : "Ctrl+Enter sends");

    /// <summary>Sends the task's draft as the next turn, through the conversation's session.</summary>
    public ICommand SendCommand => _send;

    private bool CanSend => !_busy && IsAnswerable && _snapshot is { Current: not null, Actions.Send.Enabled: true } && !string.IsNullOrWhiteSpace(_node.Draft);

    /// <summary>Reads the task again, after the node's own state changed or its conversation did.</summary>
    internal void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        _now = Read();
        foreach (var property in Shown)
        {
            OnPropertyChanged(property);
        }

        _send.NotifyCanExecuteChanged();
    }

    /// <summary>The draft changed, which decides whether there is anything to send.</summary>
    internal void OnDraftChanged()
    {
        OnPropertyChanged(nameof(Draft));
        _send.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_session is not null)
        {
            _session.Changed -= OnSessionChanged;
            _session.Dispose();
        }
    }

    private CardNow Read()
    {
        _snapshot = _session?.Snapshot;
        return CardNow.Of(_node.CardFacts(_snapshot?.Latest));
    }

    // A session tells of each change, often several at once and off the UI thread, so the card reads once per batch.
    private void OnSessionChanged(long revision)
    {
        if (Interlocked.Exchange(ref _refreshPosted, 1) == 1)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _refreshPosted, 0);
            Refresh();
        });
    }

    // Commands take no cancellation: once sent, an answer goes through even if the card collapses.
    private async Task SendAsync()
    {
        if (_session is null || _snapshot?.Current is not { } turn)
        {
            return;
        }

        var text = _node.Draft;
        _busy = true;
        _send.NotifyCanExecuteChanged();
        try
        {
            var result = await _session.SendAsync(turn, text, stopTurn: false, CancellationToken.None);
            _notice = result is SendResult.Refused refused ? RunText.Describe(refused.Problem) : null;
            if (_notice is null && _node.Draft == text)
            {
                _node.Draft = "";
            }
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }
}
