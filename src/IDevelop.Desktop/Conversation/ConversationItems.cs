using System.Windows.Input;
using IDevelop.Desktop.Mvvm;
using IDevelop.Engine;
using IDevelop.Nodes;

namespace IDevelop.Desktop.Conversation;

/// <summary>One transcript row. A row keeps its line's place in the log, so a refresh keeps it and its control stays.</summary>
public abstract class ConversationItemViewModel(int id) : ObservableObject
{
    /// <summary>The line's place in the node's conversation, oldest first.</summary>
    public int Id { get; } = id;

    internal static ConversationItemViewModel Create(int id, ConversationLine line, ConversationViewModel owner) => line switch
    {
        ConversationLine.Person person => new MessageItemViewModel(id, person.Text, null, owner),
        ConversationLine.Agent agent => new MessageItemViewModel(id, agent.Text, agent, owner),
        ConversationLine.Activity activity => new ActivityItemViewModel(id, activity),
        ConversationLine.Marker marker => new MarkerItemViewModel(id, marker),
        _ => throw new InvalidOperationException("Unknown conversation line."),
    };
}

public sealed class MessageItemViewModel : ConversationItemViewModel
{
    private readonly string _text;
    private readonly ConversationLine.Agent? _agent;
    private readonly ConversationViewModel _owner;

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
    }

    public bool IsPerson => _agent is null;

    private bool IsAgent => _agent is not null;

    /// <summary>A subagent of the node's agent wrote it, so it shows as the subagent's own.</summary>
    private bool IsSubagent => _agent?.Subagent is not null;

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
        ResultBlock.Readable readable => readable.Status is { } status ? $"Reported {status}. The inspector shows the result." : "Reported a result. The inspector shows it.",
        ResultBlock.Unreadable unreadable => $"iDevelop could not read the result block. {unreadable.Problem}",
        _ => null,
    };

    /// <summary>Copies the whole message source, result block included.</summary>
    public ICommand CopyCommand { get; }
}

public sealed class ActivityItemViewModel(int id, ConversationLine.Activity activity) : ConversationItemViewModel(id)
{
    public string Text => activity.Text;

    /// <summary>The first line, which the row shows. The tooltip holds all of it.</summary>
    public string Line => activity.Text.Split('\n', 2)[0];
}

public sealed class MarkerItemViewModel(int id, ConversationLine.Marker marker) : ConversationItemViewModel(id)
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

    /// <summary>Why it waits, why it failed, the message a stop dropped, the conflict, the feedback, or what waits for approval.</summary>
    public string Detail => string.Join("\n\n", new[] { marker.Kind == MarkerKind.Waiting ? WaitingDetail(marker.Detail) : marker.Detail, marker.Text }
        .Where(text => !string.IsNullOrWhiteSpace(text)));

    public bool IsProblem => marker.Kind is MarkerKind.Failed or MarkerKind.Dropped;

    private static string? WaitingDetail(string? why) =>
        why is null ? null : Canvas.NodeStates.Detail(new NodeState.InputRequired(why));
}
