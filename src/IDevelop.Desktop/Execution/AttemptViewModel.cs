using IDevelop.Execution;

namespace IDevelop.Desktop.Execution;

/// <summary>A task's last run in the inspector. Records never change, so each new record gets a new instance.</summary>
public sealed class AttemptViewModel(AttemptRecord record, bool elsewhere)
{
    private const int ActivityShown = 8;

    public string StatusLabel => RunText.StatusLabel(record, elsewhere);

    public StatusTone Tone => RunText.Tone(record);

    public string Configuration => RunText.Configuration(record);

    public string Timing => RunText.Timing(record);

    public string? Detail => record.Detail;

    /// <summary>Each turn as what the person sent and what the agent answered, oldest first.</summary>
    public IReadOnlyList<TurnViewModel> Turns => [.. record.Turns.Select(turn => new TurnViewModel(turn, latest: turn.Number == record.Turns.Count))];

    public string? WaitingCaption => RunText.WaitingCaption(record);

    public IReadOnlyList<string> Waiting => record.Queued;

    public string? TerminalNote => RunText.TerminalNote(record);

    public IReadOnlyList<string> Activity => [.. record.Activity.TakeLast(ActivityShown).Select(line => line.Text)];

    public bool HasActivity => !record.Activity.IsEmpty;
}

/// <summary>
/// One turn in the inspector. The agent's final text sits in a read-only box so it can be selected and copied. The latest
/// turn's box is the run's result, under the automation id the result has always had.
/// </summary>
public sealed class TurnViewModel(TurnRecord turn, bool latest)
{
    public string? Message => turn.Message;

    public string? Reply => turn.FinalText;

    public string? Note => latest ? null : RunText.EarlierTurnNote(turn);

    public string ReplyId => latest ? "LastRunResult" : $"TurnReply{turn.Number}";

    public string ReplyName => latest ? "Result" : $"Reply {turn.Number}";
}
