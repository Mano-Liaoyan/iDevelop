using IDevelop.Execution;
using IDevelop.Projects;
using IDevelop.TestSupport;
using static IDevelop.Core.Tests.Runs.RunFixtures;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;

namespace IDevelop.Core.Tests.Materialization;

public sealed class InspectionTests
{
    [Fact]
    public async Task Inspection_reads_published_code_and_later_dirty_files_without_changing_evidence_or_git()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var materializer = f.Materializer();
        Assert.Null(materializer.Inspect(W, new(Id(999)), T));
        var before = Assert.IsType<TaskWorkspace>(materializer.Inspect(W, f.RunId, T));
        Assert.Null(before.Owner);
        Assert.Equal(Path.GetFullPath(Path.Combine(f.Git.Folder, ".worktrees/93f23689/90d5b0a2")), before.Checkout);
        Assert.False(before.Registered);
        Assert.Null(before.Clean);
        Assert.Null(before.BranchTip);
        Assert.Null(before.LatestAcceptedCommit);
        Assert.False(Directory.Exists(Path.Combine(f.Git.Folder, ".worktrees")));
        Assert.False(Directory.Exists(Path.Combine(f.Git.Folder, ".git/idevelop")));
        var ready = await PublicationTests.ChangedWriter(f);
        Assert.IsType<Publication.Accepted>(materializer.Publish(f.Lease(ready.Execution.Location.Owner.Task), new(Id(2000)), ready.Execution.Launch.Attempt));
        var journal = Path.Combine(new RunStorage(f.Git.Folder, W, f.RunId).Folder, "events.jsonl");
        var bytes = File.ReadAllBytes(journal);
        var index = f.Git.Run(ready.Checkout, "rev-parse", "--path-format=absolute", "--git-path", "index").Text.Trim();
        var indexBytes = File.ReadAllBytes(index);
        var registrations = f.Git.Git("worktree", "list", "--porcelain");
        var taskLock = f.Lease(T);
        Assert.True(taskLock.Held);
        using var mutationLock = f.Git.Open().TakeMutationLock();
        var workspace = Assert.IsType<TaskWorkspace>(materializer.Inspect(W, f.RunId, T));
        Assert.Equal(new WorktreeOwner(T, ".worktrees/93f23689/90d5b0a2", "refs/heads/idp/93f23689/task/90d5b0a2"), workspace.Owner);
        Assert.Equal(Path.GetFullPath(Path.Combine(f.Git.Folder, ".worktrees/93f23689/90d5b0a2")), workspace.Checkout);
        Assert.True(workspace.Registered);
        Assert.True(workspace.Clean);
        Assert.Equal("81cae59086bf9597301026b65f1bb57380b74686", workspace.BranchTip?.Hex);
        Assert.Equal("81cae59086bf9597301026b65f1bb57380b74686", workspace.LatestAcceptedCommit?.Hex);
        Assert.Empty(workspace.Blocks);
        Assert.Empty(workspace.Salvages);
        f.Git.Write("new.txt", "late\n", ready.Checkout);
        var dirty = Assert.IsType<TaskWorkspace>(materializer.Inspect(W, f.RunId, T));
        Assert.False(dirty.Clean);
        Assert.Equal("81cae59086bf9597301026b65f1bb57380b74686", dirty.BranchTip?.Hex);
        Assert.Equal("81cae59086bf9597301026b65f1bb57380b74686", dirty.LatestAcceptedCommit?.Hex);
        Assert.Equal("late\n", File.ReadAllText(Path.Combine(dirty.Checkout, "new.txt")));
        Assert.Equal(bytes, File.ReadAllBytes(journal));
        Assert.Equal(indexBytes, File.ReadAllBytes(index));
        Assert.Equal(registrations, f.Git.Git("worktree", "list", "--porcelain"));
    }

    [Fact]
    public async Task Inspection_orders_unresolved_blocks_and_retained_salvage_by_journal_sequence()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await SalvageTests.FailedWriter(f);
        foreach (var id in new[] { 3002, 3001, 3000 })
            Assert.Equal("LiveWriter", Assert.IsType<Salvage.Blocked>(f.Materializer(boundary: new UnprovenBoundary())
                .Salvage(f.Lease(ready.Execution.Location.Owner.Task), new(Id(id)), ready.Execution.Launch.Attempt)).Block.Problem.ToString());
        var resolved = f.Read().Blocks.Single(pair => pair.Value.Block.Operation == new OperationId(Id(3001))).Key;
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, f.Op(), new RunEvent.BlockResolved(resolved, "Inspected.")));
        Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), new(Id(2000)), ready.Execution.Launch.Attempt));
        var workspace = Assert.IsType<TaskWorkspace>(f.Materializer().Inspect(W, f.RunId, T));
        Assert.Equal(new[] { "00000000-0000-0000-0000-000000003002", "00000000-0000-0000-0000-000000003000" },
            workspace.Blocks.Select(block => block.Operation.Value.ToString("D")));
        var salvage = Assert.Single(workspace.Salvages);
        Assert.Equal("refs/idp/93f23689/salvage/90d5b0a2/00000000-0000-0000-0000-000000000102", salvage.Ref);
        Assert.Equal("0dbf5cbc9310ef3abbc28073142652917c69dc2f", salvage.Commit.Hex);
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", workspace.BranchTip?.Hex);
        Assert.False(workspace.Clean);
        Assert.Null(workspace.LatestAcceptedCommit);
    }
}
