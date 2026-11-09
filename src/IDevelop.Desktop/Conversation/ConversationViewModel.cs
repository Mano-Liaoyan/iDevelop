using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Execution;
using IDevelop.Desktop.Mvvm;
using IDevelop.Engine;
using IDevelop.Nodes;
using NodeState = IDevelop.Desktop.Canvas.NodeState;
using IDevelop.Clients;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Conversation;

/// <summary>A conversation to open: the node in its workflow.</summary>
public sealed record ConversationTarget(WorkflowCanvasViewModel Canvas, TaskId Task);

/// <summary>
/// One node's conversation as the view shows it: its log's lines, the latest page first, and the composer. It reads
/// through one <see cref="IConversation"/>, writes through the node's commands, and keeps nothing the person typed; the
/// node's <see cref="ConversationState"/> does. Closing it never stops the node.
/// </summary>
public sealed class ConversationViewModel : ObservableObject, IDisposable
{
    internal const int PageSize = 200;

    private readonly IConversation _conversation;
    private readonly Func<string, Task> _copy;
    private readonly RelayCommand _loadEarlier;
    private readonly UiBatch _changed;

    // The lines from _start up to _shownEnd show as rows.
    private int _start = -1;
    private int _shownStart;
    private int _shownEnd;
    private bool _disposed;

    internal ConversationViewModel(ConversationTarget target, ConversationState state, IConversation conversation, Func<string, Task> copy)
    {
        Target = target;
        State = state;
        _conversation = conversation;
        _copy = copy;
        Node = target.Canvas.Nodes.First(node => node.Id == target.Task);
        Dock = new ActionDockViewModel(Node);
        _loadEarlier = new RelayCommand(LoadEarlier, () => HasEarlier);
        // The log tells of each line, often several at once and off the UI thread, so the view reads once per batch.
        _changed = new UiBatch(Refresh);
        state.PropertyChanged += OnStateChanged;
        Node.PropertyChanged += OnNodeChanged;
        conversation.Changed += _changed.Request;
        Refresh();
    }

    public ConversationTarget Target { get; }

    /// <summary>The node, which the view reads and raises for its state, questions, and commands.</summary>
    public TaskNodeViewModel Node { get; }

    /// <summary>What the node needs from the person now, above the composer.</summary>
    public ActionDockViewModel Dock { get; }

    internal ConversationState State { get; }

    public string AgentName => Node.Definition.Execution?.Client is { } client ? ClientCatalog.Name(client) : "The agent";

    /// <summary>How the agent asks, which the composer's info glyph holds: Claude Code asks with its choices while its turn runs, and any other client asks in its reply.</summary>
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

    /// <summary>Send's tooltip: its name and key, and then what Send does while that is not plain.</summary>
    public string SendHelp => ComposerHint is { } hint ? $"Send (Enter)\n{hint}" : "Send (Enter)";

    /// <summary>Why a message cannot go now, or null. A node that has not run has no session to continue.</summary>
    public string? SendProblem => Node.State == NodeState.Idle ? "Run the node first. A message continues its session." : null;

    /// <summary>The composer takes words: the node has run.</summary>
    public bool CanCompose => SendProblem is null;

    /// <summary>The composer's placeholder, which says what a message does now.</summary>
    public string Watermark => SendProblem ?? (Node.IsAsking ? "Answer in your own words"
        : Dock.Kind == ActionDockKind.Reply ? $"Reply to {AgentName}"
        : Node.Definition.Execution?.Client is { } client ? $"Message {ClientCatalog.Name(client)}" : "Message the agent");

    /// <summary>A node that has run no turn has nothing to read yet.</summary>
    public bool ShowsEmpty => Node.HasAgent && Node.State == NodeState.Idle && Items.Count == 0;

    /// <summary>The Working row shows at the transcript's end while a turn runs and the agent does not ask.</summary>
    public bool ShowsWorking => Node.State == NodeState.Running && !Node.IsAsking;

    public string WorkingLabel => Node.StatusLabel != NodeStates.Word(NodeState.Running) ? Node.StatusLabel : "Working";

    /// <summary>The composer's round button runs the node, which has no session yet.</summary>
    public bool ShowsRun => Node.State == NodeState.Idle;

    /// <summary>The round button stops the turn: it runs, and nothing is written. A question is answered with Send.</summary>
    public bool ShowsComposerStop => Node.State == NodeState.Running && !Node.IsAsking && !HasDraft;

    /// <summary>The round button stops the turn and then sends the draft: it runs, and a draft is written.</summary>
    public bool ShowsStopAndSend => Node.State == NodeState.Running && !Node.IsAsking && HasDraft;

    /// <summary>The round button sends: a draft, or the answer to a question.</summary>
    public bool ShowsSend => !ShowsRun && !ShowsComposerStop && !ShowsStopAndSend;

    /// <summary>The first turn shows while the transcript reaches back to the start and the node has a task to show.</summary>
    public bool ShowsTask => !HasEarlier && TaskFields.Count > 0;

    /// <summary>The node's own fields, which the agent received as its task.</summary>
    public IReadOnlyList<FieldViewModel> TaskFields => [.. Node.Fields.Where(entry => !string.IsNullOrWhiteSpace(entry.Text))];

    /// <summary>The task's first line, which shows while the first turn is folded.</summary>
    public string? TaskPreview => TaskFields.FirstOrDefault()?.Text.Split('\n', 2)[0] is { } line ? ResultViewModel.Plain(line) : null;

    /// <summary>The person opened the first turn.</summary>
    public bool IsTaskOpen
    {
        get;
        set => SetProperty(ref field, value);
    }

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
        Dock.Dispose();
    }

    internal Task CopyAsync(string text) => _copy(text);

    /// <summary>The digit key picks its choice. It works only while the composer is empty, where a digit is no message.</summary>
    internal bool PickChoice(int digit)
    {
        if (State.Draft.Length > 0 || !Node.IsAsking
            || Node.Questions.SelectMany(question => question.Choices).FirstOrDefault(choice => choice.Number == digit) is not { } choice)
        {
            return false;
        }

        choice.IsChosen = !choice.IsChosen;
        return true;
    }

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

    // What each row owes the row before it: its gap, and the Action Dock that holds the last marker. A held row keeps no gap of its own.
    private void Relate()
    {
        Dock.Offered = Dock.Kind is ActionDockKind.Approval or ActionDockKind.RoundLimit
            ? Items.OfType<MarkerItemViewModel>().LastOrDefault(marker => marker.Kind == MarkerKind.Waiting)?.Offered
            : null;
        var last = Items.LastOrDefault();
        foreach (var marker in Items.OfType<MarkerItemViewModel>())
        {
            marker.IsHeldByCard = marker == last && (Dock.Kind is ActionDockKind.Reply or ActionDockKind.Approval or ActionDockKind.RoundLimit or ActionDockKind.Interrupted or ActionDockKind.Question && marker.Kind == MarkerKind.Waiting
                || Dock.Kind == ActionDockKind.Failed && marker.Kind == MarkerKind.Failed
                || Node.State is NodeState.Completed or NodeState.Stale && marker.Kind == MarkerKind.Completed
                || Dock.Kind == ActionDockKind.Conflict && marker.Kind == MarkerKind.Conflict);
        }

        ConversationItemViewModel? before = null;
        foreach (var item in Items)
        {
            item.GapAbove = before is null || item is MarkerItemViewModel { IsHeldByCard: true } ? 0
                : item is MarkerItemViewModel { IsSessionStart: true } ? ConversationItemViewModel.AttemptGap
                : item is MessageItemViewModel { IsPerson: true } || before is MessageItemViewModel { IsPerson: true } ? ConversationItemViewModel.TurnGap
                : ConversationItemViewModel.StepGap;
            before = item;
        }

        ShowResult();
    }

    // The current result shows under the newest message that reported it.
    private void ShowResult()
    {
        var result = Node.CurrentResult;
        var reporting = result is null ? null : Items.OfType<MessageItemViewModel>().LastOrDefault(message => message.IsAgent && !message.IsSubagent && message.Text.Trim() == result.Report.Trim());
        foreach (var message in Items.OfType<MessageItemViewModel>())
        {
            message.Result = message == reporting ? result : null;
        }
    }

    private void RaiseState()
    {
        foreach (var name in StateProperties)
        {
            OnPropertyChanged(name);
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
                break;
            case nameof(TaskNodeViewModel.EngineState) or nameof(TaskNodeViewModel.IsAsking):
                RaiseState();
                break;
            case nameof(TaskNodeViewModel.Results):
                ShowResult();
                break;
        }
    }

    private static readonly string[] StateProperties =
    [
        nameof(ComposerHint), nameof(SendHelp), nameof(SendProblem), nameof(CanCompose), nameof(Watermark),
        nameof(ShowsEmpty), nameof(ShowsWorking), nameof(WorkingLabel), nameof(ShowsRun), nameof(ShowsComposerStop), nameof(ShowsSend), nameof(ShowsStopAndSend),
    ];
}
