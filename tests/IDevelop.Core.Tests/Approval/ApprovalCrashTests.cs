using System.Text.Json;
using IDevelop.Core.Tests.Runs;
using IDevelop.Execution;
using IDevelop.Projects;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Approval.ApprovalFixture;
using static IDevelop.Core.Tests.Approval.ApprovalTests;
using static IDevelop.Core.Tests.Approval.ChangedContentTests;
using static IDevelop.Core.Tests.Coordination.CoordinatorFixture;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Approval;

/// <summary>A crash at each durable step of an approval, then a restart and a repeated confirmation, give one run (E3d.1).</summary>
public sealed class ApprovalCrashTests
{
    private static readonly OperationId First = new(Guid.Parse("00000000-0000-0000-0000-0000000c0001"));
    private static readonly OperationId AfterRestart = new(Guid.Parse("00000000-0000-0000-0000-0000000c0002"));

    /// <summary>Confirms the workflow's preview in a racer process, which exits at <paramref name="point"/>.</summary>
    private static async Task Crash(ApprovalFixture f, BaseChoice choice, string point)
    {
        f.Install();
        var file = Path.Combine(f.Evidence, "workflow.json");
        File.WriteAllBytes(file, WorkflowFile.Serialize(f.Workflow));
        using var racer = new Racer(f.Git.Environment, "approve-crash", f.Project, file, f.Fakes.Folder, First.Value.ToString("D"), choice.ToString(), point);
        Assert.Equal("Previewed", await racer.Line());
        Assert.Equal(point, await racer.Line());
        await racer.Exit();
        Assert.Equal(73, racer.ExitCode);
        Assert.Equal(0, f.TotalLaunches);
    }

    private static ApprovalIntent? Intent(ApprovalFixture f) => Intents(f.Project) is [var path]
        ? JsonSerializer.Deserialize<ApprovalIntent>(File.ReadAllBytes(path), RunJournal.Options) : null;

    [Theory]
    [InlineData("approval.locked", "Snapshot")]
    [InlineData("approval.intent.after", "Snapshot")]
    [InlineData("approval.snapshot.after", "Snapshot")]
    [InlineData("approval.pinned.after", "Snapshot")]
    [InlineData("approval.approved.after", "Snapshot")]
    [InlineData("approval.opened.after", "Snapshot")]
    [InlineData("approval.intent.after", "Head")]
    [InlineData("approval.pinned.after", "Head")]
    [InlineData("approval.approved.after", "Head")]
    [InlineData("approval.opened.after", "Head")]
    public async Task A_crash_at_each_durable_step_then_a_restart_and_a_repeated_confirmation_start_one_run(string point, string chosen)
    {
        var choice = Enum.Parse<BaseChoice>(chosen);
        await using var f = ChainAnswers(new ApprovalFixture(Chain()));
        Uncommitted(f);
        var index = f.Index();
        await Crash(f, choice, point);
        var intended = Intent(f);
        Assert.Equal(point == "approval.locked", intended is null);
        Assert.Equal(point is "approval.approved.after" or "approval.opened.after" ? 1 : 0, f.ApprovedRuns().Length);

        await f.Open();
        var preview = f.Preflight();
        var starts = await System.Threading.Tasks.Task.WhenAll(Start(f, preview, choice, AfterRestart), Start(f, preview, choice, First));

        var run = Assert.Single(f.ApprovedRuns());
        Assert.All(starts, start => Assert.Equal(run, start.Coordinator.Address.Run));
        if (intended is not null) Assert.Equal(intended.Run, run);
        Assert.Equal(point is "approval.approved.after" or "approval.opened.after" ? 2 : 1, starts.Count(start => start.Existing));
        await Completed(starts[0].Coordinator);
        Assert.Equal([1, 1, 1], new[] { f.Launches(A), f.Launches(B), f.Launches(X) });
        var record = f.Read(run);
        Assert.Equal(choice, record.Base.Choice);
        Assert.Equal(choice == BaseChoice.Snapshot ? "notes\n" : null, f.ResultFile(run, A, "notes.txt"));
        if (choice == BaseChoice.Snapshot)
        {
            var recorded = Intent(f)!.Recorded.ToUnixTimeSeconds();
            Assert.Equal($"{preview.Base!.WorkTree.Hex}\n{Head.Hex}\n{recorded}\n", f.GitText("show", "-s", "--format=%T%n%P%n%ct", record.Base.Commit.Hex));
        }
        Assert.Equal(index, f.Index());
        Assert.Equal(Head.Hex + "\n", f.GitText("rev-parse", "HEAD"));
        Assert.Equal(["draft\n", "notes\n"], new[] { "plan.txt", "notes.txt" }.Select(f.Text));
        Assert.Equal(record.Base.Commit.Hex + "\n", f.GitText("rev-parse", RunLayout.ApprovedBase(record.RunKey!)));
        Assert.Equal("", f.GitText("for-each-ref", "refs/idp/approvals/"));
    }

    [Fact]
    public async Task An_approved_snapshot_base_survives_garbage_collection_before_its_first_preparation()
    {
        await using var f = ChainAnswers(new ApprovalFixture(Chain()));
        Uncommitted(f);
        await Crash(f, BaseChoice.Snapshot, "approval.approved.after");
        var run = Assert.Single(f.ApprovedRuns());
        var snapshot = f.Read(run).Base.Commit;
        f.GitText("gc", "-q", "--prune=now");
        await f.Open();

        var started = await Start(f, f.Preflight(), BaseChoice.Snapshot, AfterRestart);

        Assert.True(started.Existing);
        await Completed(started.Coordinator);
        Assert.Equal([1, 1, 1], new[] { f.Launches(A), f.Launches(B), f.Launches(X) });
        Assert.Equal("notes\n", f.ResultFile(run, A, "notes.txt"));
        Assert.Equal(snapshot.Hex + "\n", f.GitText("rev-parse", RunLayout.ApprovedBase(f.Read(run).RunKey!)));
        Assert.Equal("", f.GitText("for-each-ref", "refs/idp/approvals/"));
    }

    [Fact]
    public async Task A_confirmation_that_finds_the_run_busy_keeps_the_pin_of_a_base_not_yet_prepared()
    {
        await using var f = ChainAnswers(new ApprovalFixture(Chain()));
        Uncommitted(f);
        await Crash(f, BaseChoice.Snapshot, "approval.approved.after");
        var run = Assert.Single(f.ApprovedRuns());
        await f.Open();
        f.Workflow = Edit(f.Workflow, new WorkflowEdit.EditTitle(X, "X checks"));
        Assert.Equal(run, Assert.IsType<WorkflowStart.Busy>(await f.Runs.StartWorkflow(f.Workflow, new(f.Preflight(), BaseChoice.Snapshot, AfterRestart))
            .WaitAsync(Bound)).Active);
        f.Workflow = Edit(f.Workflow, new WorkflowEdit.EditTitle(X, "X"));
        f.GitText("gc", "-q", "--prune=now");

        var started = await Start(f, f.Preflight(), BaseChoice.Snapshot, First);

        Assert.True(started.Existing);
        await Completed(started.Coordinator);
        Assert.Equal("notes\n", f.ResultFile(run, A, "notes.txt"));
    }

    [Fact]
    public async Task A_pin_left_once_the_base_ref_holds_the_base_goes_at_the_next_confirmation()
    {
        await using var f = ChainAnswers(new ApprovalFixture(Chain()));
        await f.Open();
        var started = await Start(f, f.Preflight(), BaseChoice.Head, First);
        await Completed(started.Coordinator);
        var run = started.Coordinator.Address.Run;
        f.GitText("update-ref", $"refs/idp/approvals/{run}", Head.Hex);

        Assert.True((await Start(f, f.Preflight(), BaseChoice.Head, First)).Existing);

        Assert.Equal("", f.GitText("for-each-ref", "refs/idp/approvals/"));
        Assert.Equal(Head.Hex + "\n", f.GitText("rev-parse", RunLayout.ApprovedBase(f.Read(run).RunKey!)));
    }

    [Theory]
    [InlineData("file")]
    [InlineData("title")]
    [InlineData("commit")]
    [InlineData("base")]
    public async Task A_pending_intent_whose_content_changed_is_replaced_by_the_refreshed_confirmation(string change)
    {
        await using var f = ChainAnswers(new ApprovalFixture(Chain()));
        Uncommitted(f);
        await Crash(f, BaseChoice.Snapshot, "approval.pinned.after");
        var stale = Intent(f)!;
        Assert.Equal($"refs/idp/approvals/{stale.Run}\n", f.GitText("for-each-ref", "--format=%(refname)", "refs/idp/approvals/"));
        var choice = change == "base" ? BaseChoice.Head : BaseChoice.Snapshot;
        if (change == "file") f.Git.Write("notes.txt", "notes again\n");
        if (change == "title") f.Workflow = Edit(f.Workflow, new WorkflowEdit.EditTitle(X, "X checks"));
        if (change == "commit")
        {
            f.GitText("add", "notes.txt");
            f.GitText("-c", "commit.gpgSign=false", "commit", "-q", "-m", "notes");
        }
        await f.Open();

        var started = await Start(f, f.Preflight(), choice, AfterRestart);

        await Completed(started.Coordinator);
        var run = Assert.Single(f.ApprovedRuns());
        Assert.NotEqual(stale.Run, run);
        Assert.Equal(run, Intent(f)!.Run);
        Assert.False(Directory.Exists(Path.Combine(f.Project, ".idp", "runs", W.ToString(), stale.Run.ToString())));
        Assert.Equal("", f.GitText("for-each-ref", "refs/idp/approvals/"));
        Assert.Equal(choice, f.Read(run).Base.Choice);
        Assert.Equal(change == "file" ? "notes again\n" : change == "base" ? null : "notes\n", f.ResultFile(run, A, "notes.txt"));
        Assert.Equal(change == "title" ? "X checks" : "X", f.Read(run).Revision.Snapshot.Tasks[X].Title);
        Assert.Equal(1, f.Launches(A));
    }

    [Fact]
    public async Task A_journal_torn_in_its_first_write_finishes_as_the_intent_s_run()
    {
        await using var f = ChainAnswers(new ApprovalFixture(Chain()));
        await Crash(f, BaseChoice.Head, "approval.intent.after");
        var intended = Intent(f)!;
        File.WriteAllText(Path.Combine(f.Project, ".idp", "runs", W.ToString(), intended.Run.ToString(), "events.jsonl"), "{\"schema\":3,\"seq");
        await f.Open();

        var started = await Start(f, f.Preflight(), BaseChoice.Head, AfterRestart);

        Assert.False(started.Existing);
        await Completed(started.Coordinator);
        Assert.Equal([intended.Run], f.ApprovedRuns());
        Assert.Equal([1, 1, 1], new[] { f.Launches(A), f.Launches(B), f.Launches(X) });
    }
}
