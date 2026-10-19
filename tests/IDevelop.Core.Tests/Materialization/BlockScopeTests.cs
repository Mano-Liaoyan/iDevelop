using IDevelop.Execution;
using IDevelop.TestSupport;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Materialization;

public sealed class BlockScopeTests
{
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
    public void A_block_without_a_scope_is_invalid_data()
    {
        var operation = new OperationId(Guid.Parse("6f9619ff-8b86-4d01-b42d-00c04fc964ff"));
        var block = new MaterializationBlock(operation, T, null, MaterializationProblem.DirtyWorktree, null, [], "Checkout changed.")
            { Scope = BlockScope.Checkout.Whole };
        var scoped = RunJournal.Encode(new RunEntry(RunJournal.Schema, 1, operation, Revision.Hash("block"), At, new RunEvent.Blocked(block)));
        const string scope = ",\"scope\":{\"type\":\"checkout\",\"paths\":[],\"branch\":false,\"head\":false}";
        Assert.Contains(scope, scoped);
        Assert.Equal(RunJournal.Canonical(block), RunJournal.Canonical(Assert.IsType<RunEvent.Blocked>(Assert.Single(RunJournal.Decode(scoped).Entries).Event).Block));
        Assert.Equal(new RunRejection(RunProblem.InvalidData, 1), RunJournal.Decode(scoped.Replace(scope, "")).Rejection);
    }
}
