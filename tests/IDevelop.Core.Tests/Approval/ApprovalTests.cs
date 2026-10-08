using IDevelop.Execution;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Approval.ApprovalFixture;
using static IDevelop.Core.Tests.Coordination.CoordinatorFixture;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Approval;

/// <summary>Confirming a preview approves exactly one run of its snapshot and starts it (E3d.1).</summary>
public sealed class ApprovalTests
{
    private static OperationId Command(int n) => new(Guid.Parse($"00000000-0000-0000-0000-00000000c{n:D3}"));

    internal static ApprovalFixture ChainAnswers(ApprovalFixture f) =>
        f.Answer(A, Writes(A, "out-a.txt", "A\n")).Answer(B, Writes(B, "out-b.txt", "B\n")).Answer(X, Reports(X));

    internal static async Task<WorkflowStart.Started> Start(ApprovalFixture f, RunPreflight preview, BaseChoice choice, OperationId command,
        ProjectRuns? window = null) =>
        Assert.IsType<WorkflowStart.Started>(await (window ?? f.Runs).StartWorkflow(f.Workflow, new(preview, choice, command)).WaitAsync(Bound));

    internal static async Task<RunView> Completed(WorkflowRunCoordinator coordinator)
    {
        try { return await coordinator.Until(view => view.Status == RunStatus.Completed).WaitAsync(Bound); }
        catch (TimeoutException) { throw new TimeoutException("The run did not complete: " + Describe(coordinator.View)); }
    }

    [Fact]
    public async Task Confirming_a_clean_preview_approves_one_run_at_HEAD_and_starts_it()
    {
        await using var f = ChainAnswers(new ApprovalFixture(Chain()));
        await f.Open();
        var preview = f.Preflight();

        var started = await Start(f, preview, BaseChoice.Head, Command(1));

        Assert.False(started.Existing);
        Assert.IsType<RunCommand.Accepted>(started.Resume);
        await Completed(started.Coordinator);
        var run = Assert.Single(f.ApprovedRuns());
        Assert.Equal(run, started.Coordinator.Address.Run);
        var record = f.Read(run);
        Assert.Equal(new RunBase(Head, BaseChoice.Head), record.Base);
        Assert.Equal(preview.Revision.Id, record.Revision.Id);
        Assert.Equal([1, 1, 1], new[] { f.Launches(A), f.Launches(B), f.Launches(X) });
    }

    /// <summary>Staged, unstaged, untracked, and ignored work, as a person leaves a project before Run Workflow.</summary>
    internal static void Uncommitted(ApprovalFixture f)
    {
        f.Git.Write("staged.txt", "staged\n");
        f.GitText("add", "staged.txt");
        f.Git.Write("plan.txt", "draft\n");
        f.Git.Write("notes.txt", "notes\n");
        f.Git.Write(".gitignore", "*.log\n");
        f.Git.Write("secret.log", "secret\n");
    }

    [Fact]
    public async Task A_snapshot_base_carries_uncommitted_work_and_leaves_the_person_s_branch_index_and_files_alone()
    {
        await using var f = ChainAnswers(new ApprovalFixture(Chain()));
        Uncommitted(f);
        var index = f.Index();
        await f.Open();
        var preview = f.Preflight();
        Assert.Equal<string>([".gitignore", "notes.txt", "plan.txt", "staged.txt"], preview.Base!.Changed);
        Assert.Equal<string>(["secret.log"], preview.Base.Ignored);
        Assert.Equal<BaseChoice>([BaseChoice.Head, BaseChoice.Snapshot], preview.Choices);

        var started = await Start(f, preview, BaseChoice.Snapshot, Command(2));
        await Completed(started.Coordinator);

        var run = Assert.Single(f.ApprovedRuns());
        var record = f.Read(run);
        Assert.Equal(BaseChoice.Snapshot, record.Base.Choice);
        Assert.NotEqual(Head, record.Base.Commit);
        Assert.Equal($"{preview.Base.WorkTree.Hex}\n{Head.Hex}\n", f.GitText("show", "-s", "--format=%T%n%P", record.Base.Commit.Hex));
        Assert.Equal("draft\n", f.ResultFile(run, A, "plan.txt"));
        Assert.Equal("staged\n", f.ResultFile(run, A, "staged.txt"));
        Assert.Equal("notes\n", f.ResultFile(run, B, "notes.txt"));
        Assert.Equal("A\n", f.ResultFile(run, B, "out-a.txt"));
        Assert.Null(f.ResultFile(run, A, "secret.log"));
        Assert.Equal(index, f.Index());
        Assert.Equal("refs/heads/main\n", f.GitText("symbolic-ref", "HEAD"));
        Assert.Equal(Head.Hex + "\n", f.GitText("rev-parse", "refs/heads/main"));
        Assert.Equal(["draft\n", "staged\n", "notes\n", "secret\n"], new[] { "plan.txt", "staged.txt", "notes.txt", "secret.log" }.Select(f.Text));
    }

    [Fact]
    public async Task Cancelling_the_preview_records_and_starts_nothing()
    {
        await using var f = ChainAnswers(new ApprovalFixture(Chain()));
        Uncommitted(f);
        var index = f.Index();
        var refs = f.GitText("for-each-ref");
        await f.Open();

        var preview = f.Preflight();
        await f.Reopen();

        Assert.Equal<BaseChoice>([BaseChoice.Head, BaseChoice.Snapshot], preview.Choices);
        Assert.False(Directory.Exists(Path.Combine(f.Project, ".idp", "runs")));
        Assert.Equal(0, f.TotalLaunches);
        Assert.Equal(index, f.Index());
        Assert.Equal(refs, f.GitText("for-each-ref"));
    }

    [Fact]
    public async Task While_a_run_is_active_a_different_preview_is_busy_and_the_same_content_is_that_run()
    {
        await using var f = new ApprovalFixture(Graph([Agent(A, conversation: ConversationMode.Chat), Agent(X, readOnly: true)]));
        f.Answer(A, Writes(A, "out-a.txt", "A\n")).Answer(X, Reports(X));
        await f.Open();
        var preview = f.Preflight();
        var first = await Start(f, preview, BaseChoice.Head, Command(3));
        await first.Coordinator.Until(view => view.Status == RunStatus.Waiting).WaitAsync(Bound);
        var run = first.Coordinator.Address.Run;
        f.Workflow = Edit(f.Workflow, new WorkflowEdit.EditTitle(X, "X checks"));

        var edited = f.Preflight();
        var busy = Assert.IsType<WorkflowStart.Busy>(await f.Runs.StartWorkflow(f.Workflow, new(edited, BaseChoice.Head, Command(4))).WaitAsync(Bound));

        Assert.Equal(run, edited.Active);
        Assert.Equal(run, busy.Active);
        f.Workflow = Edit(f.Workflow, new WorkflowEdit.EditTitle(X, "X"));
        var same = await Start(f, f.Preflight(), BaseChoice.Head, Command(5));
        Assert.True(same.Existing);
        Assert.Equal(run, same.Coordinator.Address.Run);
        Assert.Equal([run], f.ApprovedRuns());
        Assert.Equal([1, 1], new[] { f.Launches(A), f.Launches(X) });
    }
}
