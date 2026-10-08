using IDevelop.Execution;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Approval.ApprovalFixture;
using static IDevelop.Core.Tests.Approval.ApprovalTests;
using static IDevelop.Core.Tests.Coordination.CoordinatorFixture;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Approval;

/// <summary>A confirmation of a preview that no longer shows what it would approve refreshes it and approves nothing (E3d.1).</summary>
public sealed class ChangedContentTests
{
    private static OperationId Command(int n) => new(Guid.Parse($"00000000-0000-0000-0000-00000000e{n:D3}"));

    private static async Task<RunPreflight> Refreshed(ApprovalFixture f, RunPreflight preview, BaseChoice choice, OperationId command)
    {
        var changed = Assert.IsType<WorkflowStart.Changed>(await f.Runs.StartWorkflow(f.Workflow, new(preview, choice, command)).WaitAsync(Bound));
        Assert.Empty(f.ApprovedRuns());
        Assert.Empty(Intents(f.Project));
        Assert.Equal(0, f.TotalLaunches);
        return changed.Current;
    }

    /// <summary>The approval intents in the workflow's run folders.</summary>
    internal static string[] Intents(string project)
    {
        var folder = Path.Combine(project, ".idp", "runs", W.ToString());
        return Directory.Exists(folder) ? [.. Directory.EnumerateFiles(folder, "approval.json", SearchOption.AllDirectories)] : [];
    }

    [Fact]
    public async Task An_execution_edit_after_the_preview_refreshes_it_and_the_refreshed_preview_approves_the_edit()
    {
        await using var f = ChainAnswers(new ApprovalFixture(Chain()));
        await f.Open();
        var preview = f.Preflight();
        f.Workflow = Edit(f.Workflow, new WorkflowEdit.EditTitle(X, "X checks"));

        var current = await Refreshed(f, preview, BaseChoice.Head, Command(1));

        Assert.Equal(Revision.Capture(f.Workflow).Id, current.Revision.Id);
        Assert.NotEqual(preview.Revision.Id, current.Revision.Id);
        var started = await Start(f, current, BaseChoice.Head, Command(1));
        await Completed(started.Coordinator);
        Assert.Equal("X checks", f.Read(Assert.Single(f.ApprovedRuns())).Revision.Snapshot.Tasks[X].Title);
    }

    [Fact]
    public async Task A_layout_move_keeps_the_preview_current()
    {
        await using var f = ChainAnswers(new ApprovalFixture(Chain()));
        await f.Open();
        var preview = f.Preflight();
        f.Workflow = Edit(f.Workflow, new WorkflowEdit.MoveTasks([new(A, new(400, 300))]));

        var started = await Start(f, preview, BaseChoice.Head, Command(2));

        Assert.False(started.Existing);
        Assert.Equal(preview.Revision.Id, f.Read(Assert.Single(f.ApprovedRuns())).Revision.Id);
    }

    [Fact]
    public async Task A_commit_after_the_preview_refreshes_it_with_the_new_HEAD()
    {
        await using var f = ChainAnswers(new ApprovalFixture(Chain()));
        await f.Open();
        var preview = f.Preflight();
        f.Git.Write("later.txt", "later\n");
        var later = f.Git.Commit("later");

        var current = await Refreshed(f, preview, BaseChoice.Head, Command(3));

        Assert.Equal(later, current.Base!.Head);
        Assert.Equal(later, f.Read((await Start(f, current, BaseChoice.Head, Command(3))).Coordinator.Address.Run).Base.Commit);
    }

    [Fact]
    public async Task A_file_edit_refreshes_a_snapshot_preview_but_not_a_HEAD_one()
    {
        await using var f = ChainAnswers(new ApprovalFixture(Chain()));
        Uncommitted(f);
        await f.Open();
        var preview = f.Preflight();
        f.Git.Write("notes.txt", "notes again\n");

        var current = await Refreshed(f, preview, BaseChoice.Snapshot, Command(4));

        Assert.NotEqual(preview.Base!.WorkTree, current.Base!.WorkTree);
        var started = await Start(f, preview, BaseChoice.Head, Command(5));
        Assert.Equal(new RunBase(Head, BaseChoice.Head), f.Read(started.Coordinator.Address.Run).Base);
    }

    [Fact]
    public async Task Work_that_appears_after_a_clean_preview_refreshes_it_with_the_snapshot_choice()
    {
        await using var f = ChainAnswers(new ApprovalFixture(Chain()));
        await f.Open();
        var preview = f.Preflight();
        f.Git.Write("notes.txt", "notes\n");

        var current = await Refreshed(f, preview, BaseChoice.Head, Command(6));

        Assert.Equal<string>(["notes.txt"], current.Base!.Changed);
        Assert.Equal<BaseChoice>([BaseChoice.Head, BaseChoice.Snapshot], current.Choices);
    }
}
