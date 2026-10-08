using System.Collections.Immutable;
using IDevelop.Execution;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Materialization;

public sealed class ReceiptResolutionTests
{
    private sealed class Crash : Exception;

    [Theory]
    [InlineData("restore", "unknown")]
    [InlineData("restore", "not-block")]
    [InlineData("restore", "resolved")]
    [InlineData("restore", "duplicate")]
    [InlineData("restore", "other-task")]
    [InlineData("restore", "default")]
    [InlineData("restore", "empty-id")]
    [InlineData("baseline", "unknown")]
    [InlineData("baseline", "not-block")]
    [InlineData("baseline", "resolved")]
    [InlineData("baseline", "duplicate")]
    [InlineData("baseline", "other-task")]
    [InlineData("baseline", "default")]
    [InlineData("baseline", "empty-id")]
    public async Task A_receipt_rejects_invalid_resolutions_without_partially_resolving_blocks(string command, string invalid)
    {
        if (command == "restore" && !OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(C)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await f.Close(ready);
        var attempt = ready.Execution.Launch.Attempt;
        f.Git.Write("a.txt", "late\n", ready.Checkout);
        var preservation = f.Op();
        Assert.Equal("Preserved", (await f.Materializer().Preserve(f.Lease(T), preservation, attempt)).GetType().Name);
        var drift = OperationIds.Derive(preservation, "preserve-drift");
        var operation = f.Op();
        var confirmation = f.Op();
        var plan = OperationIds.Derive(operation, "restore-plan");
        var preview = command == "restore" ? RestoreTests.Preview(f, ready, preservation).Identity : Revision.Hash("unused");
        var crashing = f.Materializer(probe: step =>
        {
            if (step == (command == "restore" ? "journal.restored.before" : "journal.baseline.before")) throw new Crash();
        });
        if (command == "restore")
            Assert.Throws<Crash>(() => crashing.Restore(f.Lease(T), operation, attempt, preservation, confirmation, preview));
        else
            Assert.Throws<Crash>(() => crashing.RecordRecoveryBaseline(f.Lease(T), operation, attempt, confirmation, preservation));
        var own = f.Op();
        Assert.Equal("Recorded", f.Store.Record(f.Permit, own, new RunEvent.Blocked(
            new(command == "restore" ? plan : operation, T, attempt, MaterializationProblem.InputUnavailable,
                ready.Execution.Inputs, [], "Own failure."))).GetType().Name);
        var other = f.Op();
        Assert.Equal("Recorded", f.Store.Record(f.Permit, other, new RunEvent.Blocked(
            new(other, invalid == "other-task" ? C : T, null, MaterializationProblem.GitFailed, null, [], "Other failure."))).GetType().Name);
        if (invalid == "resolved")
            Assert.Equal("Recorded", f.Store.Record(f.Permit, f.Op(), new RunEvent.BlockResolved(other, "Manually resolved.")).GetType().Name);
        ImmutableArray<OperationId> ids = invalid switch
        {
            "unknown" => [own, f.Op()],
            "not-block" => [own, OperationIds.Derive(preservation, "preserve-plan")],
            "resolved" or "other-task" => [own, other],
            "duplicate" => [own, own],
            "default" => default,
            "empty-id" => [own, new(Guid.Empty)],
            _ => throw new InvalidOperationException(),
        };
        RunEvent malformed = command == "restore" ? new RunEvent.Restored(plan, ids) :
            new RunEvent.RecoveryBaselined(new(attempt, confirmation, "fixture", preservation), ids);
        var sequence = f.Read().Sequence;
        var rejection = invalid == "default"
            ? Assert.IsType<RunRead.Rejected>(RunReducer.Apply(W, f.RunId, f.Read(),
                new(3, sequence + 1, f.Op(), Revision.Hash("malformed receipt"), At, malformed))).Reason
            : Assert.IsType<RunDecision.Rejected>(f.Store.Record(f.Permit, f.Op(), malformed)).Reason;
        Assert.Equal("InvalidData", rejection.Problem.ToString());
        Assert.Equal(sequence, f.Read().Sequence);
        Assert.False(f.Read().Blocks[own].Resolved);
        Assert.False(f.Read().Blocks[drift].Resolved);
        Assert.Empty(f.Read().Restorations);
        Assert.Empty(f.Read().Baselines);
        ImmutableArray<OperationId> resolved;
        if (command == "restore")
        {
            var result = Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(T), operation, attempt, preservation, confirmation, preview));
            resolved = result.Receipt.Resolved;
            Assert.Equal(1, RestoreTests.Moves(f, operation));
            Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        }
        else
        {
            var result = Assert.IsType<RecoveryBaselining.Recorded>(f.Materializer().RecordRecoveryBaseline(f.Lease(T), operation, attempt, confirmation, preservation));
            resolved = result.Receipt.Resolved;
            Assert.Equal("fixture", result.Receipt.Baseline.Session);
            Assert.Equal("late\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        }
        Assert.Equal(new[] { drift, own }, resolved);
        Assert.True(f.Read().Blocks[own].Resolved);
        Assert.True(f.Read().Blocks[drift].Resolved);
        Assert.Equal(invalid == "resolved", f.Read().Blocks[other].Resolved);
        Assert.Equal(1, f.Read().Restorations.Count + f.Read().Baselines.Count);
    }
}
