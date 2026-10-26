using System.Collections.Immutable;
using IDevelop.Clients;
using IDevelop.Workflows;

namespace IDevelop.Engine;

/// <summary>How a turn of a node's conversation began or ended, which the conversation shows between its messages.</summary>
public enum MarkerKind { Fresh, Completed, Waiting, Failed, Stopped, Dropped, Conflict, Feedback }

/// <summary>One line of a node's conversation, read from its log.</summary>
public abstract record ConversationLine
{
    private ConversationLine() { }

    public sealed record Person(string Text) : ConversationLine;

    /// <summary>A complete message of the node's agent, or of one of its subagents.</summary>
    public sealed record Agent(string Text) : ConversationLine
    {
        public string? Subagent { get; init; }
    }

    /// <summary>A tool the agent started, or a notice from its client.</summary>
    public sealed record Activity(string Text, bool IsTool) : ConversationLine;

    /// <summary>
    /// A fresh session began, or a turn ended. <paramref name="Detail"/> is why it waits, why it failed, the message a stop
    /// dropped, the conflict, or the feedback it sent back.
    /// </summary>
    public sealed record Marker(MarkerKind Kind, string? Detail) : ConversationLine
    {
        /// <summary>What an Approval node, or a reviewer at its round limit, offers the person to approve.</summary>
        public string? Text { get; init; }
    }
}

/// <summary>
/// A node's conversation as its log holds it, which follows the log while it is open. The person writes to it through
/// <see cref="WorkflowEngine.Send"/>.
/// </summary>
public interface IConversation : IDisposable
{
    TaskId Node { get; }

    /// <summary>Every line so far, oldest first.</summary>
    ImmutableList<ConversationLine> Lines { get; }

    /// <summary>The command that continues the node's session in a terminal, or null while a turn runs or it has no session.</summary>
    string? TerminalCommand { get; }

    /// <summary>The background work that the running turn's client waits for, such as a subagent (ADR 0030).</summary>
    ImmutableArray<BackgroundTask> Background { get; }

    /// <summary>Raised after the log grows, on the thread that wrote it.</summary>
    event Action? Changed;
}

internal sealed class NodeConversation : IConversation
{
    private readonly NodeLog _log;
    private readonly Func<string?> _terminal;
    private int _disposed;

    public NodeConversation(TaskId node, NodeLog log, Func<string?> terminal)
    {
        Node = node;
        _log = log;
        _terminal = terminal;
        log.Appended += OnAppended;
    }

    public TaskId Node { get; }

    public ImmutableList<ConversationLine> Lines
    {
        get
        {
            var lines = ImmutableList.CreateBuilder<ConversationLine>();
            string? said = null;
            foreach (var entry in _log.Entries)
            {
                // A client whose verdict carries the reply, such as Antigravity CLI, sends no message of it.
                if (entry is NodeEntry.Agent { Event: AgentEvent.Succeeded { Result: { Length: > 0 } result } } && result != said)
                {
                    lines.Add(new ConversationLine.Agent(result));
                }

                foreach (var line in Read(entry))
                {
                    lines.Add(line);
                    if (line is ConversationLine.Agent { Subagent: null } agent) said = agent.Text;
                    else if (line is ConversationLine.Person or ConversationLine.Marker { Kind: MarkerKind.Fresh }) said = null;
                }
            }

            return lines.ToImmutable();
        }
    }

    public string? TerminalCommand => _terminal();

    public ImmutableArray<BackgroundTask> Background => _log.Record is { Open: true, Background: var tasks } ? tasks : [];

    public event Action? Changed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _log.Appended -= OnAppended;
    }

    private void OnAppended() => Changed?.Invoke();

    internal static IEnumerable<ConversationLine> Read(NodeEntry entry) => entry switch
    {
        NodeEntry.TurnStarted { Session: null } => [new ConversationLine.Marker(MarkerKind.Fresh, null)],
        NodeEntry.PersonMessage message => [new ConversationLine.Person(message.Text)],
        NodeEntry.Agent { Event: AgentEvent.Message { Partial: false } message } => [new ConversationLine.Agent(message.Text) { Subagent = message.Subagent }],
        NodeEntry.Asked asked => [new ConversationLine.Agent(asked.Question.Text)],
        NodeEntry.Answered answered => [new ConversationLine.Person(answered.Text)],
        NodeEntry.Agent { Event: AgentEvent.ToolStarted tool } =>
            [new ConversationLine.Activity(tool.Detail is { Length: > 0 } detail ? $"{tool.Tool} {detail}" : tool.Tool, IsTool: true)],
        NodeEntry.Agent { Event: AgentEvent.Notice notice } => [new ConversationLine.Activity(notice.Text, IsTool: false)],
        NodeEntry.Completed => [new ConversationLine.Marker(MarkerKind.Completed, null)],
        NodeEntry.Waiting waiting => [new ConversationLine.Marker(MarkerKind.Waiting, waiting.Why)
        {
            Text = waiting.Why is NodeState.Approval or NodeState.RoundLimit ? waiting.Text : null,
        }],
        NodeEntry.Failed failed => [new ConversationLine.Marker(MarkerKind.Failed, failed.Reason)],
        NodeEntry.Stopped => [new ConversationLine.Marker(MarkerKind.Stopped, null)],
        NodeEntry.MessageDropped dropped => [new ConversationLine.Marker(MarkerKind.Dropped, dropped.Text)],
        NodeEntry.Conflicted conflict => [new ConversationLine.Marker(MarkerKind.Conflict, conflict.Description)],
        NodeEntry.Feedback feedback => [new ConversationLine.Marker(MarkerKind.Feedback, feedback.Text)],
        _ => [],
    };
}
