using System.Collections.Immutable;
using IDevelop.Execution;
using IDevelop.Nodes;

namespace IDevelop.Desktop.Execution;

/// <summary>
/// A task's last run in the inspector, after the attempts it continues. Records never change, so each new record gets a
/// new instance.
/// </summary>
/// <param name="earlier">The attempts whose session this one continues, oldest first.</param>
public sealed class AttemptViewModel(AttemptRecord record, ImmutableArray<AttemptRecord> earlier, bool elsewhere)
{
    private const int ActivityShown = 8;

    public string StatusLabel => RunText.StatusLabel(record, elsewhere);

    public StatusTone Tone => RunText.Tone(record);

    public string Configuration => RunText.Configuration(record);

    /// <summary>What ran, in short parts. <see cref="Configuration"/> says it in full.</summary>
    public IReadOnlyList<string> Agent => RunText.Agent(record);

    public string Timing => RunText.Timing(record);

    /// <summary>When the run started, which the inspector says relative to now. <see cref="Timing"/> says it in full.</summary>
    public DateTimeOffset Started => record.RequestedAt;

    /// <summary>How long the run took, such as "28 s", or null until it ends.</summary>
    public string? Took => record.EndedAt is { } ended ? RunText.Elapsed(ended - record.RequestedAt) : null;

    public string? Detail => record.Detail;

    /// <summary>Each attempt of the conversation, oldest first, so a question stays above the answer that continued it.</summary>
    public IReadOnlyList<ExchangeViewModel> Conversation { get; } = Exchanges(record, earlier, elsewhere);

    public string? WaitingCaption => RunText.WaitingCaption(record);

    public IReadOnlyList<string> Waiting => record.Queued;

    public string? TerminalNote => RunText.TerminalNote(record);

    /// <summary>The latest activity. The inspector shows <see cref="Said"/> until the person asks for the tool calls.</summary>
    public IReadOnlyList<ActivityLine> Activity { get; } = Recent(record.Activity, ActivityShown);

    /// <summary>The lines of <see cref="Activity"/> that the agent or iDevelop said, without its tool calls.</summary>
    public IReadOnlyList<ActivityLine> Said => [.. Activity.Where(line => !line.IsTool)];

    public bool HasActivity => !record.Activity.IsEmpty;

    /// <summary>"1 tool call" or "3 tool calls" in <see cref="Activity"/>, or null when it has none.</summary>
    public string? ToolCalls => Activity.Count(line => line.IsTool) switch
    {
        0 => null,
        1 => "1 tool call",
        var count => $"{count} tool calls",
    };

    /// <summary>
    /// The last <paramref name="shown"/> lines the agent or iDevelop said, and every tool call since the earliest of them,
    /// in order. With fewer such lines, every line; with none, the last <paramref name="shown"/> tool calls.
    /// </summary>
    internal static IReadOnlyList<ActivityLine> Recent(IReadOnlyList<ActivityLine> lines, int shown)
    {
        var said = lines.Select((line, index) => (line, index)).Where(entry => !entry.line.IsTool).ToList();
        var start = said.Count > shown ? said[^shown].index : said.Count > 0 ? 0 : Math.Max(0, lines.Count - shown);
        return [.. lines.Skip(start)];
    }

    /// <summary>
    /// Turns are numbered across the whole conversation. Only a conversation of several attempts has status lines. The
    /// latest attempt's detail and terminal note have their own places above and below the conversation.
    /// </summary>
    private static List<ExchangeViewModel> Exchanges(AttemptRecord record, ImmutableArray<AttemptRecord> earlier, bool elsewhere)
    {
        var number = 0;
        ExchangeViewModel Exchange(AttemptRecord attempt, bool latest) => new(
            earlier.IsEmpty ? null : RunText.ExchangeLine(attempt, latest && elsewhere),
            [.. attempt.Turns.Select(turn => new TurnViewModel(turn, ++number, latest && turn.Number == attempt.Turns.Count, Author(attempt, turn)))],
            latest ? null : RunText.TerminalNote(attempt));

        return [.. earlier.Select(attempt => Exchange(attempt, latest: false)), Exchange(record, latest: true)];
    }

    /// <summary>A review writes its reviewer's later messages and the first message of each fix round. The person writes the rest.</summary>
    private static string Author(AttemptRecord attempt, TurnRecord turn) =>
        attempt.Subject is not null || attempt.Fix is not null && turn.Number == 1 ? "iDevelop" : "You";
}

/// <summary>One attempt of a conversation: its status line, its turns, and its hand-off to a terminal.</summary>
public sealed record ExchangeViewModel(string? StatusLine, IReadOnlyList<TurnViewModel> Turns, string? TerminalNote);

/// <summary>
/// One turn in the inspector. The agent's final text sits in a read-only box so it can be selected and copied. The latest
/// turn's box is the run's result, under the automation id the result has always had. A block iDevelop read stays out of
/// the box, because its question, proposal, or verdict has its own section.
/// </summary>
/// <param name="number">The turn's place in the whole conversation, from 1.</param>
public sealed class TurnViewModel(TurnRecord turn, int number, bool latest, string author = "You")
{
    public string Author => author;

    public string? Message => turn.Message;

    public string? Reply => turn.FinalText is null ? null : ResultBlock.Prose(turn.FinalText);

    public string? Note => latest ? null : RunText.EarlierTurnNote(turn);

    public string ReplyId => latest ? "LastRunResult" : $"TurnReply{number}";

    public string ReplyName => latest ? "Result" : $"Reply {number}";
}
