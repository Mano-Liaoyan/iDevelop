using System.Text.Json;
using IDevelop.Core.Tests.Runs;
using IDevelop.Execution;
using IDevelop.Projects;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Approval.ApprovalTests;
using static IDevelop.Core.Tests.Approval.ChangedContentTests;
using static IDevelop.Core.Tests.Approval.PlannerInclusionTests;
using static IDevelop.Core.Tests.Coordination.CoordinatorFixture;

namespace IDevelop.Core.Tests.Approval;

/// <summary>
/// A crash at each durable step of a confirmation that includes a waiting Chat planner, then a restart and a repeated
/// confirmation, give one run with the planner's report included once and the planner never launched again (E3d.2).
/// </summary>
public sealed class PlannerInclusionCrashTests
{
    private static readonly OperationId First = new(Guid.Parse("00000000-0000-0000-0000-0000000d0101"));
    private static readonly OperationId AfterRestart = new(Guid.Parse("00000000-0000-0000-0000-0000000d0102"));

    private static async Task Crash(ApprovalFixture f, string point, BaseChoice choice = BaseChoice.Head)
    {
        f.Install();
        var file = Path.Combine(f.Evidence, "workflow.json");
        File.WriteAllBytes(file, WorkflowFile.Serialize(f.Workflow));
        using var racer = new Racer(f.Git.Environment, "approve-crash", f.Project, file, f.Fakes.Folder, First.Value.ToString("D"), choice.ToString(), point, "include");
        Assert.Equal("Previewed", await racer.Line());
        Assert.Equal(point, await racer.Line());
        await racer.Exit();
        Assert.Equal(73, racer.ExitCode);
    }

    private static async Task<WorkflowStart.Started> Include(ApprovalFixture f, RunPreflight preview, OperationId command, BaseChoice choice) =>
        Assert.IsType<WorkflowStart.Started>(await f.Runs.StartWorkflow(f.Workflow,
            Including(preview, command, Assert.Single(preview.Planners)) with { Choice = choice }).WaitAsync(Bound));

    [Theory]
    [InlineData("approval.locked", "Head")]
    [InlineData("approval.intent.after", "Head")]
    [InlineData("approval.finished.after", "Head")]
    [InlineData("approval.pinned.after", "Head")]
    [InlineData("approval.approved.after", "Head")]
    [InlineData("approval.opened.after", "Head")]
    [InlineData("approval.finished.after", "Snapshot")]
    [InlineData("approval.snapshot.after", "Snapshot")]
    [InlineData("approval.pinned.after", "Snapshot")]
    [InlineData("approval.approved.after", "Snapshot")]
    public async Task A_crash_at_each_step_then_a_repeated_confirmation_includes_the_planner_once(string point, string chosen)
    {
        var choice = Enum.Parse<BaseChoice>(chosen);
        await using var f = Fixture();
        // On a snapshot base, the planner read the uncommitted work that the snapshot keeps.
        if (choice == BaseChoice.Snapshot) f.Git.Write("notes.txt", "notes\n");
        await f.Open();
        var waiting = await PlanTwoTurns(f);
        await f.Close();

        await Crash(f, point, choice);

        var intents = Intents(f.Project);
        var intended = intents is [var path] ? JsonSerializer.Deserialize<ApprovalIntent>(File.ReadAllBytes(path), RunJournal.Options) : null;
        Assert.Equal(point != "approval.locked", intended is not null);
        if (intended is not null) Assert.Equal<ReportInclusion>([new(X, waiting.Id, 2)], intended.Inclusions);
        Assert.Equal(point is "approval.approved.after" or "approval.opened.after" ? 1 : 0, f.ApprovedRuns().Length);
        Assert.Equal(2, f.TotalLaunches);

        await f.Open();
        Assert.Equal(point is "approval.locked" or "approval.intent.after" ? AttemptStatus.WaitingForInput : AttemptStatus.Succeeded,
            f.Runs.Latest[X].Status);
        var preview = f.Preflight();
        var starts = await Task.WhenAll(Include(f, preview, AfterRestart, choice), Include(f, preview, First, choice));

        var run = Assert.Single(f.ApprovedRuns());
        Assert.All(starts, start => Assert.Equal(run, start.Coordinator.Address.Run));
        if (intended is not null) Assert.Equal(intended.Run, run);
        await Completed(starts[0].Coordinator);
        var record = f.Read(run);
        var included = Assert.Single(record.Results, result => result.Origin is ResultOrigin.Included);
        Assert.Equal((X, waiting.Result), (included.Task, included.Report));
        Assert.Equal([1, 1], new[] { f.Launches("N1"), f.Launches("N2") });
        Assert.Equal(2, f.TotalLaunches - f.Launches("N1") - f.Launches("N2"));
        Assert.Equal((waiting.Id, AttemptStatus.Succeeded, 2), (f.Runs.Latest[X].Id, f.Runs.Latest[X].Status, f.Runs.Latest[X].Turns.Count));
        Assert.Equal(choice, record.Base.Choice);
        Assert.Equal(choice == BaseChoice.Snapshot ? "notes\n" : null, f.ResultFile(run, Added(f, "N1"), "notes.txt"));
    }

    [Fact]
    public async Task After_a_crash_once_the_planner_finished_a_confirmation_without_it_runs_it_in_the_run()
    {
        await using var f = Fixture().Answer(X, FakeRule.On().Print(FakeAgents.SessionLine(ClientId.Codex, Session))
            .Print(FakeAgents.ReplyLines(ClientId.Codex, FirstProposal)), FakeRule.On().Print(FakeAgents.SessionLine(ClientId.Codex, "session-run"))
            .Print(FakeAgents.ReplyLines(ClientId.Codex, SecondProposal)));
        await f.Open();
        await PlanTwoTurns(f);
        await f.Close();
        await Crash(f, "approval.finished.after");
        var stale = JsonSerializer.Deserialize<ApprovalIntent>(File.ReadAllBytes(Assert.Single(Intents(f.Project))), RunJournal.Options)!;

        await f.Open();
        var started = await Start(f, f.Preflight(), BaseChoice.Head, AfterRestart);
        // The Chat planner's turn in the run ends waiting for the person, as on its own.
        await started.Coordinator.Until(view => view.Tasks[X].State == TaskState.Waiting).WaitAsync(Bound);

        var run = Assert.Single(f.ApprovedRuns());
        Assert.NotEqual(stale.Run, run);
        Assert.Equal(2, f.Launches(X));
        Assert.Empty(f.Read(run).Results);
        Assert.Single(f.Read(run).Attempts.Values, attempt => attempt.Task == X);
    }
}
