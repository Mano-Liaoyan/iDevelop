using System.Collections.ObjectModel;
using System.Windows.Input;
using IDevelop.Desktop.Execution;
using IDevelop.Desktop.Mvvm;
using IDevelop.Engine;
using IDevelop.Nodes;

namespace IDevelop.Desktop.Conversation;

/// <summary>One transcript row. A row keeps its line's place in the log, so a refresh keeps it and its control stays.</summary>
public abstract class ConversationItemViewModel(int id) : ObservableObject
{
    /// <summary>The line's place in the node's conversation, oldest first. A group of steps takes its first line's place.</summary>
    public int Id { get; } = id;

    /// <summary>A group of steps follows the row, so the row keeps close to it.</summary>
    public bool IsBeforeSteps
    {
        get;
        set => SetProperty(ref field, value);
    }
}

public sealed class MessageItemViewModel : ConversationItemViewModel
{
    private readonly string _text;
    private readonly ConversationLine.Agent? _agent;
    private readonly ConversationViewModel _owner;
    private bool _showsAuthor;
    private ResultViewModel? _result;

    /// <param name="id">The line's place in the conversation.</param>
    /// <param name="text">The whole message source, result block included.</param>
    /// <param name="agent">The agent's line, or null for the person's message.</param>
    /// <param name="owner">The conversation that shows the message.</param>
    internal MessageItemViewModel(int id, string text, ConversationLine.Agent? agent, ConversationViewModel owner) : base(id)
    {
        _text = text;
        _agent = agent;
        _owner = owner;
        CopyCommand = new RelayCommand(() => _ = _owner.CopyAsync(_text));
        CopyCommitCommand = new RelayCommand(() => _ = _owner.CopyAsync(Result?.CommitId ?? ""));
    }

    public bool IsPerson => _agent is null;

    internal bool IsAgent => _agent is not null;

    /// <summary>A subagent of the node's agent wrote it, so it shows as the subagent's own.</summary>
    public bool IsSubagent => _agent?.Subagent is not null;

    /// <summary>A person's message always names its sender. The first agent line after a person's message or a marker names its author, and a subagent's line always does.</summary>
    public bool ShowsAuthor
    {
        get => _showsAuthor || IsAsking || IsPerson;
        internal set => SetProperty(ref _showsAuthor, value);
    }

    /// <summary>The node waits for the person's reply to this message, so it reads as a question and not as narration.</summary>
    public bool IsAsking
    {
        get;
        internal set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(ShowsAuthor));
                OnPropertyChanged(nameof(AuthorLine));
            }
        }
    }

    /// <summary>The line over the message.</summary>
    public string AuthorLine => IsAsking ? $"{AuthorLabel} · Waiting for your reply" : AuthorLabel;

    /// <summary>The node's result that this message reported, which shows as a card under it, or null.</summary>
    public ResultViewModel? Result
    {
        get => _result;
        internal set
        {
            if (SetProperty(ref _result, value))
            {
                OnPropertyChanged(nameof(ResultNote));
            }
        }
    }

    public string AuthorLabel => _agent switch
    {
        null => "You",
        { Subagent: { } subagent } => $"Subagent: {subagent}",
        _ => _owner.AgentName,
    };

    /// <summary>The source the message shows. An agent's result block leaves the prose, and <see cref="ResultNote"/> says what it held.</summary>
    public string Text => IsAgent && !IsSubagent ? ResultBlock.Prose(_text) : _text;

    /// <summary>What an agent's readable result block said, such as its question, or why iDevelop could not read it.</summary>
    public string? ResultNote => !IsAgent || IsSubagent ? null : ResultBlock.Read(_text) switch
    {
        ResultBlock.Readable { Status: "asking" } asking => asking.Text("question") is { } question ? $"Asks: {question}" : "Asks a question.",
        ResultBlock.Readable when Result is not null => null,
        ResultBlock.Readable readable => readable.Status is { } status ? $"Reported {status}." : "Reported a result.",
        ResultBlock.Unreadable unreadable => $"iDevelop could not read the result block. {unreadable.Problem}",
        _ => null,
    };

    /// <summary>Copies the whole message source, result block included.</summary>
    public ICommand CopyCommand { get; }

    /// <summary>Copies the whole id of the commit that the message's result names.</summary>
    public ICommand CopyCommitCommand { get; }
}

/// <summary>A notice from the client, which stands alone, or one step of a group.</summary>
public sealed class ActivityItemViewModel(int id, ConversationLine.Activity activity) : ConversationItemViewModel(id)
{
    public string Text => activity.Text;

    /// <summary>The first line, which the row shows. The tooltip holds all of it.</summary>
    public string Line => activity.Text.Split('\n', 2)[0];

    /// <summary>The tool's name, the first word of the line.</summary>
    public string Tool => Line.Split(' ', 2)[0];

    /// <summary>What the tool ran, the rest of the line.</summary>
    public string Command => Line.Split(' ', 2) is [_, var rest] ? rest : "";
}

/// <summary>A run of tool lines as one quiet row. Whether it is open is the view's state, and it starts closed.</summary>
public sealed class StepGroupItemViewModel : ConversationItemViewModel
{
    public StepGroupItemViewModel(ActivityItemViewModel first) : base(first.Id)
    {
        Steps.Add(first);
    }

    public ObservableCollection<ActivityItemViewModel> Steps { get; } = [];

    public bool IsOpen
    {
        get;
        set => SetProperty(ref field, value);
    }

    public string CountLabel => Steps.Count == 1 ? "1 step" : $"{Steps.Count} steps";

    /// <summary>The last step's first line, which shows while the group is closed.</summary>
    public string LastLine => Steps[^1].Line;

    /// <summary>The last step, whose tool and command show while the group is closed.</summary>
    public ActivityItemViewModel Last => Steps[^1];

    /// <summary>The row's name for a screen reader.</summary>
    public string Summary => $"{CountLabel}. Last: {LastLine}";

    internal void Add(ActivityItemViewModel step)
    {
        Steps.Add(step);
        OnPropertyChanged(nameof(CountLabel));
        OnPropertyChanged(nameof(LastLine));
        OnPropertyChanged(nameof(Last));
        OnPropertyChanged(nameof(Summary));
    }
}

/// <param name="id">The line's place in the conversation.</param>
/// <param name="marker">The boundary or event.</param>
/// <param name="attempt">How many fresh sessions began up to this line.</param>
/// <param name="above">The prose of the agent's message before the line, or null.</param>
public sealed class MarkerItemViewModel(int id, ConversationLine.Marker marker, int attempt, string? above) : ConversationItemViewModel(id)
{
    /// <summary>A short heading for the boundary or event, such as "New session" or "Failed".</summary>
    public string Title => marker.Kind switch
    {
        MarkerKind.Fresh => "New session",
        MarkerKind.Completed => "Completed",
        MarkerKind.Waiting => "Waiting for you",
        MarkerKind.Failed => "Failed",
        MarkerKind.Stopped => "Stopped",
        MarkerKind.Dropped => "Message not delivered",
        MarkerKind.Conflict => "Conflict",
        MarkerKind.Feedback => "Sent back",
    };

    /// <summary>What the row says: a session's start names its attempt.</summary>
    public string Heading => marker.Kind == MarkerKind.Fresh ? $"Attempt {attempt} · New session" : Title;

    /// <summary>A session's start is a rule across the column. Every other marker is a line at its left.</summary>
    public bool IsSessionStart => marker.Kind == MarkerKind.Fresh;

    /// <summary>
    /// Why it failed, the message a stop dropped, the conflict, the feedback, or what waits for approval. A wait leaves out what the
    /// agent's message above it said and what the state's own sentence says, which the attention card shows.
    /// </summary>
    public string Detail => string.Join("\n\n", new[] { marker.Kind == MarkerKind.Waiting ? WaitingDetail(marker.Detail) : marker.Detail, marker.Text }
        .Where(text => !string.IsNullOrWhiteSpace(text) && !(marker.Kind == MarkerKind.Waiting && text!.Trim() == above?.Trim())));

    public bool HasDetail => Detail.Length > 0;

    public bool IsProblem => marker.Kind is MarkerKind.Failed or MarkerKind.Dropped;

    public bool IsCompleted => marker.Kind == MarkerKind.Completed;

    public bool IsWaiting => marker.Kind is MarkerKind.Waiting or MarkerKind.Conflict;

    /// <summary>The last line of a node that waits, failed, or completed: the attention card or the header's status says the same, so the row hides.</summary>
    public bool IsHeldByCard
    {
        get;
        internal set => SetProperty(ref field, value);
    }

    internal MarkerKind Kind => marker.Kind;

    /// <summary>What the marker offers for approval, or null.</summary>
    internal string? Offered => marker.Text;

    // A state's fixed sentence belongs to its card. A reason of the agent's own stays.
    private static string? WaitingDetail(string? why) => why is null or NodeState.Approval or NodeState.Reply or NodeState.Conflict or NodeState.Interrupted or NodeState.RoundLimit ? null : why;
}
