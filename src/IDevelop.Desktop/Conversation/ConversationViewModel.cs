using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Execution;
using IDevelop.Desktop.Mvvm;
using IDevelop.Engine;
using IDevelop.Nodes;
using EngineState = IDevelop.Engine.NodeState;
using NodeState = IDevelop.Desktop.Canvas.NodeState;
using IDevelop.Clients;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Conversation;

/// <summary>A conversation to open: the node in its workflow.</summary>
public sealed record ConversationTarget(WorkflowCanvasViewModel Canvas, TaskId Task);

/// <summary>What the attention card over the composer asks of the person, from the node's state.</summary>
public enum AttentionCard { None, Question, Reply, Approval, Conflict, Interrupted, Failed, Stale }

/// <summary>
/// One node's conversation as the view shows it: its log's lines, the latest page first, and the composer. It reads
/// through one <see cref="IConversation"/>, writes through the node's commands, and keeps nothing the person typed; the
/// node's <see cref="ConversationState"/> does. Closing it never stops the node.
/// </summary>
public sealed class ConversationViewModel : ObservableObject, IDisposable
{
    internal const int PageSize = 200;

    // How wide the view counts a chip when it decides how many fit. A chip that has room takes more.
    private const double MaxChipWidth = 120;

    // On one side of the strip: its label, and the +3 chip.
    private const double StripLabelWidth = 44;
    private const double MoreChipWidth = 32;
    private const double ChipGap = 4;

    private readonly IConversation _conversation;
    private readonly Func<string, Task> _copy;
    private readonly RelayCommand _loadEarlier;
    private readonly UiBatch _changed;

    // The lines from _start up to _shownEnd show as rows.
    private int _start = -1;
    private int _shownStart;
    private int _shownEnd;
    private int _chipRoom = 2;
    private bool _disposed;

    internal ConversationViewModel(ConversationTarget target, ConversationState state, IConversation conversation, Func<string, Task> copy)
    {
        Target = target;
        State = state;
        _conversation = conversation;
        _copy = copy;
        Node = target.Canvas.Nodes.First(node => node.Id == target.Task);
        _loadEarlier = new RelayCommand(LoadEarlier, () => HasEarlier);
        // The log tells of each line, often several at once and off the UI thread, so the view reads once per batch.
        _changed = new UiBatch(Refresh);
        state.PropertyChanged += OnStateChanged;
        Node.PropertyChanged += OnNodeChanged;
        conversation.Changed += _changed.Request;
        Refresh();
    }

    public ConversationTarget Target { get; }

    /// <summary>The conversation's workflow, whose bar the view repeats while it covers the canvas.</summary>
    public WorkflowCanvasViewModel Canvas => Target.Canvas;

    /// <summary>The node, which the view reads and raises for its state, questions, and commands.</summary>
    public TaskNodeViewModel Node { get; }

    internal ConversationState State { get; }

    /// <summary>The project and workflow the node belongs to, as the breadcrumb names them.</summary>
    public string Breadcrumb => $"{Target.Canvas.Project.Name} › {Target.Canvas.Name}";

    /// <summary>The breadcrumb's tooltip, which adds what the workflow's bar says.</summary>
    public string BreadcrumbTip => Canvas.ShowsBar ? $"{Breadcrumb}\n{Canvas.BarSummary}" : Breadcrumb;

    /// <summary>The composer takes the keys when the conversation opens only while the node waits for the person's words.</summary>
    internal bool WaitsForWords => Card is AttentionCard.Reply or AttentionCard.Question or AttentionCard.Interrupted;

    public string AgentName => Node.Definition.Execution?.Client is { } client ? ClientCatalog.Name(client) : "The agent";

    /// <summary>
    /// How the agent asks, which the header's info glyph holds: Claude Code asks with its choices while its turn runs, and any
    /// other client asks in its reply, which the person answers with a message.
    /// </summary>
    public string Limitations => Node.Definition.Execution?.Client == ClientId.ClaudeCode
        ? "Claude Code asks with its choices while its turn runs. Choose, or answer in your own words."
        : "The agent's questions arrive in its reply. Answer them with your message.";

    public ObservableCollection<ConversationItemViewModel> Items { get; } = [];

    public string Draft
    {
        get => State.Draft;
        set => State.Draft = value;
    }

    public bool HasDraft => !string.IsNullOrWhiteSpace(State.Draft);

    /// <summary>What Send does now, while that is not plain, or null.</summary>
    public string? ComposerHint => Node.IsAsking ? "Sending answers the agent's question with your choices and your words."
        : Node.State == NodeState.Running ? "Your message waits for this turn to end. Stop and send ends the turn first."
        : null;

    /// <summary>Send's tooltip. While a draft shows the key beside the button, the tooltip leaves it out.</summary>
    public string SendHelp => string.Join("\n", new[] { HasDraft ? "Send" : "Send (Ctrl+Enter)", ComposerHint }.OfType<string>());

    /// <summary>The composer's placeholder, which says what a message does now.</summary>
    public string Watermark => Node.IsAsking ? "Answer in your own words"
        : Card == AttentionCard.Reply ? $"Reply to {AgentName}"
        : Node.Definition.Execution?.Client is { } client ? $"Message {ClientCatalog.Name(client)}" : "Message the agent";

    /// <summary>The composer's model chip: the model and level, or the agent's name while it has no model.</summary>
    public string ModelChip => Node.AgentModel is { Length: > 0 } model ? model : Node.AgentClient;

    /// <summary>A node that has run no turn has nothing to read yet, so the transcript offers its Run.</summary>
    public bool ShowsEmpty => Node.State == NodeState.Idle && Items.Count == 0;

    /// <summary>The line over the composer shows while a turn runs.</summary>
    public bool ShowsWorking => Node.State == NodeState.Running;

    public string WorkingLabel => Node.IsAsking ? "Waiting for your answer" : Node.State == NodeState.Running && Node.StatusLabel != "Running" ? Node.StatusLabel : "Working";

    /// <summary>The first line of what the agent did last, unless the transcript's last row already says it.</summary>
    public string? WorkingLine => Node.Latest?.Split('\n', 2)[0] is { } line && !(Items.LastOrDefault() is StepGroupItemViewModel steps && steps.LastLine == line) ? line : null;

    public AttentionCard Card => Node.IsAsking ? AttentionCard.Question : Node.EngineState switch
    {
        EngineState.InputRequired { Why: EngineState.Approval or EngineState.RoundLimit } => AttentionCard.Approval,
        EngineState.InputRequired { Why: EngineState.Conflict } => AttentionCard.Conflict,
        EngineState.InputRequired { Why: EngineState.Interrupted } => AttentionCard.Interrupted,
        EngineState.InputRequired => AttentionCard.Reply,
        EngineState.Failed => AttentionCard.Failed,
        EngineState.Stale => AttentionCard.Stale,
        _ => AttentionCard.None,
    };

    public bool ShowsCard => Card != AttentionCard.None;

    public bool IsQuestionCard => Card == AttentionCard.Question;

    public bool IsProblemCard => Card == AttentionCard.Failed;

    public bool IsWarningCard => Card == AttentionCard.Stale;

    /// <summary>Approve is the card's main action on an approval, and only an alternative to a reply.</summary>
    public bool IsApprovalCard => Card == AttentionCard.Approval;

    public bool ShowsCardRetry => Card is AttentionCard.Failed or AttentionCard.Stale or AttentionCard.Interrupted;

    public bool IsRetryPrimary => Card is AttentionCard.Failed or AttentionCard.Stale;

    public bool ShowsChangeModel => Card == AttentionCard.Failed && Node.HasAgent;

    public string CardTitle => Card switch
    {
        AttentionCard.Question => Node.Questions is [var only] ? only.Header : Node.Questions.Count == 0 ? "The agent asks. Answer below." : "Question",
        AttentionCard.Reply => HasOwnReason ? "The agent asks. Answer below." : "The agent waits for your reply",
        AttentionCard.Approval => "Approval",
        AttentionCard.Conflict => "Conflict",
        AttentionCard.Interrupted => "Interrupted",
        AttentionCard.Failed => "Failed",
        AttentionCard.Stale => "Stale",
        _ => "",
    };

    /// <summary>What the card explains, or null. A question's rows say it, and a wait on the agent's words leaves them to the message above.</summary>
    public string? CardBody => Card switch
    {
        AttentionCard.None or AttentionCard.Question => null,
        AttentionCard.Reply => HasOwnReason && Node.StateDetail?.Trim() != Node.LastReply?.Trim() ? Node.StateDetail : null,
        _ => Node.StateDetail,
    };

    /// <summary>A reply's card says what its action does in the action's words and tooltip, so it holds no hint.</summary>
    public string? CardHint => null;

    /// <summary>Approve's words: on a wait for a reply it ends the node with the agent's message, and never the draft, so it says so.</summary>
    public string ApproveLabel => Card == AttentionCard.Reply ? "Accept the agent's message as the result" : "Approve";

    /// <summary>The card's action ends the node on the agent's words, so it stays quiet beside the composer.</summary>
    public bool IsReplyCard => Card == AttentionCard.Reply;

    /// <summary>A person who writes a reply has chosen to go on, so a reply's way to end the node gives way to Send.</summary>
    public bool ShowsCardApprove => Node.ShowsApprove;

    /// <summary>Stop is the composer's one action while a turn runs and nothing is written.</summary>
    public bool ShowsComposerStop => Node.ShowsStop && (!HasDraft || Node.IsAsking);

    /// <summary>Send has something to send: a draft, the choices of a question, or anything at all once no turn runs.</summary>
    public bool ShowsSend => HasDraft || !Node.ShowsStop || Node.IsAsking;

    /// <summary>A draft during a turn offers the way to send it now.</summary>
    public bool ShowsStopAndSend => Node.CanStopAndSend && HasDraft;

    /// <summary>What Approve hands on, and to which nodes.</summary>
    public string ApproveHelp => Card == AttentionCard.Reply
        ? $"Accepting completes the node and hands the agent's message above to {Recipients}."
        : $"Makes the agent's last reply the result for {Recipients}.";

    // "A and B", "A, B, and 2 more", or the node itself when nothing reads it.
    private string Recipients => Node.Outputs.Select(link => link.Node.Title).ToList() switch
    {
        [] => "this node",
        [var one] => one,
        [var one, var two] => $"{one} and {two}",
        [var one, var two, .. var rest] => $"{one}, {two}, and {rest.Count} more",
    };

    /// <summary>What an approval offers, which the last wait's marker holds, or null.</summary>
    public string? CardOffered => Card == AttentionCard.Approval
        ? Items.OfType<MarkerItemViewModel>().LastOrDefault(marker => marker.Kind == MarkerKind.Waiting)?.Offered
        : null;

    /// <summary>A reply's one action shares the header's line, so a short dock keeps it in view.</summary>
    public bool IsCompactCard => Card == AttentionCard.Reply || (Card == AttentionCard.Question && Node.Questions.Count == 0);

    /// <summary>The card has words to read under its header.</summary>
    public bool ShowsCardBody => CardBody is not null || CardOffered is not null || CardHint is not null || Node.Questions.Count > 0 && Node.IsAsking;

    /// <summary>The person opened the note box of Send back.</summary>
    public bool IsSendBackOpen
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>The input strip's chips in view.</summary>
    public IReadOnlyList<NodeLink> ShownInputs => [.. Node.Inputs.Take(ShownCount(Node.Inputs.Count, _chipRoom))];

    public IReadOnlyList<NodeLink> MoreInputs => [.. Node.Inputs.Skip(ShownCount(Node.Inputs.Count, _chipRoom))];

    public IReadOnlyList<NodeLink> ShownOutputs => [.. Node.Outputs.Take(ShownCount(Node.Outputs.Count, _chipRoom))];

    public IReadOnlyList<NodeLink> MoreOutputs => [.. Node.Outputs.Skip(ShownCount(Node.Outputs.Count, _chipRoom))];

    public bool HasInputs => Node.Inputs.Count > 0;

    public bool HasOutputs => Node.Outputs.Count > 0;

    public bool HasMoreInputs => MoreInputs.Count > 0;

    public bool HasMoreOutputs => MoreOutputs.Count > 0;

    public string MoreInputsLabel => $"+{MoreInputs.Count}";

    public string MoreOutputsLabel => $"+{MoreOutputs.Count}";

    public bool ShowsStrip => HasInputs || HasOutputs;

    /// <summary>The first turn shows while the transcript reaches back to the start and the node has a task to show.</summary>
    public bool ShowsTask => !HasEarlier && Items.Count > 0 && TaskFields.Count > 0;

    /// <summary>The earlier nodes that the agent read, by name, or null.</summary>
    public string? TaskInputLine => HasInputs ? $"Input: {string.Join(", ", Node.Inputs.Select(link => link.Node.Title))}" : null;

    /// <summary>The opened task names the earlier nodes it read.</summary>
    public bool ShowsTaskInputLine => IsTaskOpen && HasInputs;

    /// <summary>The node's own fields, which the agent received as its task.</summary>
    public IReadOnlyList<FieldViewModel> TaskFields => [.. Node.Fields.Where(entry => !string.IsNullOrWhiteSpace(entry.Text))];

    /// <summary>The task's first line, which shows while the first turn is folded.</summary>
    public string? TaskPreview => TaskFields.FirstOrDefault()?.Text.Split('\n', 2)[0] is { } line ? ResultViewModel.Plain(line) : null;

    /// <summary>The person opened the first turn.</summary>
    public bool IsTaskOpen
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(ShowsTaskInputLine));
            }
        }
    }

    private bool HasOwnReason => Node.EngineState is EngineState.InputRequired { Why: not EngineState.Reply };

    public bool HasEarlier => _start > 0;

    public ICommand LoadEarlierCommand => _loadEarlier;

    /// <summary>Asks the view to follow the end again, as after the person sends.</summary>
    internal event Action? FollowRequested;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _conversation.Changed -= _changed.Request;
        _conversation.Dispose();
        State.PropertyChanged -= OnStateChanged;
        Node.PropertyChanged -= OnNodeChanged;
    }

    internal Task CopyAsync(string text) => _copy(text);

    /// <summary>Fits the strip's chips to its width. Each side shows what fits in whole chips, and the rest goes behind its +3 chip.</summary>
    internal void FitChips(double width)
    {
        var room = (width - 12) / 2 - StripLabelWidth - MoreChipWidth;
        var chips = Math.Max(1, (int)((room + ChipGap) / (Math.Clamp(room, 48, MaxChipWidth) + ChipGap)));
        if (chips != _chipRoom)
        {
            _chipRoom = chips;
            RaiseStrip();
        }
    }

    /// <summary>The digit key picks its choice. It works only while the composer is empty, where a digit is no message.</summary>
    internal bool PickChoice(int digit)
    {
        if (!Node.IsAsking || State.Draft.Length > 0)
        {
            return false;
        }

        if (Node.Questions.SelectMany(question => question.Choices).FirstOrDefault(choice => choice.Number == digit) is not { } choice)
        {
            return false;
        }

        choice.IsChosen = !choice.IsChosen;
        return true;
    }

    private static int ShownCount(int count, int room) => count <= room ? count : room;

    /// <summary>
    /// Shows the log's lines from the first one shown to the end. The log only grows, so the rows already shown stay, and new
    /// lines join at the end. A row of steps takes each new tool line that follows it.
    /// </summary>
    private void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        var lines = _conversation.Lines;
        if (_start < 0)
        {
            // A conversation opened again starts at the line the person last read, or at the latest page.
            _start = Math.Max(0, Math.Min(lines.Count - PageSize, State.Anchor?.Entry ?? int.MaxValue));
            _shownStart = _shownEnd = _start;
        }

        if (_start < _shownStart)
        {
            List<ConversationItemViewModel> earlier = [];
            StepGroupItemViewModel? group = null;
            for (var id = _start; id < _shownStart; id++)
            {
                if (Row(lines, id, ref group) is { } row)
                {
                    earlier.Add(row);
                }
            }

            // The nearest row goes in first, as a person scrolling up would meet it.
            foreach (var row in Enumerable.Reverse(earlier))
            {
                Items.Insert(0, row);
            }

            _shownStart = _start;
        }

        var last = Items.LastOrDefault() as StepGroupItemViewModel;
        for (; _shownEnd < lines.Count; _shownEnd++)
        {
            if (Row(lines, _shownEnd, ref last) is { } row)
            {
                Items.Add(row);
            }
        }

        Relate();
        OnPropertyChanged(nameof(HasEarlier));
        OnPropertyChanged(nameof(ShowsEmpty));
        OnPropertyChanged(nameof(ShowsTask));
        OnPropertyChanged(nameof(CardOffered));
        OnPropertyChanged(nameof(CardBody));
        OnPropertyChanged(nameof(CardHint));
        OnPropertyChanged(nameof(ShowsCardBody));
        _loadEarlier.NotifyCanExecuteChanged();
    }

    // The row for a line, or null when the line joined the group before it.
    private ConversationItemViewModel? Row(IReadOnlyList<ConversationLine> lines, int id, ref StepGroupItemViewModel? group)
    {
        if (lines[id] is ConversationLine.Activity { IsTool: true } tool)
        {
            var step = new ActivityItemViewModel(id, tool);
            if (group is not null)
            {
                group.Add(step);
                return null;
            }

            return group = new StepGroupItemViewModel(step);
        }

        group = null;
        return lines[id] switch
        {
            ConversationLine.Person person => new MessageItemViewModel(id, person.Text, null, this),
            ConversationLine.Agent agent => new MessageItemViewModel(id, agent.Text, agent, this),
            ConversationLine.Activity notice => new ActivityItemViewModel(id, notice),
            ConversationLine.Marker marker => new MarkerItemViewModel(id, marker, Attempt(lines, id), AgentAbove(lines, id)),
            _ => throw new InvalidOperationException("Unknown conversation line."),
        };
    }

    private static int Attempt(IReadOnlyList<ConversationLine> lines, int id) =>
        lines.Take(id + 1).Count(line => line is ConversationLine.Marker { Kind: MarkerKind.Fresh });

    private static string? AgentAbove(IReadOnlyList<ConversationLine> lines, int id)
    {
        for (var at = id - 1; at >= 0; at--)
        {
            if (lines[at] is ConversationLine.Agent agent)
            {
                return ResultBlock.Prose(agent.Text);
            }

            if (lines[at] is not ConversationLine.Activity)
            {
                return null;
            }
        }

        return null;
    }

    // What each row owes the row before it: the author label, the tight gap over steps, and the card that holds the last marker.
    private void Relate()
    {
        var label = true;
        ConversationItemViewModel? before = null;
        foreach (var item in Items)
        {
            switch (item)
            {
                case MessageItemViewModel { IsAgent: true } agent:
                    agent.ShowsAuthor = label || agent.IsSubagent;
                    label = agent.IsSubagent;
                    break;
                case MessageItemViewModel or MarkerItemViewModel:
                    label = true;
                    break;
            }

            before?.IsBeforeSteps = item is StepGroupItemViewModel;
            before = item;
        }

        foreach (var marker in Items.OfType<MarkerItemViewModel>())
        {
            marker.IsHeldByCard = marker == before && (Card is AttentionCard.Reply or AttentionCard.Approval or AttentionCard.Question && marker.Kind == MarkerKind.Waiting
                || Card == AttentionCard.Failed && marker.Kind == MarkerKind.Failed
                || Node.State == NodeState.Completed && marker.Kind == MarkerKind.Completed
                || Card == AttentionCard.Conflict && marker.Kind == MarkerKind.Conflict);
        }

        ShowResults();
    }

    // A result shows under the newest message that reported it.
    private void ShowResults()
    {
        var left = Node.Results.ToList();
        foreach (var message in Items.OfType<MessageItemViewModel>().Reverse().Where(message => message.IsAgent && !message.IsSubagent))
        {
            var result = left.FirstOrDefault(entry => entry.Report.Trim() == message.Text.Trim());
            message.Result = result;
            result?.ReadChanges(Node.ReadChanges);
            left.Remove(result!);
        }
    }

    private void RaiseStrip()
    {
        foreach (var name in StripProperties)
        {
            OnPropertyChanged(name);
        }
    }

    private void RaiseState()
    {
        foreach (var name in StateProperties)
        {
            OnPropertyChanged(name);
        }

        if (Card != AttentionCard.Approval)
        {
            IsSendBackOpen = false;
        }

        Relate();
    }

    private void LoadEarlier()
    {
        _start = Math.Max(0, _start - PageSize);
        Refresh();
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConversationState.Draft))
        {
            OnPropertyChanged(nameof(Draft));
            OnPropertyChanged(nameof(HasDraft));
            OnPropertyChanged(nameof(SendHelp));
            OnPropertyChanged(nameof(ShowsCardApprove));
            OnPropertyChanged(nameof(ShowsComposerStop));
            OnPropertyChanged(nameof(ShowsSend));
            OnPropertyChanged(nameof(ShowsStopAndSend));
            if (State.Draft.Length == 0)
            {
                FollowRequested?.Invoke();
            }
        }
    }

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TaskNodeViewModel.AgentLabel):
                OnPropertyChanged(nameof(AgentName));
                OnPropertyChanged(nameof(Watermark));
                OnPropertyChanged(nameof(Limitations));
                OnPropertyChanged(nameof(ModelChip));
                break;
            case nameof(TaskNodeViewModel.EngineState) or nameof(TaskNodeViewModel.IsAsking):
                RaiseState();
                break;
            case nameof(TaskNodeViewModel.Latest):
                OnPropertyChanged(nameof(WorkingLine));
                break;
            case nameof(TaskNodeViewModel.Results):
                RaiseStrip();
                ShowResults();
                break;
            case nameof(TaskNodeViewModel.Inputs) or nameof(TaskNodeViewModel.Outputs):
                RaiseStrip();
                OnPropertyChanged(nameof(CardHint));
                OnPropertyChanged(nameof(ApproveHelp));
                break;
        }
    }

    private static readonly string[] StateProperties =
    [
        nameof(ComposerHint), nameof(SendHelp), nameof(Watermark), nameof(IsReplyCard), nameof(ShowsCardApprove),
        nameof(ShowsComposerStop), nameof(ShowsSend), nameof(ShowsStopAndSend), nameof(ShowsEmpty), nameof(ShowsWorking), nameof(WorkingLabel), nameof(WorkingLine),
        nameof(Card), nameof(ShowsCard), nameof(IsQuestionCard), nameof(IsProblemCard), nameof(IsWarningCard), nameof(IsApprovalCard),
        nameof(ShowsCardRetry), nameof(IsRetryPrimary), nameof(ShowsChangeModel), nameof(CardTitle), nameof(CardBody), nameof(CardHint), nameof(ApproveLabel), nameof(ApproveHelp), nameof(CardOffered), nameof(IsCompactCard), nameof(ShowsCardBody),
    ];

    private static readonly string[] StripProperties =
    [
        nameof(ShownInputs), nameof(MoreInputs), nameof(ShownOutputs), nameof(MoreOutputs), nameof(HasInputs), nameof(HasOutputs),
        nameof(HasMoreInputs), nameof(HasMoreOutputs), nameof(MoreInputsLabel), nameof(MoreOutputsLabel), nameof(ShowsStrip),
    ];
}
