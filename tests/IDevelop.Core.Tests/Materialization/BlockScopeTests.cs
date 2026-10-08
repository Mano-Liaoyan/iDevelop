using IDevelop.Execution;
using IDevelop.TestSupport;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Materialization;

public sealed class BlockScopeTests
{
    [LinuxOrWindowsTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Restore_clears_a_publication_ownership_refusal_only_after_the_lock_is_back(bool relock)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await DoneWriter(f);
        var attempt = ready.Execution.Launch.Attempt;
        var reason = LockReason(f);
        Assert.Equal(0, f.Git.Run(f.Git.Folder, "worktree", "unlock", ready.Checkout).ExitCode);
        var publication = f.Op();
        var refused = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(f.Lease(T), publication, attempt));
        Assert.Equal("UncertainOwnership: The recorded worktree ownership lock is absent or differs.", refused.Block.Problem + ": " + refused.Block.Detail);
        var block = Assert.Single(f.Read().Blocks).Key;
        Assert.Equal(0, f.Git.Run(f.Git.Folder, "worktree", "lock", "--reason", relock ? reason : "someone else", ready.Checkout).ExitCode);
        var preservation = f.Op();
        var preserved = await f.Materializer().Preserve(f.Lease(T), preservation, attempt);
        if (!relock)
        {
            var held = Assert.IsType<Preservation.Blocked>(preserved);
            Assert.Equal("UncertainOwnership: The recorded worktree ownership lock is absent or differs.", held.Block.Problem + ": " + held.Block.Detail);
            Assert.False(f.Read().Blocks[block].Resolved);
            Assert.Equal("Blocked", f.Materializer().Publish(f.Lease(T), publication, attempt).GetType().Name);
            Assert.Empty(f.Read().Results);
            return;
        }
        Assert.IsType<Preservation.Preserved>(preserved);
        var preview = RestoreTests.Preview(f, ready, preservation);
        Assert.Empty(preview.Paths);
        Assert.Equal(new[] { block }, preview.Repairs);
        Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(T), f.Op(), attempt, preservation, f.Op(), preview.Identity));
        Assert.True(f.Read().Blocks[block].Resolved);
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), publication, attempt));
        Assert.Equal("done\n", accepted.Result.Report);
        Assert.Single(f.Read().Results);
    }

    [LinuxOrWindowsFact]
    public async Task A_pin_conflict_stays_blocked_until_its_own_preservation_rewrites_the_pin()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await DoneWriter(f);
        var attempt = ready.Execution.Launch.Attempt;
        var first = f.Op();
        var record = f.Read();
        var pin = RunLayout.PreservationPin(record.RunKey!, record.TaskKeys[T], attempt, first, 1);
        var fired = false;
        var conflicted = Assert.IsType<Preservation.Blocked>(await f.Materializer(probe: step =>
        {
            if (step != "git.pin.before" || fired) return;
            fired = true;
            f.Git.Git("update-ref", pin, f.A.Hex);
        }).Preserve(f.Lease(T), first, attempt));
        Assert.Equal("UncertainOwnership: The retention pin changed while it was being updated.", conflicted.Block.Problem + ": " + conflicted.Block.Detail);
        var block = Assert.Single(f.Read().Blocks).Key;
        var second = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), second, attempt));
        var preview = RestoreTests.Preview(f, ready, second);
        Assert.Empty(preview.Repairs);
        Assert.Empty(preview.Rechecks);
        Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(T), f.Op(), attempt, second, f.Op(), preview.Identity));
        Assert.False(f.Read().Blocks[block].Resolved);
        Assert.Equal(f.A.Hex, f.Git.Git("rev-parse", pin).Trim());
        var publication = f.Op();
        var gated = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(f.Lease(T), publication, attempt));
        Assert.Equal("The retention pin changed while it was being updated.", gated.Block.Detail);
        var rerun = Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), first, attempt));
        Assert.True(f.Read().Blocks[block].Resolved);
        Assert.Equal(rerun.Commit.Hex, f.Git.Git("rev-parse", pin).Trim());
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), publication, attempt));
        Assert.Equal("done\n", accepted.Result.Report);
    }

    [LinuxOrWindowsFact]
    public async Task A_superseding_restore_resolves_the_input_block_of_the_restore_it_replaces()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await f.Close(ready);
        var attempt = ready.Execution.Launch.Attempt;
        f.Git.Write("a.txt", "late\n", ready.Checkout);
        var first = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), first, attempt));
        var preview = RestoreTests.Preview(f, ready, first);
        var operation = f.Op();
        var confirmation = f.Op();
        var held = Assert.IsType<Restoration.Blocked>(f.Materializer(probe: step =>
        {
            if (step == "restore.file.a.txt.written") throw new IOException("The process cannot access the file because it is being used by another process.");
        }).Restore(f.Lease(T), operation, attempt, first, confirmation, preview.Identity));
        Assert.Equal("InputUnavailable", held.Block.Problem.ToString());
        Assert.Equal(new[] { "DirtyWorktree", "InputUnavailable" }, Open(f));
        f.Git.Write("a.txt", "later\n", ready.Checkout);
        var second = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), second, attempt));
        var next = RestoreTests.Preview(f, ready, second);
        Assert.Equal(OperationIds.Derive(operation, "restore-plan"), next.Supersedes);
        Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(T), f.Op(), attempt, second, f.Op(), next.Identity));
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        var rerun = Assert.IsType<Restoration.Rejected>(f.Materializer().Restore(f.Lease(T), operation, attempt, first, confirmation, preview.Identity));
        Assert.Equal("ReplacementConflict", rerun.Reason.Problem.ToString());
        Assert.Empty(Open(f));
        Assert.Equal("Accepted", f.Materializer().Publish(f.Lease(T), f.Op(), attempt).GetType().Name);
    }

    [LinuxOrWindowsFact]
    public async Task A_baseline_from_another_operation_resolves_the_failed_baseline_and_the_earlier_drift()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await RecoveryBaselineTests.CloseInterrupted(f, ready);
        var previous = ready.Execution.Launch.Attempt;
        f.Git.Write("keep.txt", "keep\n", ready.Checkout);
        var first = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), first, previous));
        f.Git.Write("keep.txt", "keep more\n", ready.Checkout);
        var failed = f.Op();
        var confirmation = f.Op();
        var refused = Assert.IsType<RecoveryBaselining.Blocked>(f.Materializer().RecordRecoveryBaseline(f.Lease(T), failed, previous, confirmation, first));
        Assert.Equal("DirtyWorktree", refused.Block.Problem.ToString());
        Assert.Equal(new[] { "DirtyWorktree", "DirtyWorktree" }, Open(f));
        var second = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), second, previous));
        Assert.IsType<RecoveryBaselining.Recorded>(f.Materializer().RecordRecoveryBaseline(f.Lease(T), f.Op(), previous, confirmation, second));
        Assert.Empty(Open(f));
        var replay = Assert.IsType<RecoveryBaselining.Recorded>(f.Materializer().RecordRecoveryBaseline(f.Lease(T), failed, previous, confirmation, first));
        Assert.Equal(second, replay.Receipt.Baseline.Preservation);
        Assert.Equal("keep more\n", File.ReadAllText(Path.Combine(ready.Checkout, "keep.txt")));
    }

    private static string[] Open(PreparationFixture f) =>
        [.. f.Materializer().Inspect(W, f.RunId, T)!.Blocks.Select(b => b.Problem.ToString())];

    private static string LockReason(PreparationFixture f) =>
        f.Git.Run(f.Git.Folder, "worktree", "list", "--porcelain").Text.Split('\n')
            .Single(line => line.StartsWith("locked ", StringComparison.Ordinal))["locked ".Length..];

    private static async Task<Preparation.Ready> DoneWriter(PreparationFixture f)
    {
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("result.txt", "done\n", ready.Checkout);
        await f.Close(ready, "done\n");
        return ready;
    }
}
