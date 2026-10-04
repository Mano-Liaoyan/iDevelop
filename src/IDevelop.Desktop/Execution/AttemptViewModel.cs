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

    /// <summary>The client's final text, shown in a read-only box so it can be selected and copied.</summary>
    public string? Result => record.Result;

    public string? Detail => record.Detail;

    public IReadOnlyList<string> Activity => [.. record.Activity.TakeLast(ActivityShown).Select(line => line.Text)];

    public bool HasActivity => !record.Activity.IsEmpty;
}
