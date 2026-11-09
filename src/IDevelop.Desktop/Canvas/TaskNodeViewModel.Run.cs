using System.Diagnostics.CodeAnalysis;
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
        nameof(EngineState), nameof(WaitReason), nameof(IsIdle), nameof(IsRunningNow), nameof(StatusLabel), nameof(StatusTip), nameof(StateDetail), nameof(Tone), nameof(IsWaiting), nameof(RunRefusal), nameof(ShowsRun),
        nameof(ShowsRetry), nameof(ShowsStop), nameof(ShowsApprove), nameof(ApproveLabel), nameof(ShowsSendBack), nameof(ShowsContinue), nameof(ShowsMarkResolved),
        nameof(CanStopAndSend), nameof(HasResults), nameof(HasNoOutput), nameof(OutputLine), nameof(CardResult), nameof(CardResultDetail), nameof(CardResultIsCommit), nameof(LastReply), nameof(ReviewSummary),
        nameof(Preview), nameof(Latest), nameof(Questions), nameof(IsAsking),
        nameof(ShowsGoesTo), nameof(HeaderActions), nameof(ComposerHoldsAction), nameof(IsStale), nameof(RetryIsInMenu), nameof(StopIsInMenu), nameof(RetryLabel), nameof(HasExtraPage), nameof(ExtraTitle), nameof(ExtraGlyph), nameof(ExtraBadge),
    ];

    private EngineState _engineState = new EngineState.Idle();
    private AgentQuestion? _asked;
    private IReadOnlyList<QuestionViewModel> _questions = [];
    private string? _runRefusal;
    private IReadOnlyList<NodeResult> _history = [];
    private (int Index, string Text)? _lastReply;
    private (int Index, string Text)? _proposed;
    private IReadOnlyList<ResultViewModel> _results = [];
    private IReadOnlyList<FindingViewModel> _findings = [];
    private (IReadOnlyList<NodeResult>, NodeState, string?) _shownRows;
    private string? _latest;
    private System.Collections.Immutable.ImmutableArray<Clients.BackgroundTask> _background = [];
    private IConversation? _log;
    private UiBatch _logChanged = null!;
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

    /// <summary>What the node needs from the person now, which its glyph tells apart.</summary>
    public WaitReason WaitReason => NodeStates.Why(_engineState);

    /// <summary>"Running", "Done", or what the node needs, such as "Needs you · Approval".</summary>
    public string StatusLabel => NodeStates.Label(_engineState, _background);

    /// <summary>The status's tooltip: its label and, where the state has one, its sentence, which a short inspector shows only here.</summary>
    public string StatusTip => StateDetail is { Length: > 0 } detail ? $"{StatusLabel}\n{detail}" : StatusLabel;

    /// <summary>What the node needs from the person, or why it failed or went stale, or null when its status says enough.</summary>
    public string? StateDetail => NodeStates.Detail(_engineState);

    /// <summary>The state's sentence in one short line, which a short inspector shows in place of <see cref="StateDetail"/>.</summary>
    public StatusTone Tone => NodeStates.Tone(State);

    /// <summary>The node waits for the person.</summary>
    public bool IsWaiting => State == NodeState.InputRequired;

    /// <summary>The node has not run, so its status pill shows no glyph, as Idle has none on any surface.</summary>
    public bool IsIdle => State == NodeState.Idle;

    /// <summary>The node runs now, so a list's state glyph spins.</summary>
    public bool IsRunningNow => State == NodeState.Running;

    /// <summary>Why the engine would not start the node now, which keeps Run and Retry off and is their tooltip, or null.</summary>
    public string? RunRefusal => State == NodeState.Running || IsAsking ? null : _runRefusal;

    /// <summary>Run starts a node that has not run yet.</summary>
    public bool ShowsRun => State == NodeState.Idle;

    /// <summary>Retry starts a fresh session once a turn has ended.</summary>
    public bool ShowsRetry => State is NodeState.Completed or NodeState.Failed or NodeState.Stale or NodeState.InputRequired && !IsAsking;

    public bool ShowsStop => State == NodeState.Running || IsAsking;

    /// <summary>What Retry says: a node that has not failed runs again from here.</summary>
    public string RetryLabel => State == NodeState.Completed ? "Run from here" : "Retry";

    /// <summary>The running turn waits for the person's answer to the agent's question, which Send gives.</summary>
    public bool IsAsking => _asked is not null;

    /// <summary>What the agent asks while its turn runs, each question with its choices, or none.</summary>
    public IReadOnlyList<QuestionViewModel> Questions => _questions;

    /// <summary>
    /// Approve accepts what an Approval node or a reviewer at its round limit offers, or a Chat node's last reply. While a
    /// planner's proposal is open, Accept and Finish approves it instead.
    /// </summary>
    public bool ShowsApprove => (Waits(EngineState.Approval, EngineState.RoundLimit) || Waits(EngineState.Reply)) && Role != NodeRole.Proposing;

    /// <summary>What Approve does in words: on a wait for a reply it makes the agent's message the node's result.</summary>
    public string ApproveLabel => Waits(EngineState.Reply) ? "Accept as result" : "Approve";

    /// <summary>Send back returns the person's note to the nodes before an Approval node or a reviewer at its round limit.</summary>
    public bool ShowsSendBack => Waits(EngineState.Approval, EngineState.RoundLimit);

    /// <summary>A turn that iDevelop's closing interrupted goes on in its session.</summary>
    public bool ShowsContinue => Waits(EngineState.Interrupted);

    /// <summary>The person resolved the conflict in the node's worktree.</summary>
    public bool ShowsMarkResolved => Waits(EngineState.Conflict);

    /// <summary>The note that Send back returns, which the inspector writes.</summary>
    [AllowNull]
    public string SendBackNote
    {
        get;
        set
        {
            if (SetProperty(ref field, value ?? ""))
            {
                _sendBack.NotifyCanExecuteChanged();
            }
        }
    } = "";

    /// <summary>The node's results, newest first, each with whether it is the current one.</summary>
    public IReadOnlyList<ResultViewModel> Results => _results;

    /// <summary>What this node hands on now, in plain words, or null before a first result.</summary>
    public string? HandsOn => _results.Count > 0 ? _results[0].PlainSummary : null;

    /// <summary>
    /// The folded Output section's line: the first line of the latest result's own summary, such as "Added POST /usage with
    /// idempotency keys", or, for a result without a report, its heading and commit, such as "Result 1 · Current · Commit
    /// acc31d9", or "No result yet" (ADR 0065).
    /// </summary>
    public string OutputSummary => _results.Count == 0 ? "No result yet"
        : _results[0].HasReport ? CardText.Line(_results[0].PlainSummary, 200)
        : string.Join(" · ", new[] { _results[0].Heading, _results[0].Commit }.OfType<string>());

    public bool HasResults => _history.Count > 0;

    /// <summary>The node's latest result, or null.</summary>
    internal NodeResult? LatestResult => _history.Count > 0 ? _history[^1] : null;

    /// <summary>What the agent did last: its latest message, or the tool it started after it, or null.</summary>
    public string? Latest => _latest;

    /// <summary>The prose of the agent's latest complete message, without its result block, or null.</summary>
    public string? LastReply => _lastReply is { Text: var text } ? ResultBlock.Prose(text) : null;

    /// <summary>The verdict of a review's latest reply, or null.</summary>
    private Verdict? Verdict => IsReview && _lastReply is { Text: var reply } ? Verdict.Read(reply).Verdict : null;

    /// <summary>"Approved" or "2 open findings" for a review's latest verdict, or null, which its card and the inspector show.</summary>
    public string? ReviewSummary => Verdict is { } verdict
        ? verdict.Approves ? "Approved" : verdict.Findings.Length == 1 ? "1 open finding" : $"{verdict.Findings.Length} open findings"
        : null;

    public IReadOnlyList<FindingViewModel> Findings => _findings;

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

    partial void InitializeRun()
    {
        _logChanged = new UiBatch(() =>
        {
            if (_log is not null)
            {
                ShowEngine();
            }
        });
        _run = new RelayCommand(() => Act(engine => engine.Run(Id)), () => ShowsRun && RunRefusal is null);
        _retry = new RelayCommand(() => Act(engine => engine.Retry(Id)), () => ShowsRetry && RunRefusal is null);
        _stop = new RelayCommand(() => Act(engine => engine.Stop(Id)), () => ShowsStop);
        _approve = new RelayCommand(Approve, () => ShowsApprove);
        _sendBack = new RelayCommand(SendBack, () => ShowsSendBack && !string.IsNullOrWhiteSpace(SendBackNote));
        _continue = new RelayCommand(() => Act(engine => engine.Send(Id, "Continue where you left off.", interrupt: false)), () => ShowsContinue);
        _markResolved = new RelayCommand(() => Act(engine => engine.MarkResolved(Id)), () => ShowsMarkResolved);
        _openWorktree = new RelayCommand(() => UiWork.Start(OpenWorktree, _canvas.Notice), () => ShowsMarkResolved);
        _send = new RelayCommand(() => Send(interrupt: false),
            () => HasAgent && State != NodeState.Idle && (!string.IsNullOrWhiteSpace(Draft) || IsAsking && _questions.All(question => question.HasChoice)));
        _stopAndSend = new RelayCommand(() => Send(interrupt: true), () => CanStopAndSend && !string.IsNullOrWhiteSpace(Draft));
        _openInTerminal = new RelayCommand(() => UiWork.Start(OpenInTerminal, _canvas.Notice), () => HasAgent && State is not (NodeState.Idle or NodeState.Running) && !IsAsking);
    }

    /// <summary>Reads the node from the engine again: its state, why it cannot run, its results, and its agent's latest reply.</summary>
    internal void ShowEngine()
    {
        var engine = _canvas.Engine;
        _engineState = engine.State(Id);
        var asked = (_engineState as EngineState.InputRequired)?.Question;
        if (asked?.Key != _asked?.Key)
        {
            _asked = asked;
            _questions = asked is null ? [] : Asked(asked);
        }

        _runRefusal = engine.CanRun(Id);
        _history = engine.History(Id);
        (_lastReply, _proposed, _latest) = HasAgent ? ReadReplies(engine) : (null, null, null);
        _background = HasAgent ? Log(engine).Background : [];
        ShowProposal();
        ShowState();
        ShowRows();
        foreach (var property in RunDependents)
        {
            OnPropertyChanged(property);
        }

        foreach (var command in new[] { _run, _retry, _stop, _approve, _sendBack, _continue, _markResolved, _openWorktree, _send, _stopAndSend, _openInTerminal })
        {
            command.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Builds the rows of the node's results and findings, and raises them, only when what they show changed.</summary>
    private void ShowRows()
    {
        var shown = (_history, State, IsReview ? _lastReply?.Text : null);
        if (shown == _shownRows)
        {
            return;
        }

        _shownRows = shown;
        _results = [.. _history.Select((result, index) => new ResultViewModel(result, index + 1,
            current: index == _history.Count - 1 && State == NodeState.Completed)).Reverse()];
        _findings = Verdict is { } verdict ? [.. verdict.Findings.Select(finding => new FindingViewModel(finding))] : [];
        OnPropertyChanged(nameof(Results));
        OnPropertyChanged(nameof(CurrentResult));
        OnPropertyChanged(nameof(EarlierResults));
        OnPropertyChanged(nameof(HasEarlierResults));
        OnPropertyChanged(nameof(ExtraBadge));
        OnPropertyChanged(nameof(OutputSummary));
        OnPropertyChanged(nameof(HandsOn));
        OnPropertyChanged(nameof(Findings));
    }

    /// <summary>A review that runs on this node's result marks it, or nothing does.</summary>
    internal void ShowReviewer(string? reviewer)
    {
        if (_reviewer != reviewer)
        {
            _reviewer = reviewer;
            ShowState();
            OnPropertyChanged(nameof(ReviewerTip));
            OnPropertyChanged(nameof(MarkTip));
        }
    }

    private string? _reviewer;

    /// <summary>
    /// Shows the proposal of a planner's latest reply, unless the person closed it or accepting it would change nothing. An
    /// open proposal keeps the person's choices while its reply stays the latest.
    /// </summary>
    internal void ShowProposal()
    {
        if (_task.Blueprint.Work is not WorkSpec.Agent { Proposes: true } || _proposed is not { } reply || _canvas.Engine.Planned(Id) is not { } handles)
        {
            Proposal = null;
            return;
        }

        var identity = (Id, reply.Index);
        var shown = _proposal is { } open && open.Identity.Equals(identity) ? open
            : IDevelop.Nodes.Proposal.Read(Id, reply.Index, reply.Text, handles, _canvas.FindBlueprint) switch
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
            _log.Changed += _logChanged.Request;
        }

        return _log;
    }

    /// <summary>The node leaves the canvas, or its project closes, so it stops following its log.</summary>
    internal void StopFollowingLog()
    {
        if (_log is { } log)
        {
            log.Changed -= _logChanged.Request;
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

    // The digits run on across the questions, so one key picks one choice.
    private List<QuestionViewModel> Asked(AgentQuestion asked)
    {
        var digit = 1;
        List<QuestionViewModel> questions = [];
        foreach (var question in asked.Questions)
        {
            questions.Add(new QuestionViewModel(question, _send.NotifyCanExecuteChanged, digit, asked.Questions.Length > 1));
            digit += question.Options.Length;
        }

        return questions;
    }

    private void Send(bool interrupt)
    {
        var text = Draft;
        if (!interrupt && _asked is { } asked)
        {
            // The draft answers each question without a choice, or the last one when every question has a choice.
            var open = _questions.Where(question => !question.HasChoice).DefaultIfEmpty(_questions[^1]).ToHashSet();
            var reply = new Clients.QuestionsReply([.. _questions.Select(question => question.Answer(open.Contains(question) ? text.Trim() : ""))]);
            if (Act(engine => engine.Answer(Id, asked.Key, reply)) && Draft == text) Draft = "";
            return;
        }

        if (Act(engine => engine.Send(Id, text, interrupt)) && Draft == text)
        {
            Draft = "";
        }
    }

    private async Task OpenWorktree()
    {
        if (_canvas.Engine.Folder(Id) is not { } folder || !await _canvas.OpenFolder(folder))
        {
            _canvas.Notice("Couldn't open the node's worktree.");
        }
    }

    private async Task OpenInTerminal()
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
