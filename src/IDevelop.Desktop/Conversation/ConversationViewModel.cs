using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Threading;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Execution;
using IDevelop.Desktop.Mvvm;
using IDevelop.Engine;
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

    private static readonly string[] NodeDependents =
    [
        nameof(StatusLabel), nameof(Tone), nameof(ShowsStopAndSend), nameof(ShowsApprove), nameof(ShowsStop), nameof(ComposerHint), nameof(Questions), nameof(IsAsking),
    ];

    private readonly IConversation _conversation;
    private readonly Func<string, Task> _copy;
    private readonly TaskNodeViewModel? _node;
    private readonly RelayCommand _loadEarlier;
    private int _start = -1;
    private int _posted;
    private bool _disposed;

    internal ConversationViewModel(ConversationTarget target, ConversationState state, IConversation conversation, Func<string, Task> copy)
    {
        Target = target;
        State = state;
        _conversation = conversation;
        _copy = copy;
        _node = target.Canvas.Nodes.FirstOrDefault(node => node.Id == target.Task);
        _loadEarlier = new RelayCommand(LoadEarlier, () => HasEarlier);
        state.PropertyChanged += OnStateChanged;
        if (_node is not null)
        {
            _node.PropertyChanged += OnNodeChanged;
        }

        conversation.Changed += OnConversationChanged;
        Refresh();
    }

    public ConversationTarget Target { get; }

    /// <summary>The conversation's workflow, whose bar the view repeats while it covers the canvas.</summary>
    public WorkflowCanvasViewModel Canvas => Target.Canvas;

    internal ConversationState State { get; }

    public string Title => _node?.Title ?? "Node";

    /// <summary>The project and workflow the node belongs to, as the breadcrumb names them.</summary>
    public string Breadcrumb => $"{Target.Canvas.Project.Name} › {Target.Canvas.Name}";

    public string AgentName => _node?.SelectedClient.Id is { } client ? ClientCatalog.Name(client) : "The agent";

    public string StatusLabel => _node?.StatusLabel ?? "";

    public StatusTone Tone => _node?.Tone ?? StatusTone.Neutral;

    /// <summary>
    /// How the agent asks: Claude Code asks with its choices while its turn runs, and any other client asks in its reply,
    /// which the person answers with a message.
    /// </summary>
    public string Limitations => _node?.SelectedClient.Id == ClientId.ClaudeCode
        ? "Claude Code asks with its choices while its turn runs. Choose, or answer in your own words."
        : "The agent's questions arrive in its reply. Answer them with your message.";

    /// <summary>What the agent asks while its turn runs, each question with its choices, which Send answers.</summary>
    public IReadOnlyList<Canvas.QuestionViewModel> Questions => _node?.Questions ?? [];

    public bool IsAsking => _node?.IsAsking ?? false;

    public ObservableCollection<ConversationItemViewModel> Items { get; } = [];

    public string Draft
    {
        get => State.Draft;
        set => State.Draft = value;
    }

    public string ComposerHint => IsAsking ? "Sending answers the agent's question with your choices and your words." : _node?.State switch
    {
        NodeState.Running => "Your message waits for this turn to end. Stop and send ends the turn first.",
        NodeState.Idle => "Run the node first. A message continues its session.",
        _ => "Sending continues the node's session.",
    };

    public string Watermark => _node?.SelectedClient.Id is { } client ? $"Message {ClientCatalog.Name(client)}" : "Message the agent";

    public bool HasEarlier => _start > 0;

    public bool ShowsStopAndSend => _node?.CanStopAndSend ?? false;

    /// <summary>A Chat node's reply, which approving makes its result.</summary>
    public bool ShowsApprove => _node?.ShowsApprove ?? false;

    public bool ShowsStop => _node?.ShowsStop ?? false;

    public ICommand SendCommand => _node?.SendCommand ?? Disabled;

    public ICommand StopAndSendCommand => _node?.StopAndSendCommand ?? Disabled;

    public ICommand ApproveCommand => _node?.ApproveCommand ?? Disabled;

    public ICommand StopCommand => _node?.StopCommand ?? Disabled;

    public ICommand OpenInTerminalCommand => _node?.OpenInTerminalCommand ?? Disabled;

    public ICommand LoadEarlierCommand => _loadEarlier;

    /// <summary>Asks the view to follow the end again, as after the person sends.</summary>
    internal event Action? FollowRequested;

    private static ICommand Disabled { get; } = new RelayCommand(() => { }, () => false);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _conversation.Changed -= OnConversationChanged;
        _conversation.Dispose();
        State.PropertyChanged -= OnStateChanged;
        if (_node is not null)
        {
            _node.PropertyChanged -= OnNodeChanged;
        }
    }

    internal Task CopyAsync(string text) => _copy(text);

    /// <summary>
    /// Shows the log's lines from the first one shown to the end. The log only grows, so the rows already shown stay, and new
    /// lines join at the end.
    /// </summary>
    internal void Refresh()
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
        }

        var first = Items.Count > 0 ? Items[0].Id : lines.Count;
        for (var id = first - 1; id >= _start; id--)
        {
            Items.Insert(0, ConversationItemViewModel.Create(id, lines[id], this));
        }

        for (var id = Items.Count > 0 ? Items[^1].Id + 1 : _start; id < lines.Count; id++)
        {
            Items.Add(ConversationItemViewModel.Create(id, lines[id], this));
        }

        OnPropertyChanged(nameof(HasEarlier));
        _loadEarlier.NotifyCanExecuteChanged();
    }

    private void LoadEarlier()
    {
        _start = Math.Max(0, _start - PageSize);
        Refresh();
    }

    // The log tells of each line, often several at once and off the UI thread, so the view reads once per batch.
    private void OnConversationChanged()
    {
        if (Interlocked.Exchange(ref _posted, 1) == 1)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _posted, 0);
            Refresh();
        });
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConversationState.Draft))
        {
            OnPropertyChanged(nameof(Draft));
            if (State.Draft.Length == 0)
            {
                FollowRequested?.Invoke();
            }
        }
    }

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TaskNodeViewModel.Title))
        {
            OnPropertyChanged(nameof(Title));
        }
        else if (e.PropertyName is nameof(TaskNodeViewModel.AgentLabel))
        {
            OnPropertyChanged(nameof(AgentName));
            OnPropertyChanged(nameof(Watermark));
            OnPropertyChanged(nameof(Limitations));
        }
        else if (e.PropertyName is nameof(TaskNodeViewModel.EngineState))
        {
            foreach (var property in NodeDependents)
            {
                OnPropertyChanged(property);
            }
        }
    }
}
