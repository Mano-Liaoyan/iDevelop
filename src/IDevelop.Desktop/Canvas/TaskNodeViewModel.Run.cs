using System.Windows.Input;
using IDevelop.Desktop.Execution;
using IDevelop.Desktop.Mvvm;
using IDevelop.Engine;
using IDevelop.Nodes;
using IDevelop.Workflows;
using EngineState = IDevelop.Engine.NodeState;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// The node as the canvas's engine sees it: its state, why it cannot run, its results, its agent's latest reply, and the
/// commands that act on it (ADR 0017, ADR 0042). The card, its menu, and the inspector share them.
/// </summary>
public sealed partial class TaskNodeViewModel
{
    private static readonly string[] RunDependents =
    [
        nameof(EngineState), nameof(StatusLabel), nameof(StateDetail), nameof(Tone), nameof(IsWaiting), nameof(RunRefusal), nameof(ShowsRun),
        nameof(ShowsRetry), nameof(ShowsStop), nameof(ShowsApprove), nameof(ShowsSendBack), nameof(ShowsContinue), nameof(ShowsMarkResolved),
        nameof(CanStopAndSend), nameof(Results), nameof(HasResults), nameof(LastReply), nameof(ReviewSummary), nameof(FindingsSummary),
        nameof(Findings), nameof(HasFindings), nameof(Preview), nameof(SendProblem), nameof(Latest),
    ];

    private EngineState _engineState = new EngineState.Idle();
    private string? _runRefusal;
    private IReadOnlyList<NodeResult> _history = [];
    private (int Index, string Text)? _lastReply;
    private (int Index, string Text)? _proposed;
    private string? _latest;
    private IConversation? _log;
    private int _logChanged;
    private string _sendBackNote = "";
    private RelayCommand _run = null!;
    private RelayCommand _retry = null!;
    private RelayCommand _stop = null!;
    private RelayCommand _approve = null!;
    private RelayCommand _sendBack = null!;
    private RelayCommand _continue = null!;
    private RelayCommand _markResolved = null!;
    private RelayCommand _openWorktree = null!;
    private RelayCommand _send = null!;
    private RelayCommand _stopAndSend = null!;
    private RelayCommand _openInTerminal = null!;

    /// <summary>The node's state as the engine gives it, with why it waits for the person.</summary>
    public EngineState EngineState => _engineState;

    /// <summary>"Running", "Completed", or what the node needs, such as "Input required · approval".</summary>
    public string StatusLabel => NodeStates.Label(_engineState);

    /// <summary>What the node needs from the person, or why it failed or went stale, or null when its status says enough.</summary>
    public string? StateDetail => NodeStates.Detail(_engineState);

    public StatusTone Tone => NodeStates.Tone(State);

    /// <summary>The node waits for the person.</summary>
    public bool IsWaiting => State == NodeState.InputRequired;

    /// <summary>Why the engine would not start the node now, which keeps Run and Retry off and is their tooltip, or null.</summary>
    public string? RunRefusal => State == NodeState.Running ? null : _runRefusal;

    /// <summary>Run starts a node that has not run yet.</summary>
    public bool ShowsRun => State == NodeState.Idle;

    /// <summary>Retry starts a fresh session once a turn has ended.</summary>
    public bool ShowsRetry => State is NodeState.Completed or NodeState.Failed or NodeState.Stale or NodeState.InputRequired;

    public bool ShowsStop => State == NodeState.Running;

    /// <summary>
    /// Approve accepts what an Approval node or a reviewer at its round limit offers, or a Chat node's last reply. While a
    /// planner's proposal is open, Accept and Finish approves it instead.
    /// </summary>
    public bool ShowsApprove => Waits(EngineState.Approval, EngineState.RoundLimit, EngineState.Reply) && Role != NodeRole.Proposing;

    /// <summary>Send back returns the person's note to the nodes before an Approval node or a reviewer at its round limit.</summary>
    public bool ShowsSendBack => Waits(EngineState.Approval, EngineState.RoundLimit);

    /// <summary>A turn that iDevelop's closing interrupted goes on in its session.</summary>
    public bool ShowsContinue => Waits(EngineState.Interrupted);

    /// <summary>The person resolved the conflict in the node's worktree.</summary>
    public bool ShowsMarkResolved => Waits(EngineState.Conflict);

    /// <summary>The note that Send back returns, which the inspector writes.</summary>
    public string SendBackNote
    {
        get => _sendBackNote;
        set
        {
            if (SetProperty(ref _sendBackNote, value ?? ""))
            {
                _sendBack.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>The node's results, newest first, each with whether it is the current one.</summary>
    public IReadOnlyList<ResultViewModel> Results => [.. _history.Select((result, index) => new ResultViewModel(result, index + 1,
        current: index == _history.Count - 1 && State == NodeState.Completed)).Reverse()];

    public bool HasResults => _history.Count > 0;

    /// <summary>What the agent did last: its latest message, or the tool it started after it, or null.</summary>
    public string? Latest => _latest;

    /// <summary>The prose of the agent's latest complete message, without its result block, or null.</summary>
    public string? LastReply => _lastReply is { Text: var text } ? ResultBlock.Prose(text) : null;

    /// <summary>The verdict of a review's latest reply, or null.</summary>
    private Verdict? Verdict => IsReview && _lastReply is { Text: var reply } ? ReviewLedger.ReadVerdict(reply).Verdict : null;

    /// <summary>"Approved" or "2 open findings" for a review's latest verdict, or null, which its card shows.</summary>
    public string? ReviewSummary => Verdict is { } verdict
        ? verdict.Approves ? "Approved" : verdict.Findings.Length == 1 ? "1 open finding" : $"{verdict.Findings.Length} open findings"
        : null;

    public string? FindingsSummary => ReviewSummary;

    public IReadOnlyList<FindingViewModel> Findings => Verdict is { } verdict ? [.. verdict.Findings.Select(finding => new FindingViewModel(finding))] : [];

    public bool HasFindings => Findings.Count > 0;

    public ICommand RunCommand => _run;

    public ICommand RetryCommand => _retry;

    public ICommand StopCommand => _stop;

    public ICommand ApproveCommand => _approve;

    public ICommand SendBackCommand => _sendBack;

    /// <summary>Continues an interrupted turn's session.</summary>
    public ICommand ContinueCommand => _continue;

    public ICommand MarkResolvedCommand => _markResolved;

    /// <summary>Opens the node's worktree, where the person resolves a conflict.</summary>
    public ICommand OpenWorktreeCommand => _openWorktree;

    /// <summary>Continues the node's session with the draft, after the running turn ends.</summary>
    public ICommand SendCommand => _send;

    /// <summary>Stops the running turn and continues the session with the draft.</summary>
    public ICommand StopAndSendCommand => _stopAndSend;

    /// <summary>Copies the client's command that continues the node's session in a terminal.</summary>
    public ICommand OpenInTerminalCommand => _openInTerminal;

    public bool CanStopAndSend => HasAgent && State == NodeState.Running;

    /// <summary>Why a message cannot go to the agent now, or null.</summary>
    public string? SendProblem => !HasAgent ? null : State == NodeState.Idle ? "Run the node first. A message continues its session." : null;

    partial void InitializeRun()
    {
        _run = new RelayCommand(() => Act(engine => engine.Run(Id)), () => ShowsRun && RunRefusal is null);
        _retry = new RelayCommand(() => Act(engine => engine.Retry(Id)), () => ShowsRetry && RunRefusal is null);
        _stop = new RelayCommand(() => Act(engine => engine.Stop(Id)), () => ShowsStop);
        _approve = new RelayCommand(Approve, () => ShowsApprove);
        _sendBack = new RelayCommand(SendBack, () => ShowsSendBack && !string.IsNullOrWhiteSpace(SendBackNote));
        _continue = new RelayCommand(() => Act(engine => engine.Send(Id, "Continue where you left off.", interrupt: false)), () => ShowsContinue);
        _markResolved = new RelayCommand(() => Act(engine => engine.MarkResolved(Id)), () => ShowsMarkResolved);
        _openWorktree = new RelayCommand(async () =>
        {
            if (_canvas.Engine.Folder(Id) is not { } folder || !await _canvas.OpenFolder(folder))
            {
                _canvas.Notice("Couldn't open the node's worktree.");
            }
        }, () => ShowsMarkResolved);
        _send = new RelayCommand(() => Send(interrupt: false), () => HasAgent && State != NodeState.Idle && !string.IsNullOrWhiteSpace(Draft));
        _stopAndSend = new RelayCommand(() => Send(interrupt: true), () => CanStopAndSend && !string.IsNullOrWhiteSpace(Draft));
        _openInTerminal = new RelayCommand(OpenInTerminal, () => HasAgent && State is not (NodeState.Idle or NodeState.Running));
    }

    /// <summary>Reads the node from the engine again: its state, why it cannot run, its results, and its agent's latest reply.</summary>
    internal void ShowEngine()
    {
        var engine = _canvas.Engine;
        _engineState = engine.State(Id);
        _runRefusal = engine.CanRun(Id);
        _history = engine.History(Id);
        (_lastReply, _proposed, _latest) = HasAgent ? ReadReplies(engine) : (null, null, null);
        ShowProposal();
        ShowState();
        OnPropertyChanged(nameof(ShowsApprove));
        foreach (var property in RunDependents)
        {
            OnPropertyChanged(property);
        }

        foreach (var command in new[] { _run, _retry, _stop, _approve, _sendBack, _continue, _markResolved, _openWorktree, _send, _stopAndSend, _openInTerminal })
        {
            command.NotifyCanExecuteChanged();
        }
    }

    /// <summary>A review that runs on this node's result marks it, or nothing does.</summary>
    internal void ShowReviewer(string? reviewer)
    {
        if (_reviewer != reviewer)
        {
            _reviewer = reviewer;
            ShowState();
            OnPropertyChanged(nameof(ReviewerTip));
        }
    }

    private string? _reviewer;

    /// <summary>
    /// Shows the proposal of a planner's latest reply, unless the person closed it or accepting it would change nothing. An
    /// open proposal keeps the person's choices while its reply stays the latest.
    /// </summary>
    internal void ShowProposal()
    {
        if (_task.Blueprint.Work is not WorkSpec.Agent { Proposes: true } || _proposed is not { } reply)
        {
            Proposal = null;
            return;
        }

        var identity = (Id, reply.Index);
        var shown = _proposal is { } open && open.Identity.Equals(identity) ? open
            : IDevelop.Nodes.Proposal.Read(Id, reply.Index, reply.Text, _canvas.HandlesOf(Id), _canvas.FindBlueprint) switch
            {
                ProposalRead.None => null,
                var read => new ProposalViewModel(_canvas, read, identity),
            };
        if (shown is not null && _canvas.IsClosed(shown))
        {
            shown = null;
        }

        shown?.Refresh();
        Proposal = shown;
    }

    /// <summary>
    /// The agent's latest reply, its latest reply that ends with a result block, which may hold a proposal, and what it did
    /// last, a message or a tool.
    /// </summary>
    private ((int, string)? Last, (int, string)? Proposed, string? Latest) ReadReplies(WorkflowEngine engine)
    {
        var lines = Log(engine).Lines;
        (int, string)? last = null;
        string? latest = null;
        for (var index = lines.Count - 1; index >= 0; index--)
        {
            latest ??= lines[index] switch
            {
                ConversationLine.Agent said => ResultBlock.Prose(said.Text),
                ConversationLine.Activity activity => activity.Text,
                _ => null,
            };
            if (lines[index] is not ConversationLine.Agent { Subagent: null } agent) continue;
            last ??= (index, agent.Text);
            // A later reply that proposes nothing keeps the proposal before it.
            if (ResultBlock.Read(agent.Text) is ResultBlock.Readable { Status: "proposal" } or ResultBlock.Unreadable) return (last, (index, agent.Text), latest);
        }

        return (last, null, latest);
    }

    /// <summary>
    /// The node's log, which the node follows while it is on the canvas: a line the agent writes in a turn changes no
    /// state, so the card reads the log again, once per batch, when it grows.
    /// </summary>
    private IConversation Log(WorkflowEngine engine)
    {
        if (_log is null)
        {
            _log = engine.Conversation(Id);
            _log.Changed += OnLogChanged;
        }

        return _log;
    }

    private void OnLogChanged()
    {
        if (Interlocked.Exchange(ref _logChanged, 1) == 1)
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _logChanged, 0);
            if (_log is not null)
            {
                ShowEngine();
            }
        });
    }

    /// <summary>The node leaves the canvas, or its project closes, so it stops following its log.</summary>
    internal void StopFollowingLog()
    {
        if (_log is { } log)
        {
            log.Changed -= OnLogChanged;
            log.Dispose();
            _log = null;
        }
    }

    private void ShowState()
    {
        State = NodeStates.Of(_engineState);
        Role = _proposal is { HasItems: true } ? NodeRole.Proposing : _reviewer is not null ? NodeRole.UnderReview : NodeRole.None;
    }

    private bool Waits(params string[] why) => _engineState is EngineState.InputRequired waiting && why.Contains(waiting.Why);

    private void Approve()
    {
        Act(engine => engine.Approve(Id));
    }

    private void SendBack()
    {
        var note = SendBackNote.Trim();
        if (Act(engine => engine.SendBack(Id, note)))
        {
            SendBackNote = "";
        }
    }

    private void Send(bool interrupt)
    {
        var text = Draft;
        if (Act(engine => engine.Send(Id, text, interrupt)) && Draft == text)
        {
            Draft = "";
        }
    }

    private async void OpenInTerminal()
    {
        using var conversation = _canvas.Engine.Conversation(Id);
        if (conversation.TerminalCommand is not { } command)
        {
            _canvas.Notice("The node has no session to continue in a terminal.");
            return;
        }

        try
        {
            await _canvas.Copy(command);
            _canvas.Notice($"Copied the command that continues \"{Title}\" in a terminal: {command}");
        }
        catch (Exception)
        {
            _canvas.Notice($"Couldn't copy the command. Run it in a terminal: {command}");
        }
    }

    /// <summary>Runs an engine command, and says in the notice why the engine refused it. Returns whether it went through.</summary>
    private bool Act(Func<WorkflowEngine, Task> command)
    {
        try
        {
            _ = command(_canvas.Engine);
            _canvas.Notice(null);
            _canvas.ShowEngine();
            return true;
        }
        catch (InvalidOperationException e)
        {
            _canvas.Notice(e.Message);
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _canvas.Notice($"Couldn't write the log of \"{Title}\". {e.Message}");
            _canvas.ShowEngine();
            return false;
        }
    }
}
