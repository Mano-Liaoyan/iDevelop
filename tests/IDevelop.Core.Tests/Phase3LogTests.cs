using System.Text.Encodings.Web;
using System.Text.Json;
using IDevelop.Execution;
using IDevelop.TestSupport;

namespace IDevelop.Core.Tests;

/// <summary>
/// tests/Shared/Fixtures/phase3-attempts holds attempt logs that the phase 3 engine wrote, through the fake agent, and
/// record.json beside each holds the record the phase 3 reducer folded from it.
/// </summary>
public class Phase3LogTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static TheoryData<string> Logs => new(Directory.EnumerateDirectories(Fixture.Path("phase3-attempts")).Select(folder => Path.GetFileName(folder)).Order());

    [Theory]
    [MemberData(nameof(Logs))]
    public void A_phase_3_log_folds_to_the_record_phase_3_folded(string log)
    {
        var folder = Fixture.Path(Path.Combine("phase3-attempts", log));

        var record = AttemptReducer.Replay(AttemptLog.Read(folder))!;

        Assert.Equal(File.ReadAllText(Path.Combine(folder, "record.json")).Replace("\r\n", "\n"), Phase3Fields(record));
    }

    /// <summary>The public fields a phase 3 record had, in the shape record.json stores them.</summary>
    private static string Phase3Fields(AttemptRecord record) => JsonSerializer.Serialize(
        new
        {
            id = record.Id.ToString(),
            task = record.Task.ToString(),
            taskTitle = record.TaskTitle,
            client = Clients.WireName(record.Requested.Client),
            model = record.Requested.Model,
            reasoning = record.Requested.Reasoning,
            requestedAt = record.RequestedAt,
            status = record.Status.ToString(),
            stopping = record.Stopping,
            endedAt = record.EndedAt,
            result = record.Result,
            detail = record.Detail,
            sessionId = record.SessionId,
            reportedModel = record.ReportedModel,
            reportedReasoning = record.ReportedReasoning,
            activity = record.Activity.Select(line => new { at = line.At, text = line.Text }),
        },
        Options) + "\n";
}
