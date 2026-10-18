using IDevelop.Execution;
using IDevelop.TestSupport;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Materialization;

public sealed class BlockScopeTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Publish_reruns_past_its_own_repository_fault_but_not_another_operations(bool own)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await DoneWriter(f);
        var attempt = ready.Execution.Launch.Attempt;
        var publication = f.Op();
        var block = f.Op();
        Assert.Equal("Recorded", f.Store.Record(f.Permit, block, new RunEvent.Blocked(
            new(own ? publication : f.Op(), T, attempt, MaterializationProblem.UncertainOwnership, ready.Execution.Inputs, [],
                "The run belongs to a different repository common directory.") { Scope = new BlockScope.Repository() })).GetType().Name);
        var outcome = f.Materializer().Publish(f.Lease(T), publication, attempt);
        if (own)
        {
            Assert.Equal("done\n", Assert.IsType<Publication.Accepted>(outcome).Result.Report);
            Assert.True(f.Read().Blocks[block].Resolved);
        }
        else
        {
            Assert.Equal("The run belongs to a different repository common directory.", Assert.IsType<Publication.Blocked>(outcome).Block.Detail);
            Assert.False(f.Read().Blocks[block].Resolved);
            Assert.Empty(f.Read().Results);
        }
    }

    [UnixFact]
    public async Task Publish_retries_past_its_own_ancestry_failure()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await DoneWriter(f);
        var attempt = ready.Execution.Launch.Attempt;
        var tip = f.Read().RootExits[ready.Execution.Launch].Tip;
        var operation = f.Op();
        var failed = Assert.IsType<Publication.Blocked>(f.FailingGit($"merge-base --is-ancestor {ready.Execution.Location.AttemptBase.Hex} {tip.Hex}")
            .Publish(f.Lease(T), operation, attempt));
        Assert.Equal("GitFailed: fatal: injected failure\n", failed.Block.Problem + ": " + failed.Block.Detail);
        Assert.Equal(new BlockScope.Operation(), failed.Block.Scope);
        var fault = Assert.Single(f.Read().Blocks).Key;
        Assert.Equal("done\n", Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), operation, attempt)).Result.Report);
        Assert.True(f.Read().Blocks[fault].Resolved);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Publish_reruns_past_its_own_evidence_block_but_not_past_checkout_drift(bool own)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await DoneWriter(f);
        var attempt = ready.Execution.Launch.Attempt;
        var publication = f.Op();
        var block = f.Op();
        Assert.Equal("Recorded", f.Store.Record(f.Permit, block, new RunEvent.Blocked(
            new(publication, T, attempt, MaterializationProblem.UncertainOwnership, ready.Execution.Inputs, [], "The frozen publication recipe produced a different commit.")
            { Scope = own ? new BlockScope.Operation() : BlockScope.Checkout.Whole })).GetType().Name);
        var outcome = f.Materializer().Publish(f.Lease(T), publication, attempt);
        if (own)
        {
            Assert.Equal("done\n", Assert.IsType<Publication.Accepted>(outcome).Result.Report);
            Assert.True(f.Read().Blocks[block].Resolved);
            Assert.Single(f.Read().Results);
        }
        else
        {
            Assert.Equal("The frozen publication recipe produced a different commit.", Assert.IsType<Publication.Blocked>(outcome).Block.Detail);
            Assert.False(f.Read().Blocks[block].Resolved);
            Assert.Empty(f.Read().Results);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_ready_preparation_resolves_only_its_own_earlier_blocks(bool turn)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T) with { Conversation = IDevelop.Workflows.ConversationMode.Chat }));
        Preparation.Ready? first = null;
        if (turn)
        {
            first = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
            Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(T), f.Op(), first.Execution.Launch, f.ObserveAndLog(first)));
        }
        var operation = f.Op();
        var own = f.Op();
        var other = f.Op();
        foreach (var (id, recorder) in new[] { (own, operation), (other, f.Op()) })
            Assert.Equal("Recorded", f.Store.Record(f.Permit, id, new RunEvent.Blocked(
                new(recorder, T, first?.Execution.Launch.Attempt, MaterializationProblem.InputUnavailable, null, [], "Stored input was unreadable.")
                { Scope = new BlockScope.Operation() })).GetType().Name);
        Assert.IsType<Preparation.Ready>(turn
            ? await f.Materializer().PrepareTurn(f.Lease(T), operation, new(first!.Execution.Launch.Attempt, 2), "Continue.")
            : await f.Prepare(T, operation));
        Assert.True(f.Read().Blocks[own].Resolved);
        Assert.False(f.Read().Blocks[other].Resolved);
        Assert.Equal("Prepared.", Assert.Single(f.Read().Receipts.Values.Select(e => e.Event).OfType<RunEvent.BlockResolved>()).Reason);
    }

    [Fact]
    public void A_block_journaled_before_scopes_replays_as_unrecorded_and_encodes_unchanged()
    {
        var operation = new OperationId(Guid.Parse("6f9619ff-8b86-4d01-b42d-00c04fc964ff"));
        var block = new MaterializationBlock(operation, T, null, MaterializationProblem.DirtyWorktree, null, [], "Checkout changed.")
            { Scope = BlockScope.Checkout.Whole };
        var scoped = RunJournal.Encode(new RunEntry(3, 1, operation, Revision.Hash("legacy"), At, new RunEvent.Blocked(block)));
        const string scope = ",\"scope\":{\"type\":\"checkout\",\"paths\":[],\"branch\":false,\"head\":false,\"indexLock\":false}";
        Assert.Contains(scope, scoped);
        var legacy = scoped.Replace(scope, "");
        var read = RunJournal.Decode(legacy);
        Assert.Null(read.Rejection);
        var entry = Assert.Single(read.Entries);
        Assert.Same(BlockScope.Unrecorded.Value, Assert.IsType<RunEvent.Blocked>(entry.Event).Block.Scope);
        Assert.Equal(legacy, RunJournal.Encode(entry));
    }

    private static async Task<Preparation.Ready> DoneWriter(PreparationFixture f)
    {
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("result.txt", "done\n", ready.Checkout);
        await f.Close(ready, "done\n");
        return ready;
    }
}
