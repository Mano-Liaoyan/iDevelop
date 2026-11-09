using System.ComponentModel;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Mvvm;
using EngineState = IDevelop.Engine.NodeState;
using NodeState = IDevelop.Desktop.Canvas.NodeState;

namespace IDevelop.Desktop.Conversation;

/// <summary>What the Action Dock asks of the person. There is one kind for each row of the dock's table.</summary>
public enum ActionDockKind { None, Question, Reply, Approval, RoundLimit, Conflict, Interrupted, Failed, Stale, Refusal }

/// <summary>
/// The one place where a node's required human action appears, above the composer: its heading, its words, and its buttons, from the
/// node's state. The dock never reads the agent's prose for choices. It reads only what the node offers.
/// </summary>
public sealed class ActionDockViewModel : ObservableObject, IDisposable
{
    private static readonly string[] NodeProperties =
    [
        nameof(TaskNodeViewModel.EngineState), nameof(TaskNodeViewModel.IsAsking), nameof(TaskNodeViewModel.Questions), nameof(TaskNodeViewModel.RunRefusal),
        nameof(TaskNodeViewModel.ShowsApprove), nameof(TaskNodeViewModel.ApproveLabel), nameof(TaskNodeViewModel.StateDetail), nameof(TaskNodeViewModel.LastReply),
        nameof(TaskNodeViewModel.StatusLabel), nameof(TaskNodeViewModel.Outputs),
    ];

    private static readonly string[] StateProperties =
    [
        nameof(Kind), nameof(IsShown), nameof(IsFailed), nameof(IsWarning), nameof(HasHeading), nameof(Title), nameof(MarkState), nameof(MarkWhy), nameof(Detail), nameof(HasDetail),
        nameof(ShowsQuestions), nameof(ShowsApprove), nameof(IsApprovePrimary), nameof(ApproveLabel), nameof(ApproveTip), nameof(ShowsSendBack), nameof(ShowsConflict), nameof(ShowsContinue),
        nameof(ShowsRetry), nameof(IsRetryPrimary), nameof(ShowsTerminal), nameof(Refusal), nameof(HasRefusal), nameof(HasOffered), nameof(HasActions), nameof(HasPinned), nameof(HasBody),
    ];

    private bool _disposed;

    internal ActionDockViewModel(TaskNodeViewModel node)
    {
        Node = node;
        node.PropertyChanged += OnNodeChanged;
    }

    public TaskNodeViewModel Node { get; }

    public ActionDockKind Kind => Node.IsAsking ? ActionDockKind.Question : Node.EngineState switch
    {
        EngineState.InputRequired { Why: EngineState.Approval } => ActionDockKind.Approval,
        EngineState.InputRequired { Why: EngineState.RoundLimit } => ActionDockKind.RoundLimit,
        EngineState.InputRequired { Why: EngineState.Conflict } => ActionDockKind.Conflict,
        EngineState.InputRequired { Why: EngineState.Interrupted } => ActionDockKind.Interrupted,
        EngineState.InputRequired => ActionDockKind.Reply,
        EngineState.Failed => ActionDockKind.Failed,
        EngineState.Stale => ActionDockKind.Stale,
        _ => HasRefusal ? ActionDockKind.Refusal : ActionDockKind.None,
    };

    public bool IsShown => Kind != ActionDockKind.None;

    /// <summary>The dock takes the Failed state's stroke and fill.</summary>
    public bool IsFailed => Kind == ActionDockKind.Failed;

    /// <summary>The dock takes the Stale state's stroke and fill, as a refusal to run does.</summary>
    public bool IsWarning => Kind is ActionDockKind.Stale or ActionDockKind.Refusal;

    /// <summary>A refusal says everything in its one line, so it has no heading of its own.</summary>
    public bool HasHeading => Kind is not (ActionDockKind.None or ActionDockKind.Refusal);

    /// <summary>The state in its words, such as "Needs you · Question".</summary>
    public string Title => Kind switch
    {
        ActionDockKind.Question => $"{NodeStates.Word(NodeState.InputRequired)} · {NodeStates.Need(WaitReason.Question)}",
        ActionDockKind.None or ActionDockKind.Refusal => "",
        ActionDockKind.Failed => NodeStates.Word(NodeState.Failed),
        ActionDockKind.Stale => NodeStates.Word(NodeState.Stale),
        _ => Node.StatusLabel,
    };

    /// <summary>The State Mark on the heading. A question that a running agent asks shows the Needs you disc.</summary>
    public NodeState MarkState => Kind switch
    {
        ActionDockKind.Failed => NodeState.Failed,
        ActionDockKind.Stale => NodeState.Stale,
        _ => NodeState.InputRequired,
    };

    /// <summary>The reason glyph on the mark.</summary>
    public WaitReason MarkWhy => Kind == ActionDockKind.Question ? WaitReason.Question : Node.WaitReason;

    /// <summary>The state's sentence. Its "Write back, or approve" stays away while Accept does, and an agent's own words stay in the transcript.</summary>
    public string? Detail => Kind switch
    {
        ActionDockKind.None or ActionDockKind.Question or ActionDockKind.Refusal => null,
        ActionDockKind.Reply when !Node.ShowsApprove || Node.StateDetail?.Trim() == Node.LastReply?.Trim() => null,
        _ => Node.StateDetail,
    };

    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    /// <summary>What an Approval or a Round limit offers, which the last wait's marker holds, or null.</summary>
    public string? Offered
    {
        get;
        internal set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(HasOffered));
                OnPropertyChanged(nameof(HasBody));
            }
        }
    }

    public bool HasOffered => Kind is ActionDockKind.Approval or ActionDockKind.RoundLimit && !string.IsNullOrEmpty(Offered);

    public bool ShowsQuestions => Kind == ActionDockKind.Question;

    public bool ShowsApprove => Kind is ActionDockKind.Reply or ActionDockKind.Approval or ActionDockKind.RoundLimit && Node.ShowsApprove;

    /// <summary>Approve leads an Approval. Beside a reply the composer leads, so Accept stays quiet.</summary>
    public bool IsApprovePrimary => Kind is ActionDockKind.Approval or ActionDockKind.RoundLimit;

    public string ApproveLabel => Node.ApproveLabel;

    /// <summary>What Approve hands on, and to which nodes.</summary>
    public string ApproveTip => Kind == ActionDockKind.Reply
        ? $"Completes the node and hands the agent's message above to {Recipients}."
        : $"Makes the agent's last reply the result for {Recipients}.";

    public bool ShowsSendBack => Kind is ActionDockKind.Approval or ActionDockKind.RoundLimit;

    public bool ShowsConflict => Kind == ActionDockKind.Conflict;

    public bool ShowsContinue => Kind == ActionDockKind.Interrupted;

    public bool ShowsRetry => Kind is ActionDockKind.Interrupted or ActionDockKind.Failed or ActionDockKind.Stale;

    public bool IsRetryPrimary => Kind is ActionDockKind.Failed or ActionDockKind.Stale;

    public bool ShowsTerminal => Kind == ActionDockKind.Failed;

    /// <summary>Why the engine would not start the node, with the Warning mark, or null.</summary>
    public string? Refusal => Node.RunRefusal;

    public bool HasRefusal => !string.IsNullOrEmpty(Refusal);

    /// <summary>The dock's row of buttons has something to hold.</summary>
    public bool HasActions => ShowsApprove || ShowsSendBack || ShowsConflict || ShowsContinue || ShowsRetry || ShowsTerminal;

    /// <summary>The dock has words under its heading.</summary>
    public bool HasBody => HasOffered || HasDetail || HasRefusal || ShowsQuestions;

    /// <summary>The note box or a button shows, and they stay in view under the words.</summary>
    public bool HasPinned => HasActions || ShowsSendBack;

    // "A and B", "A, B, and 2 more", or the node itself when nothing reads it.
    private string Recipients => Node.Outputs.Select(link => link.Node.Title).ToList() switch
    {
        [] => "this node",
        [var one] => one,
        [var one, var two] => $"{one} and {two}",
        [var one, var two, .. var rest] => $"{one}, {two}, and {rest.Count} more",
    };

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            Node.PropertyChanged -= OnNodeChanged;
        }
    }

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (NodeProperties.Contains(e.PropertyName))
        {
            foreach (var name in StateProperties)
            {
                OnPropertyChanged(name);
            }
        }
    }
}
