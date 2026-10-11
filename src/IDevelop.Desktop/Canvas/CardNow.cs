using IDevelop.Desktop.Execution;
using IDevelop.Execution;
using IDevelop.Nodes;

namespace IDevelop.Desktop.Canvas;

/// <summary>What an expanded card's first section is about, which picks its glyph and what goes under its heading.</summary>
public enum CardNowKind { Message, Question, Reply, Approval, Result, OutOfDate, Stopped, Waits, Proposal, Task }

/// <summary>
/// What the card knows of its task when it reads its first section: its state and status line as the card shows them, and
/// what the attempt, the run, and the runs before it hold (#94, #90).
/// </summary>
/// <param name="Status">The card's status line, such as "Failed" or "Out of date".</param>
internal sealed record CardFacts(NodeState State, string Status)
{
    public NodeRole Role { get; init; }

    public bool HasAgent { get; init; } = true;

    /// <summary>
    /// The attempt the card's state describes, as its conversation reads it, through the run while the run's conversation
    /// is the task's. Null while the state comes from an earlier run, whose attempt the conversation does not show.
    /// </summary>
    public AttemptRecord? Latest { get; init; }

    /// <summary>Why the task stands where it does, in the run or between runs, or null when its status says enough.</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// The result the card shows is one from an earlier run: the task's result still counts between runs, or the run took
    /// it from an earlier run instead of running the task.
    /// </summary>
    public bool FromEarlierRun { get; init; }

    /// <summary>The report of the earlier run's result that still counts, or null.</summary>
    public string? EarlierReport { get; init; }

    /// <summary>Why Run would start nothing, such as "Runs after "A". Run "A" first.", or null.</summary>
    public string? Refusal { get; init; }

    /// <summary>Which run ran the task last, for one whose newest run ended it without a result, or null.</summary>
    public string? Owner { get; init; }

    /// <summary>The run holds the task until other tasks hand on, or it waits between runs for tasks without results.</summary>
    public bool Waits { get; init; }

    /// <summary>What the list of tasks it waits for does not say: which of them only the person can run, or null.</summary>
    public string? WaitsNote { get; init; }

    /// <summary>The planner's open proposal as its card names it, such as "Proposal · 3 tasks", or null.</summary>
    public string? Proposal { get; init; }

    /// <summary>A review's summary, such as "Round 2 · 1 open finding", or null.</summary>
    public string? Review { get; init; }

    public string FieldLabel { get; init; } = "Task";

    public string? FieldText { get; init; }
}

/// <summary>
/// The first section of an expanded card and of a collapsed card's peek (#94, ADR 0024): a heading, the whole text under it,
/// and a quiet line beside the heading. It shows only what the attempt, the run, and the runs before it already hold: the
/// agent's latest message, its question or reply, the result's first paragraph, why that result is out of date, why the
/// task stopped, what it waits for, or the task's own first field.
/// </summary>
public sealed record CardNow(CardNowKind Kind, string Heading, string? Text, string? Meta = null)
{
    /// <summary>
    /// The text says only which run left the task where it stands, not why, so it stays out of the state's callout: a run
    /// that ended a task between runs keeps no reason for it.
    /// </summary>
    public bool Quiet { get; init; }

    internal static CardNow Of(CardFacts facts)
    {
        var latest = facts.Latest;
        if (facts.Role == NodeRole.Proposing && facts.Proposal is { } proposal)
        {
            return new(CardNowKind.Proposal, "Proposal", $"{proposal}. Review the tasks in the inspector, then accept or dismiss them.");
        }

        return facts.State switch
        {
            NodeState.Running or NodeState.RunningElsewhere or NodeState.Stopping or NodeState.InReview => new(CardNowKind.Message, "Latest message",
                facts.Review ?? latest?.Activity.LastOrDefault(line => !line.IsTool)?.Text ?? "No message yet.", latest is null ? null : Joined(Tools(latest), Since(latest))),
            NodeState.Waiting when !facts.HasAgent => new(CardNowKind.Approval, "Waits for your approval",
                facts.Detail ?? "Approve what the tasks before it handed on, or send it back."),
            NodeState.Waiting when latest?.Pending is Pending.Question question => new(CardNowKind.Question, "The agent asks", question.Text),
            NodeState.Waiting when latest?.Pending is Pending.Reply => new(CardNowKind.Reply, "The agent replied",
                LastParagraph(ResultBlock.Prose(latest.Result)) ?? RunText.Waiting(latest.Pending)),
            NodeState.Waiting when latest?.Pending is { } pending => new(CardNowKind.Reply, "The agent waits for you", RunText.Waiting(pending)),
            NodeState.Waiting => new(CardNowKind.Stopped, facts.Status, facts.Detail),
            // A result from an earlier run shows that run's report, and says beside its heading where it comes from.
            NodeState.Succeeded when facts.FromEarlierRun => new(CardNowKind.Result, "Result",
                FirstParagraph(ResultBlock.Prose(facts.EarlierReport)) ?? facts.Detail ?? "Its result from an earlier run still counts.", "From an earlier run"),
            NodeState.Succeeded => new(CardNowKind.Result, "Result", facts.Review ?? FirstParagraph(ResultBlock.Prose(latest?.Result)) ?? facts.Detail,
                latest is { EndedAt: { } ended } ? Joined(Tools(latest), $"took {RunText.Elapsed(ended - latest.RequestedAt)}") : null),
            // Said once: why its result no longer counts, or, where Run's refusal says that, what to run first.
            NodeState.OutOfDate => new(CardNowKind.OutOfDate, "Out of date", facts.Detail ?? facts.Refusal ?? "Its result from an earlier run no longer counts."),
            // A run that ended the task says why; the attempt it shows says why only when it is the one that ended.
            NodeState.Failed or NodeState.Interrupted or NodeState.Cancelled => (facts.Detail ?? latest?.Detail) is { } why
                ? new(CardNowKind.Stopped, facts.Status, why)
                : new(CardNowKind.Stopped, facts.Status, facts.Owner) { Quiet = true },
            _ when facts.Waits => new(CardNowKind.Waits, "Starts after", facts.WaitsNote),
            _ => new(CardNowKind.Task, facts.FieldLabel, FirstParagraph(facts.FieldText) ?? "Nothing written yet."),
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
