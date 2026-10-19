using System.Text.Json;
using IDevelop.Core.Tests.Runs;
using IDevelop.Execution;
using static IDevelop.Core.Tests.Approval.ApprovalFixture;
using static IDevelop.Core.Tests.Approval.ApprovalTests;
using static IDevelop.Core.Tests.Coordination.CoordinatorFixture;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Approval;

/// <summary>
/// A run an earlier build left, with a journal of an earlier format, its approval intent and its Git leftovers, neither shows
/// nor blocks: the workflow runs, and every one of those files and refs stays as it was (#138, #145).
/// </summary>
public sealed class EarlierRunTests
{
    private static readonly RunId EarlierRun = new(Guid.Parse("00000000-0000-0000-0000-0000000e0001"));

    [Fact]
    public async Task A_project_with_an_earlier_runs_journal_opens_runs_its_workflow_and_leaves_that_run_alone()
    {
        await using var f = ChainAnswers(new ApprovalFixture(Chain()));
        var folder = Path.Combine(f.Project, ".idp", "runs", W.ToString(), EarlierRun.ToString());
        Directory.CreateDirectory(folder);
        var journal = Path.Combine(folder, "events.jsonl");
        File.WriteAllText(journal, EarlierJournalTests.Earlier(EarlierRun));
        // The approval intent and pin of that earlier run, as a build between #144 and #145 wrote them.
        var operation = new OperationId(Guid.Parse("00000000-0000-0000-0000-0000000e0002"));
        var intent = Path.Combine(folder, "approval.json");
        File.WriteAllBytes(intent, JsonSerializer.SerializeToUtf8Bytes(new ApprovalIntent(2, EarlierRun, operation, [operation],
            Revision.Capture(f.Workflow), Head), RunJournal.Options));
        var pin = ApprovalPin.Name(EarlierRun);
        f.GitText("update-ref", pin, Head.Hex);
        var before = (File.ReadAllBytes(journal), File.ReadAllBytes(intent));

        await f.Open();
        var preview = f.Preflight();
        Assert.True(preview.Confirmable);
        Assert.Empty(preview.Gaps);
        var started = await Start(f, preview, new(Guid.Parse("00000000-0000-0000-0000-0000000e0003")));
        await Completed(started.Coordinator);

        Assert.NotEqual(EarlierRun, started.Coordinator.Address.Run);
        Assert.Equal([1, 1, 1], new[] { f.Launches(A), f.Launches(B), f.Launches(X) });
        Assert.Equal(before.Item1, File.ReadAllBytes(journal));
        Assert.Equal(before.Item2, File.ReadAllBytes(intent));
        Assert.Equal(Head.Hex + "\n", f.GitText("rev-parse", pin));
        Assert.Equal(RunProblem.EarlierFormat, Assert.IsType<RunRead.Rejected>(RunStore.Open(f.Project).Read(W, EarlierRun)).Reason.Problem);
        Assert.Equal([started.Coordinator.Address.Run], RunStore.Open(f.Project).Records(W).Select(record => record.Id));
    }
}
