using IDevelop.Nodes;
using EngineState = IDevelop.Engine.NodeState;

namespace IDevelop.Desktop.Canvas;

/// <summary>What an expanded card's first section is about, which picks its glyph and what goes under its heading.</summary>
public enum CardNowKind { Message, Question, Reply, Approval, Result, Stale, Stopped, Proposal, Task }

/// <summary>What the card knows of its node when it reads its first section: its engine state and status line, and what its log holds.</summary>
/// <param name="Status">The card's status line, such as "Failed" or "Stale".</param>
internal sealed record CardFacts(EngineState State, string Status)
{
    public NodeRole Role { get; init; }

    public bool HasAgent { get; init; } = true;

    /// <summary>The agent's latest complete message, or null.</summary>
    public string? LastReply { get; init; }

    /// <summary>The report of the node's latest result, or null.</summary>
    public string? Report { get; init; }

    /// <summary>What the node needs or why it stopped, in a sentence, or null.</summary>
    public string? Detail { get; init; }

    /// <summary>The planner's open proposal as its card names it, such as "Proposal · 3 tasks", or null.</summary>
    public string? Proposal { get; init; }

    /// <summary>A review's summary, such as "2 open findings", or null.</summary>
    public string? Review { get; init; }

    public string FieldLabel { get; init; } = "Task";

    public string? FieldText { get; init; }
}

/// <summary>
/// The first section of an expanded card and of a collapsed card's peek (#94, ADR 0024): a heading, the whole text under it,
/// and a quiet line beside the heading. It shows only what the engine already holds: the agent's latest message, its
/// question or reply, the result's first paragraph, why the node is stale or stopped, or the node's own first field.
/// </summary>
public sealed record CardNow(CardNowKind Kind, string Heading, string? Text, string? Meta = null)
{
    internal static CardNow Of(CardFacts facts)
    {
        if (facts.Role == NodeRole.Proposing && facts.Proposal is { } proposal)
        {
            return new(CardNowKind.Proposal, "Proposal", $"{proposal}. Review the tasks in the inspector, then accept or dismiss them.");
        }

        return facts.State switch
        {
            EngineState.Running => new(CardNowKind.Message, "Latest message", facts.LastReply is { } reply ? LastParagraph(ResultBlock.Prose(reply)) : "No message yet."),
            EngineState.InputRequired { Why: EngineState.Approval or EngineState.RoundLimit } => new(CardNowKind.Approval, "Waits for your approval", facts.Detail),
            EngineState.InputRequired { Why: EngineState.Reply } => new(CardNowKind.Reply, "The agent replied",
                LastParagraph(ResultBlock.Prose(facts.LastReply)) ?? facts.Detail),
            EngineState.InputRequired { Why: EngineState.Interrupted or EngineState.Conflict } => new(CardNowKind.Stopped, facts.Status, facts.Detail),
            EngineState.InputRequired waiting => new(CardNowKind.Question, "The agent asks", waiting.Why),
            EngineState.Completed => new(CardNowKind.Result, "Result", facts.Review ?? FirstParagraph(ResultBlock.Prose(facts.Report)) ?? "No report."),
            EngineState.Stale => new(CardNowKind.Stale, "Stale", facts.Detail),
            EngineState.Failed => new(CardNowKind.Stopped, facts.Status, facts.Detail),
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
}
