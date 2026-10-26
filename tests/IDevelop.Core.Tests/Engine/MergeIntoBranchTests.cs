using IDevelop.Engine;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Engine.EngineFixture;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.Core.Tests.Engine;

/// <summary>Merge into my branch merges every end node's commit into the checked-out branch, once every node is complete and current.</summary>
public sealed class MergeIntoBranchTests : IAsyncLifetime
{
    private readonly EngineFixture _f = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _f.DisposeAsync().AsTask();

    [Fact]
    public async Task It_is_refused_until_every_node_is_complete_and_while_one_is_stale()
    {
        var (a, b) = (_f.Node("A"), _f.Node("B"));
        _f.Connect(a, b);
        _f.Install(On().Choose(
            (Fresh("A"), Reply("s-a", "Report A", On().Write("a.txt", "A\n"))),
            (Fresh("B"), Reply("s-b", "Report B", On().Write("b.txt", "B\n")))));
        var engine = await _f.Open();
        Assert.False(engine.CanMergeIntoBranch);
        await Assert.ThrowsAsync<InvalidOperationException>(engine.MergeIntoBranch);
        await engine.Run(a);
        await Until<NodeState.Completed>(engine, b);
        Assert.True(engine.CanMergeIntoBranch);

        engine.Update(_f.Workflow.Must(new WorkflowEdit.SetField(a, "instructions", "Do A again.")));

        Assert.Equal(new NodeState.Stale(), engine.State(a));
        Assert.False(engine.CanMergeIntoBranch);
        await Assert.ThrowsAsync<InvalidOperationException>(engine.MergeIntoBranch);
        Assert.Equal(_f.Start, _f.Head);
    }

    [Fact]
    public async Task A_workflow_of_nodes_that_only_read_offers_no_merge()
    {
        var (a, b) = (_f.Reader("A"), _f.Reader("B"));
        _f.Connect(a, b);
        _f.Install(On().Choose((Fresh("A"), Reply("s-a", "Report A")), (Fresh("B"), Reply("s-b", "Report B"))));
        var engine = await _f.Open();
        await engine.Run(a);

        await Until<NodeState.Completed>(engine, b);

        Assert.False(engine.CanMergeIntoBranch);
        await Assert.ThrowsAsync<InvalidOperationException>(engine.MergeIntoBranch);
        Assert.Equal(_f.Start, _f.Head);
    }

    [Fact]
    public async Task It_merges_the_end_nodes_commits_into_the_checked_out_branch()
    {
        var (a, b, c) = (_f.Node("A"), _f.Node("B"), _f.Node("C"));
        _f.Connect(a, b);
        _f.Install(On().Choose(
            (Fresh("A"), Reply("s-a", "Report A", On().Write("a.txt", "A\n"))),
            (Fresh("B"), Reply("s-b", "Report B", On().Write("b.txt", "B\n"))),
            (Fresh("C"), Reply("s-c", "Report C", On().Write("c.txt", "C\n")))));
        var engine = await _f.Open();
        await engine.RunWorkflow();
        await Until<NodeState.Completed>(engine, b);
        await Until<NodeState.Completed>(engine, c);

        Assert.True(engine.CanMergeIntoBranch);

        await engine.MergeIntoBranch();

        // The branch holds every end node's commit now, so there is nothing left to merge.
        Assert.False(engine.CanMergeIntoBranch);
        Assert.Equal(["README.md", "a.txt", "b.txt", "c.txt"], _f.Files("HEAD").Keys.Order(StringComparer.Ordinal));
        Assert.Equal("refs/heads/main", _f.Repository.Git("symbolic-ref", "HEAD").Trim());
        Assert.Equal("C\n", File.ReadAllText(Path.Combine(_f.Project, "c.txt")));
    }

    [Fact]
    public async Task It_is_refused_while_HEAD_is_detached()
    {
        var a = _f.Node("A");
        _f.Install(Reply("s-a", "Report A", On().Write("a.txt", "A\n")));
        var engine = await _f.Open();
        await engine.Run(a);
        await Until<NodeState.Completed>(engine, a);
        _f.Repository.Git("checkout", "-q", "--detach");

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(engine.MergeIntoBranch);

        Assert.Contains("detached", refused.Message, StringComparison.Ordinal);
        Assert.Equal(_f.Start, _f.Head);
    }

    [Fact]
    public async Task It_is_refused_when_the_end_nodes_conflict_with_each_other()
    {
        var (a, b) = (_f.Node("A"), _f.Node("B"));
        _f.Install(On().Choose(
            (Fresh("A"), Reply("s-a", "Report A", On().Write("x.txt", "A\n"))),
            (Fresh("B"), Reply("s-b", "Report B", On().Write("x.txt", "B\n")))));
        var engine = await _f.Open();
        await engine.RunWorkflow();
        await Until<NodeState.Completed>(engine, a);
        await Until<NodeState.Completed>(engine, b);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(engine.MergeIntoBranch);

        Assert.Contains("Merge node", refused.Message, StringComparison.Ordinal);
        Assert.Equal(_f.Start, _f.Head);
    }

    [Fact]
    public async Task It_is_refused_with_the_conflicting_paths_when_the_branch_moved()
    {
        var a = _f.Node("A");
        _f.Install(Reply("s-a", "Report A", On().Write("x.txt", "A\n")));
        var engine = await _f.Open();
        await engine.Run(a);
        await Until<NodeState.Completed>(engine, a);
        _f.Repository.Write("x.txt", "Mine\n");
        var moved = _f.Repository.Commit("Mine").Hex;

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(engine.MergeIntoBranch);

        Assert.Contains("x.txt", refused.Message, StringComparison.Ordinal);
        Assert.Equal(moved, _f.Head);
    }
}
