using IDevelop.Desktop.Execution;
using IDevelop.Execution;
using IDevelop.Nodes;

namespace IDevelop.Desktop.Canvas;

/// <summary>What an expanded card's first section is about, which picks its glyph and what goes under its heading.</summary>
public enum CardNowKind { Message, Question, Reply, Approval, Result, Stopped, Waits, Proposal, Task }

/// <summary>
/// The first section of an expanded card and of a collapsed card's peek (#94, ADR 0024): a heading, the whole text under it,
/// and a quiet line beside the heading. It shows only what the attempt, the run, and the task record already hold: the
/// agent's latest message, its question or reply, the result's first paragraph, why it stopped, what it waits for, or
/// the task's own first field.
/// </summary>
public sealed record CardNow(CardNowKind Kind, string Heading, string? Text, string? Meta = null)
{
    /// <param name="latest">The task's latest attempt as its conversation reads it, in the run while a run shows the task.</param>
    /// <param name="status">The card's status line, such as "Failed" or "Sent back".</param>
    /// <param name="detail">Why the run holds or stopped the task, or null.</param>
    /// <param name="waitsInRun">A run holds the task until other tasks hand on.</param>
    /// <param name="proposal">The planner's open proposal as its card names it, such as "Proposal · 3 tasks", or null.</param>
    /// <param name="review">A review's summary, such as "Round 2 · 1 open finding", or null.</param>
    public static CardNow Of(NodeState state, NodeRole role, bool hasAgent, AttemptRecord? latest, string status, string? detail,
        bool waitsInRun, string? proposal, string? review, string fieldLabel, string? fieldText)
    {
        if (role == NodeRole.Proposing && proposal is not null)
        {
            return new(CardNowKind.Proposal, "Proposal", $"{proposal}. Review the tasks in the inspector, then accept or dismiss them.");
        }

        return state switch
        {
            NodeState.Running or NodeState.RunningElsewhere or NodeState.Stopping or NodeState.InReview => new(CardNowKind.Message, "Latest message",
                review ?? latest?.Activity.LastOrDefault(line => !line.IsTool)?.Text ?? "No message yet.", latest is null ? null : Joined(Tools(latest), Since(latest))),
            NodeState.Waiting when !hasAgent => new(CardNowKind.Approval, "Waits for your approval",
                detail ?? "Approve what the tasks before it handed on, or send it back."),
            NodeState.Waiting when latest?.Pending is Pending.Question question => new(CardNowKind.Question, "The agent asks", question.Text),
            NodeState.Waiting when latest?.Pending is Pending.Reply => new(CardNowKind.Reply, "The agent replied",
                LastParagraph(ResultBlock.Prose(latest.Result)) ?? RunText.Waiting(latest.Pending)),
            NodeState.Waiting when latest?.Pending is { } pending => new(CardNowKind.Reply, "The agent waits for you", RunText.Waiting(pending)),
            NodeState.Waiting => new(CardNowKind.Stopped, status, detail),
            NodeState.Succeeded => new(CardNowKind.Result, "Result", review ?? FirstParagraph(ResultBlock.Prose(latest?.Result)) ?? detail,
                latest is { EndedAt: { } ended } ? Joined(Tools(latest), $"took {RunText.Elapsed(ended - latest.RequestedAt)}") : null),
            NodeState.Failed or NodeState.Interrupted or NodeState.Cancelled => new(CardNowKind.Stopped, status, detail ?? latest?.Detail),
            _ when waitsInRun => new(CardNowKind.Waits, "Starts after", null),
            _ => new(CardNowKind.Task, fieldLabel, FirstParagraph(fieldText) ?? "Nothing written yet."),
        };
    }

    /// <summary>The text's first paragraph that is not a heading, whole, or null for no text.</summary>
    public static string? FirstParagraph(string? text) => Paragraphs(text).FirstOrDefault();

    /// <summary>The text's last paragraph that is not a heading, whole, where a reply asks what it asks, or null for no text.</summary>
    public static string? LastParagraph(string? text) => Paragraphs(text).LastOrDefault();

    private static IEnumerable<string> Paragraphs(string? text) => string.IsNullOrWhiteSpace(text) ? []
        : text.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(paragraph => !paragraph.StartsWith('#'));

    private static string? Tools(AttemptRecord attempt) => attempt.Activity.Count(line => line.IsTool) switch
    {
        0 => null,
        1 => "1 tool call",
        var count => $"{count} tool calls",
    };

    private static string Since(AttemptRecord attempt) => $"since {attempt.RequestedAt.ToLocalTime():HH:mm}";

    private static string? Joined(params string?[] parts) =>
        string.Join(" · ", parts.Where(part => !string.IsNullOrEmpty(part))) is { Length: > 0 } meta ? char.ToUpperInvariant(meta[0]) + meta[1..] : null;
}
