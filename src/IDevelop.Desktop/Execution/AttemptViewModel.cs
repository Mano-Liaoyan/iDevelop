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

    public string Timing => RunText.Timing(record);

    public string? Detail => record.Detail;

    /// <summary>Each attempt of the conversation, oldest first, so a question stays above the answer that continued it.</summary>
    public IReadOnlyList<ExchangeViewModel> Conversation { get; } = Exchanges(record, earlier, elsewhere);

    public string? WaitingCaption => RunText.WaitingCaption(record);

    public IReadOnlyList<string> Waiting => record.Queued;

    public string? TerminalNote => RunText.TerminalNote(record);

    public IReadOnlyList<string> Activity => [.. record.Activity.TakeLast(ActivityShown).Select(line => line.Text)];

    public bool HasActivity => !record.Activity.IsEmpty;

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
