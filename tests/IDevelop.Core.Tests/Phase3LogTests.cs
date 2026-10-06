using System.Text.Encodings.Web;
using System.Text.Json;
using IDevelop.Execution;
using IDevelop.TestSupport;

namespace IDevelop.Core.Tests;

/// <summary>
/// tests/Shared/Fixtures/phase3-attempts holds attempt logs that the phase 3 engine wrote on 2026-10-04 at commit 73221ef.
/// A one-off test that was never committed drove ProjectRuns through the fake agent's shims, once per case, and
/// record.json beside each log holds the record the phase 3 reducer folded from it. The crashed case is the exception.
/// That test planted its first three lines to simulate the crash, so their command and arguments are not what 73221ef
/// launched. Only its reconciled line and its fold came from the phase 3 engine. The logs are frozen evidence of what
/// phase 3 wrote and folded, not output to regenerate. The commands in them name test folders that no longer exist.
/// </summary>
public class Phase3LogTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly Dictionary<string, TurnOutcome> Outcomes = new()
    {
        ["agy-bad-model"] = TurnOutcome.Failed,
        ["agy-succeeded"] = TurnOutcome.Succeeded,
        ["claude-bad-model"] = TurnOutcome.Failed,
        ["claude-succeeded"] = TurnOutcome.Succeeded,
        ["codex-bad-model"] = TurnOutcome.Failed,
        ["codex-cancelled"] = TurnOutcome.Stopped,
        ["codex-crashed"] = TurnOutcome.Interrupted,
        ["codex-launch-failed"] = TurnOutcome.Failed,
        ["codex-leave-gave-up"] = TurnOutcome.Interrupted,
        ["codex-left"] = TurnOutcome.Interrupted,
        ["codex-succeeded"] = TurnOutcome.Succeeded,
        ["pi-failed-exit-0"] = TurnOutcome.Failed,
        ["pi-succeeded"] = TurnOutcome.Succeeded,
    };

    public static TheoryData<string> Logs => new(Directory.EnumerateDirectories(Fixture.Path("phase3-attempts")).Select(folder => Path.GetFileName(folder)).Order());

    [Theory]
    [MemberData(nameof(Logs))]
    public void A_phase_3_log_folds_to_the_record_phase_3_folded_with_one_turn(string log)
    {
        var folder = Fixture.Path(Path.Combine("phase3-attempts", log));

        var record = AttemptReducer.Replay(AttemptLog.Read(folder))!;

        Assert.Equal(File.ReadAllText(Path.Combine(folder, "record.json")).Replace("\r\n", "\n"), Phase3Fields(record));
        Assert.Equal([new TurnRecord(1, null, Outcomes[log], record.Result, record.Detail)], record.Turns);
        Assert.Equal((0, null, null), (record.Queued.Count, record.Continues, record.Terminal));
        Assert.Null(record.RunBinding);
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
