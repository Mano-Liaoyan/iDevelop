using System.Runtime.Versioning;
using IDevelop.Core.Tests.Git;
using IDevelop.Execution;
using IDevelop.Workflows;
using IDevelop.TestSupport;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Materialization;

public sealed class RestoreTests
{
    private sealed class Crash : Exception;

    private sealed record RestoreCase(Preparation.Ready Ready, OperationId Preservation, OperationId Operation,
        OperationId Confirmation, RestorePreview Preview, string IndexPath, string ScratchRoot);

    [LinuxOrWindowsTheory]
    [InlineData("both unknown")]
    [InlineData("checkout unknown")]
    [InlineData("Git folder unknown")]
    [InlineData("different")]
    public async Task Restore_requires_proven_same_volume_evidence(string evidence)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await f.Close(ready);
        var attempt = ready.Execution.Launch.Attempt;
        f.Git.Write("a.txt", "drifted\n", ready.Checkout);
        var preservation = f.Op();
        Assert.Equal("Preserved", (await f.Materializer().Preserve(f.Lease(T), preservation, attempt)).GetType().Name);
        var materializer = f.Materializer(volumes: path => evidence switch
        {
            "both unknown" => null,
            "checkout unknown" => path == ready.Checkout ? null : 1UL,
            "Git folder unknown" => path == ready.Checkout ? 1UL : null,
            _ => path == ready.Checkout ? 1UL : 2UL,
        });
        var refused = Assert.IsType<RestorePreviewRead.Refused>(materializer.PreviewRestore(f.Lease(T), attempt, preservation));
        var detail = evidence == "different"
            ? "The checkout and its Git folder are on different file systems, so Restore cannot replace files atomically."
            : "iDevelop cannot confirm that the checkout and its Git folder share a file system on this system, so Restore will not replace files. Change them outside iDevelop, then preserve again.";
        Assert.Equal("DirtyWorktree", refused.Problem.ToString());
        Assert.Equal(detail, refused.Detail);
        Assert.Equal(new BlockScope.Checkout(["a.txt"]), refused.Scope);
        Assert.Equal("drifted\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        var operation = f.Op();
        var blocked = Assert.IsType<Restoration.Blocked>(materializer.Restore(f.Lease(T), operation, attempt, preservation, f.Op(), Revision.Hash("refused preview")));
        Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
        Assert.Equal(detail, blocked.Block.Detail);
        Assert.Equal(new BlockScope.Checkout(["a.txt"]), blocked.Block.Scope);
        Assert.Equal(0, Moves(f, operation));
        Assert.Equal("drifted\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        var preview = Preview(f, ready, preservation);
        Assert.Equal("Restored", f.Materializer().Restore(f.Lease(T), f.Op(), attempt, preservation, f.Op(), preview.Identity).GetType().Name);
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
    }

    [OtherPlatformFact]
    public async Task Restore_refuses_file_moves_without_platform_volume_evidence()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await f.Close(ready);
        var attempt = ready.Execution.Launch.Attempt;
        f.Git.Write("a.txt", "drifted\n", ready.Checkout);
        var preservation = f.Op();
        Assert.Equal("Preserved", (await f.Materializer().Preserve(f.Lease(T), preservation, attempt)).GetType().Name);
        var refused = Assert.IsType<RestorePreviewRead.Refused>(f.Materializer().PreviewRestore(f.Lease(T), attempt, preservation));
        Assert.Equal("DirtyWorktree", refused.Problem.ToString());
        Assert.Equal("iDevelop cannot confirm that the checkout and its Git folder share a file system on this system, so Restore will not replace files. Change them outside iDevelop, then preserve again.", refused.Detail);
        Assert.Equal(new BlockScope.Checkout(["a.txt"]), refused.Scope);
        Assert.Equal("drifted\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        f.Git.Write("a.txt", "A\n", ready.Checkout);
        var repaired = f.Op();
        Assert.Equal("Preserved", (await f.Materializer().Preserve(f.Lease(T), repaired, attempt)).GetType().Name);
        var preview = Preview(f, ready, repaired);
        Assert.Empty(preview.Paths);
        Assert.Equal("Restored", f.Materializer().Restore(f.Lease(T), f.Op(), attempt, repaired, f.Op(), preview.Identity).GetType().Name);
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
    }

    [LinuxOrWindowsFact]
    public async Task A_restore_receipt_crash_resolves_every_covered_block_atomically()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await f.Close(ready);
        var attempt = ready.Execution.Launch.Attempt;
        f.Git.Write("a.txt", "late\n", ready.Checkout);
        Assert.Equal("Blocked", f.Materializer().Publish(f.Lease(T), f.Op(), attempt).GetType().Name);
        var publicationBlock = Assert.Single(f.Read().Blocks).Key;
        var preservation = f.Op();
        Assert.Equal("Preserved", (await f.Materializer().Preserve(f.Lease(T), preservation, attempt)).GetType().Name);
        var preview = Preview(f, ready, preservation);
        Assert.Equal(2, f.Read().Blocks.Values.Count(b => !b.Resolved && b.Block.Task == T));
        var operation = f.Op();
        var confirmation = f.Op();
        Assert.Throws<Crash>(() => f.Materializer(probe: step => { if (step == "journal.restored.after") throw new Crash(); })
            .Restore(f.Lease(T), operation, attempt, preservation, confirmation, preview.Identity));
        var receipt = f.Read().Restorations[OperationIds.Derive(operation, "restore-plan")];
        Assert.Equal(new[] { publicationBlock, OperationIds.Derive(preservation, "preserve-drift") }, receipt.Resolved);
        Assert.Equal(2, receipt.Resolved.Length);
        Assert.Equal(0, f.Read().Blocks.Values.Count(b => !b.Resolved && b.Block.Task == T));
        var sequence = f.Read().Sequence;
        var restored = f.Materializer().Restore(f.Lease(T), operation, attempt, preservation, confirmation, preview.Identity);
        Assert.Equal("Restored", restored.GetType().Name);
        Assert.Equal(receipt, Assert.IsType<Restoration.Restored>(restored).Receipt);
        Assert.Equal(sequence, f.Read().Sequence);
        Assert.Equal(0, f.Read().Blocks.Values.Count(b => !b.Resolved && b.Block.Task == T));
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        var published = f.Materializer().Publish(f.Lease(T), f.Op(), attempt);
        Assert.Equal("Accepted", published.GetType().Name);
        Assert.Equal("B ready.\n", Assert.IsType<Publication.Accepted>(published).Result.Report);
    }

    [LinuxOrWindowsFact]
    public async Task Restore_applies_the_repository_clean_and_smudge_filter()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await f.Close(ready);
        var attempt = ready.Execution.Launch.Attempt;
        Assert.Equal(0, f.Git.Run(f.Git.Folder, "config", "filter.mark.clean", "grep -v '^smudged$' || true").ExitCode);
        Assert.Equal(0, f.Git.Run(f.Git.Folder, "config", "filter.mark.smudge", "cat && echo smudged").ExitCode);
        File.WriteAllText(Path.Combine(f.Git.Open().CommonDirectory, "info", "attributes"), "*.txt filter=mark\n");
        File.Delete(Path.Combine(ready.Checkout, "a.txt"));
        Assert.Equal(0, f.Git.Run(ready.Checkout, "checkout", "--", "a.txt").ExitCode);
        Assert.Equal("A\nsmudged\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal("", f.Git.Run(ready.Checkout, "status", "--porcelain").Text);
        f.Git.Write("a.txt", "late\nsmudged\n", ready.Checkout);
        Assert.Equal(" M a.txt\n", f.Git.Run(ready.Checkout, "status", "--porcelain").Text);
        var preservation = f.Op();
        Assert.Equal("Preserved", (await f.Materializer().Preserve(f.Lease(T), preservation, attempt)).GetType().Name);
        var preview = Preview(f, ready, preservation);
        Assert.Equal(new[] { "a.txt" }, preview.Paths.Select(p => p.Path));
        var restored = f.Materializer().Restore(f.Lease(T), f.Op(), attempt, preservation, f.Op(), preview.Identity);
        Assert.Equal("Restored", restored.GetType().Name);
        Assert.Equal("A\nsmudged\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal("", f.Git.Run(ready.Checkout, "status", "--porcelain").Text);
    }

    [Theory]
    [InlineData("--skip-worktree", "--no-skip-worktree")]
    [InlineData("--assume-unchanged", "--no-assume-unchanged")]
    public async Task Restore_refuses_hidden_index_flags_even_when_the_index_does_not_move(string flag, string clear)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await f.Close(ready);
        var attempt = ready.Execution.Launch.Attempt;
        var preservation = f.Op();
        Assert.Equal("Preserved", (await f.Materializer().Preserve(f.Lease(T), preservation, attempt)).GetType().Name);
        var preview = Preview(f, ready, preservation);
        Assert.Empty(preview.Paths);
        Assert.Equal(preview.Current.IndexTree, preview.To.IndexTree);
        Assert.Equal(0, f.Git.Run(ready.Checkout, "update-index", flag, "root.txt").ExitCode);
        var refused = Assert.IsType<RestorePreviewRead.Refused>(f.Materializer().PreviewRestore(f.Lease(T), attempt, preservation));
        Assert.Equal("DirtyWorktree", refused.Problem.ToString());
        Assert.Equal("Restore needs a plain index. The index marks entries assume-unchanged or skip-worktree.", refused.Detail);
        var operation = f.Op();
        var blocked = Assert.IsType<Restoration.Blocked>(f.Materializer().Restore(f.Lease(T), operation, attempt, preservation, f.Op(), preview.Identity));
        Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
        Assert.Equal("Restore needs a plain index. The index marks entries assume-unchanged or skip-worktree.", blocked.Block.Detail);
        Assert.Equal(0, Moves(f, operation));
        Assert.Equal("root\n", File.ReadAllText(Path.Combine(ready.Checkout, "root.txt")));
        Assert.Equal(0, f.Git.Run(ready.Checkout, "update-index", clear, "root.txt").ExitCode);
        var repaired = Preview(f, ready, preservation);
        Assert.Equal("Restored", f.Materializer().Restore(f.Lease(T), f.Op(), attempt, preservation, f.Op(), repaired.Identity).GetType().Name);
        Assert.Equal("root\n", File.ReadAllText(Path.Combine(ready.Checkout, "root.txt")));
    }

    [LinuxOrWindowsFact]
    public async Task Restore_refuses_a_smudge_result_that_does_not_clean_to_the_preview_blob()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await f.Close(ready);
        var attempt = ready.Execution.Launch.Attempt;
        Assert.Equal(0, f.Git.Run(f.Git.Folder, "config", "filter.mark.clean", "cat").ExitCode);
        Assert.Equal(0, f.Git.Run(f.Git.Folder, "config", "filter.mark.smudge", "cat && echo smudged").ExitCode);
        File.WriteAllText(Path.Combine(f.Git.Open().CommonDirectory, "info", "attributes"), "*.txt filter=mark\n");
        f.Git.Write("a.txt", "late\n", ready.Checkout);
        var preservation = f.Op();
        Assert.Equal("Preserved", (await f.Materializer().Preserve(f.Lease(T), preservation, attempt)).GetType().Name);
        var preview = Preview(f, ready, preservation);
        var operation = f.Op();
        var confirmation = f.Op();
        var blocked = Assert.IsType<Restoration.Blocked>(f.Materializer().Restore(f.Lease(T), operation, attempt, preservation, confirmation, preview.Identity));
        Assert.Equal("InputUnavailable", blocked.Block.Problem.ToString());
        Assert.Equal("The restore blob differs from its preview.", blocked.Block.Detail);
        Assert.Equal(new BlockScope.Checkout(["a.txt"]), blocked.Block.Scope);
        Assert.Equal(0, Moves(f, operation));
        Assert.Equal("late\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal(0, f.Git.Run(f.Git.Folder, "config", "filter.mark.smudge", "cat").ExitCode);
        Assert.Equal("Restored", f.Materializer().Restore(f.Lease(T), operation, attempt, preservation, confirmation, preview.Identity).GetType().Name);
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal(1, Moves(f, operation));
    }

    [Fact]
    public async Task Conflicting_stages_after_publication_refuse_restore_without_removing_the_file()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await f.Close(ready);
        var attempt = ready.Execution.Launch.Attempt;
        var published = f.Materializer().Publish(f.Lease(T), f.Op(), attempt);
        Assert.Equal("Accepted", published.GetType().Name);
        Assert.Equal("B ready.\n", Assert.IsType<Publication.Accepted>(published).Result.Report);
        var entries = new System.Text.StringBuilder();
        var contents = new[] { "base\n", "ours\n", "theirs\n" };
        for (var stage = 1; stage <= 3; stage++)
        {
            var blob = await f.Git.RunWithInput(ready.Checkout, contents[stage - 1], "hash-object", "-w", "--stdin");
            Assert.Equal(0, blob.ExitCode);
            entries.Append($"100644 {blob.Text.Trim()} {stage}\tc.txt\n");
        }
        Assert.Equal(0, (await f.Git.RunWithInput(ready.Checkout, entries.ToString(), "update-index", "--index-info")).ExitCode);
        f.Git.Write("c.txt", "conflicted\n", ready.Checkout);
        Assert.Equal(3, f.Git.Run(ready.Checkout, "ls-files", "-u").Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        var preservation = f.Op();
        Assert.Equal("Preserved", (await f.Materializer().Preserve(f.Lease(T), preservation, attempt)).GetType().Name);
        var preview = f.Materializer().PreviewRestore(f.Lease(T), attempt, preservation);
        if (preview is RestorePreviewRead.Previewed succeeded)
            f.Materializer().Restore(f.Lease(T), f.Op(), attempt, preservation, f.Op(), succeeded.Preview.Identity);
        Assert.True(File.Exists(Path.Combine(ready.Checkout, "c.txt")));
        Assert.Equal("conflicted\n", File.ReadAllText(Path.Combine(ready.Checkout, "c.txt")));
        Assert.Equal(3, f.Git.Run(ready.Checkout, "ls-files", "-u").Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal("Refused", preview.GetType().Name);
        var refused = Assert.IsType<RestorePreviewRead.Refused>(preview);
        Assert.Equal("DirtyWorktree", refused.Problem.ToString());
        Assert.Equal("Restore needs a plain index. The index has unresolved stages.", refused.Detail);
        Assert.Equal(new BlockScope.Checkout(["c.txt"]), refused.Scope);
    }

    [LinuxOrWindowsTheory]
    [InlineData("before")]
    [InlineData("journal.restore-plan.after")]
    [InlineData("git.restore-file-a.txt.before")]
    [InlineData("restore.file.a.txt.written")]
    public async Task An_unresolved_claim_before_or_during_restore_prevents_every_move(string point)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        var attempt = ready.Execution.Launch.Attempt;
        f.Git.Write("a.txt", "drifted\n", ready.Checkout);
        var preservation = f.Op();
        Assert.Equal("Preserved", (await f.Materializer().Preserve(f.Lease(T), preservation, attempt)).GetType().Name);
        var preview = Preview(f, ready, preservation);
        Assert.Equal(new[] { "a.txt" }, preview.Paths.Select(p => p.Path));
        if (point == "before") RootExitTests.Claim(f, ready);
        var operation = f.Op();
        var restored = f.Materializer(probe: step =>
        {
            if (step == point) RootExitTests.Claim(f, ready);
        }).Restore(f.Lease(T), operation, attempt, preservation, f.Op(), preview.Identity);
        Assert.Equal("Rejected", restored.GetType().Name);
        Assert.Equal("UnresolvedOwnership", Assert.IsType<Restoration.Rejected>(restored).Reason.Problem.ToString());
        Assert.Equal(new[] { ready.Execution.Launch }, f.Read().UnresolvedClaims);
        Assert.Equal(point == "before" ? "no plan" : "planned",
            f.Read().Plans.ContainsKey(OperationIds.Derive(operation, "restore-plan")) ? "planned" : "no plan");
        Assert.Equal(0, Moves(f, operation));
        Assert.Equal("drifted\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch))?.Hex);
        Assert.Equal("Observed", f.Materializer().ObserveRootExit(f.Lease(T), f.Op(), ready.Execution.Launch, new RootExit.Exited(1)).GetType().Name);
        await f.Close(ready, outcome: TerminalAttemptOutcome.Failed);
        f.Git.Write("a.txt", "later\n", ready.Checkout);
        var fresh = f.Op();
        Assert.Equal("Preserved", (await f.Materializer().Preserve(f.Lease(T), fresh, attempt)).GetType().Name);
        var control = f.Op();
        Assert.Equal("Restored", f.Materializer().Restore(f.Lease(T), control, attempt, fresh, f.Op(), Preview(f, ready, fresh).Identity).GetType().Name);
        Assert.Equal(1, Moves(f, control));
        Assert.Equal("drifted\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
    }

    [LinuxOrWindowsTheory]
    [InlineData("none")]
    [InlineData("journal.restore-head-intent.after")]
    [InlineData("git.restore-head.after")]
    [InlineData("journal.restore-head-observed.before")]
    public async Task Detached_head_restore_converges_after_a_crash(string point)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await f.Close(ready);
        var attempt = ready.Execution.Launch.Attempt;
        var branch = ready.Execution.Location.Owner.Branch;
        Assert.Equal(0, f.Git.Run(ready.Checkout, "checkout", "-q", "--detach").ExitCode);
        f.Git.Write("a.txt", "late\n", ready.Checkout);
        var preservation = f.Op();
        Assert.Equal("Preserved", (await f.Materializer().Preserve(f.Lease(T), preservation, attempt)).GetType().Name);
        var preview = Preview(f, ready, preservation);
        Assert.Equal(new[] { "HEAD" }, preview.Refs);
        Assert.Equal(1, f.Read().Blocks.Values.Count(block => !block.Resolved));
        var operation = f.Op();
        var confirmation = f.Op();
        if (point != "none")
            Assert.Throws<Crash>(() => f.Materializer(probe: step => { if (step == point) throw new Crash(); })
                .Restore(f.Lease(T), operation, attempt, preservation, confirmation, preview.Identity));
        var outcome = f.Materializer().Restore(f.Lease(T), operation, attempt, preservation, confirmation, preview.Identity);
        Assert.Equal("Restored", outcome.GetType().Name);
        Assert.Equal("refs/heads/idp/93f23689/task/90d5b0a2\n", f.Git.Run(ready.Checkout, "symbolic-ref", "HEAD").Text);
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", GitFixture.Read(f.Git.Open().ReadRef(branch))?.Hex);
        Assert.Equal("", f.Git.Run(ready.Checkout, "status", "--porcelain").Text);
        Assert.Equal(0, f.Read().Blocks.Values.Count(b => !b.Resolved));
        Assert.Equal(2, Moves(f, operation));
        var published = f.Materializer().Publish(f.Lease(T), f.Op(), attempt);
        Assert.Equal("Accepted", published.GetType().Name);
        Assert.Equal("B ready.\n", Assert.IsType<Publication.Accepted>(published).Result.Report);
    }

    [LinuxOrWindowsFact]
    public async Task A_lock_only_restore_leaves_unfinished_retry_path_drift_open()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await SalvageTests.FailedWriter(f);
        var attempt = ready.Execution.Launch.Attempt;
        f.Git.Write("a.txt", "drifted\n", ready.Checkout);
        var early = f.Op();
        Assert.Equal("Preserved", (await f.Materializer().Preserve(f.Lease(T), early, attempt)).GetType().Name);
        var drift = OperationIds.Derive(early, "preserve-drift");
        Assert.Equal(new BlockScope.Checkout(["a.txt"]), f.Read().Blocks[drift].Block.Scope);
        var salvage = Assert.IsType<Salvage.Retained>(await f.Materializer().Salvage(f.Lease(T), f.Op(), attempt));
        var indexPath = GitFixture.Read(f.Git.Open().IndexPath(ready.Checkout));
        Assert.Throws<Crash>(() => f.Materializer(probe: step =>
        {
            if (step != "journal.retry-reset-intent.after") return;
            File.WriteAllText(indexPath + ".lock", "partial\n");
            throw new Crash();
        }).ResetForRetry(f.Lease(T), f.Op(), salvage.Receipt.Plan, f.Op()));
        Assert.Equal("partial\n", File.ReadAllText(indexPath + ".lock"));
        Assert.False(f.Read().Blocks[drift].Resolved);
        var preservation = f.Op();
        Assert.Equal("Preserved", (await f.Materializer().Preserve(f.Lease(T), preservation, attempt)).GetType().Name);
        var whole = f.Op();
        Assert.Equal("Recorded", f.Store.Record(f.Permit, whole, new RunEvent.Blocked(
            new(whole, T, attempt, MaterializationProblem.UncertainOwnership, ready.Execution.Inputs, [], "Checkout identity drift.")
            { Scope = BlockScope.Checkout.Whole })).GetType().Name);
        var shared = f.Op();
        Assert.Equal("Recorded", f.Store.Record(f.Permit, shared, new RunEvent.Blocked(
            new(shared, T, attempt, MaterializationProblem.UncertainOwnership, ready.Execution.Inputs, [], "Stash drift.")
            { Scope = new BlockScope.Refs(["refs/stash"]) })).GetType().Name);
        var preview = Preview(f, ready, preservation);
        var locked = OperationIds.Derive(preservation, "preserve-drift");
        Assert.Equal(new[] { locked }, preview.Repairs);
        Assert.Empty(preview.Rechecks);
        Assert.Equal("Restored", f.Materializer().Restore(f.Lease(T), f.Op(), attempt, preservation, f.Op(), preview.Identity).GetType().Name);
        Assert.False(File.Exists(indexPath + ".lock"));
        Assert.True(f.Read().Blocks[locked].Resolved);
        Assert.False(f.Read().Blocks[whole].Resolved);
        Assert.False(f.Read().Blocks[shared].Resolved);
        Assert.Equal("drifted\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal(1, f.Read().Blocks.Count(block => block.Key == drift && !block.Value.Resolved));
        Assert.Equal(new BlockScope.Checkout(["a.txt"]), f.Read().Blocks[drift].Block.Scope);
    }

    [LinuxOrWindowsFact]
    public async Task A_completed_restore_rerun_after_publication_returns_its_receipt_without_appending()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("result.txt", "done\n", ready.Checkout);
        await f.Close(ready, "done\n");
        var attempt = ready.Execution.Launch.Attempt;
        f.Git.Write("result.txt", "late\n", ready.Checkout);
        var publication = f.Op();
        Assert.Equal("Blocked", f.Materializer().Publish(f.Lease(T), publication, attempt).GetType().Name);
        var preservation = f.Op();
        Assert.Equal("Preserved", (await f.Materializer().Preserve(f.Lease(T), preservation, attempt)).GetType().Name);
        var preview = Preview(f, ready, preservation);
        var operation = f.Op();
        var confirmation = f.Op();
        var restored = Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(T), operation, attempt, preservation, confirmation, preview.Identity));
        var accepted = f.Materializer().Publish(f.Lease(T), publication, attempt);
        Assert.Equal("Accepted", accepted.GetType().Name);
        Assert.Equal("done\n", Assert.IsType<Publication.Accepted>(accepted).Result.Report);
        var sequence = f.Read().Sequence;
        var again = f.Materializer().Restore(f.Lease(T), operation, attempt, preservation, confirmation, preview.Identity);
        Assert.Equal("Restored", again.GetType().Name);
        Assert.Equal(restored, again);
        Assert.Equal(sequence, f.Read().Sequence);
    }

    [LinuxOrWindowsFact]
    public async Task A_restore_rerun_resolves_its_own_block_and_enables_publication()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("result.txt", "done\n", ready.Checkout);
        await f.Close(ready, "done\n");
        var attempt = ready.Execution.Launch.Attempt;
        f.Git.Write("result.txt", "late\n", ready.Checkout);
        var preservation = f.Op();
        Assert.Equal("Preserved", (await f.Materializer().Preserve(f.Lease(T), preservation, attempt)).GetType().Name);
        var preview = Preview(f, ready, preservation);
        var operation = f.Op();
        var confirmation = f.Op();
        Assert.Throws<Crash>(() => f.Materializer(probe: step => { if (step == "journal.restore-plan.after") throw new Crash(); })
            .Restore(f.Lease(T), operation, attempt, preservation, confirmation, preview.Identity));
        f.Git.Write("extra.txt", "extra\n", ready.Checkout);
        Assert.Equal("Blocked", f.Materializer().Restore(f.Lease(T), operation, attempt, preservation, confirmation, preview.Identity).GetType().Name);
        Assert.Equal(2, f.Read().Blocks.Values.Count(b => !b.Resolved && b.Block.Task == T));
        File.Delete(Path.Combine(ready.Checkout, "extra.txt"));
        Assert.Equal("Restored", f.Materializer().Restore(f.Lease(T), operation, attempt, preservation, confirmation, preview.Identity).GetType().Name);
        Assert.Equal("done\n", File.ReadAllText(Path.Combine(ready.Checkout, "result.txt")));
        Assert.Equal(0, f.Read().Blocks.Values.Count(b => !b.Resolved && b.Block.Task == T));
        var published = f.Materializer().Publish(f.Lease(T), f.Op(), attempt);
        Assert.Equal("Accepted", published.GetType().Name);
        Assert.Equal("done\n", Assert.IsType<Publication.Accepted>(published).Result.Report);
    }

    [LinuxOrWindowsFact]
    public async Task A_restore_success_resolves_its_own_registration_refusal()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await f.Close(ready);
        var attempt = ready.Execution.Launch.Attempt;
        f.Git.Write("a.txt", "late\n", ready.Checkout);
        var preservation = f.Op();
        Assert.Equal("Preserved", (await f.Materializer().Preserve(f.Lease(T), preservation, attempt)).GetType().Name);
        var drift = OperationIds.Derive(preservation, "preserve-drift");
        var preview = Preview(f, ready, preservation);
        var operation = f.Op();
        var confirmation = f.Op();
        var reason = f.Git.Run(f.Git.Folder, "worktree", "list", "--porcelain").Text.Split('\n')
            .Single(line => line.StartsWith("locked ", StringComparison.Ordinal))["locked ".Length..];
        Assert.Equal(0, f.Git.Run(f.Git.Folder, "worktree", "unlock", ready.Checkout).ExitCode);
        var refused = Assert.IsType<Restoration.Blocked>(f.Materializer()
            .Restore(f.Lease(T), operation, attempt, preservation, confirmation, preview.Identity));
        Assert.Equal("UncertainOwnership", refused.Block.Problem.ToString());
        Assert.Equal(new BlockScope.Ownership(), refused.Block.Scope);
        var own = f.Read().Blocks.Single(b => b.Value.Block.Operation == OperationIds.Derive(operation, "restore-plan")).Key;
        Assert.False(f.Read().Blocks[own].Resolved);
        Assert.Equal(0, f.Git.Run(f.Git.Folder, "worktree", "lock", "--reason", reason, ready.Checkout).ExitCode);
        var fresh = Preview(f, ready, preservation);
        var result = Assert.IsType<Restoration.Restored>(f.Materializer()
            .Restore(f.Lease(T), operation, attempt, preservation, confirmation, fresh.Identity));
        Assert.Equal(new[] { drift, own }, result.Receipt.Resolved);
        Assert.True(f.Read().Blocks[own].Resolved);
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal(1, Moves(f, operation));
    }

    [LinuxOrWindowsFact]
    public async Task Restore_after_a_registration_refusal_enables_publication()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("result.txt", "done\n", ready.Checkout);
        await f.Close(ready, "done\n");
        var attempt = ready.Execution.Launch.Attempt;
        f.Git.Write("result.txt", "late\n", ready.Checkout);
        var reason = f.Git.Run(f.Git.Folder, "worktree", "list", "--porcelain").Text.Split('\n')
            .Single(line => line.StartsWith("locked ", StringComparison.Ordinal))["locked ".Length..];
        Assert.Equal(0, f.Git.Run(f.Git.Folder, "worktree", "unlock", ready.Checkout).ExitCode);
        var failed = Assert.IsType<Preservation.Blocked>(await f.Materializer().Preserve(f.Lease(T), f.Op(), attempt));
        Assert.Equal("UncertainOwnership", failed.Block.Problem.ToString());
        Assert.Equal(0, f.Git.Run(f.Git.Folder, "worktree", "lock", "--reason", reason, ready.Checkout).ExitCode);
        var preservation = f.Op();
        Assert.Equal("Preserved", (await f.Materializer().Preserve(f.Lease(T), preservation, attempt)).GetType().Name);
        Assert.Equal("Blocked", f.Materializer().Publish(f.Lease(T), f.Op(), attempt).GetType().Name);
        Assert.Equal(2, f.Read().Blocks.Values.Count(b => !b.Resolved && b.Block.Task == T));
        var preview = Preview(f, ready, preservation);
        Assert.Equal("Restored", f.Materializer().Restore(f.Lease(T), f.Op(), attempt, preservation, f.Op(), preview.Identity).GetType().Name);
        Assert.Equal("done\n", File.ReadAllText(Path.Combine(ready.Checkout, "result.txt")));
        Assert.Equal(0, f.Read().Blocks.Values.Count(b => !b.Resolved && b.Block.Task == T));
        var published = f.Materializer().Publish(f.Lease(T), f.Op(), attempt);
        Assert.Equal("Accepted", published.GetType().Name);
        Assert.Equal("done\n", Assert.IsType<Publication.Accepted>(published).Result.Report);
    }

    [LinuxOrWindowsFact]
    public async Task Restore_applies_the_repository_line_ending_conversion()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await f.Close(ready);
        var attempt = ready.Execution.Launch.Attempt;
        Assert.Equal(0, f.Git.Run(f.Git.Folder, "config", "core.autocrlf", "true").ExitCode);
        File.Delete(Path.Combine(ready.Checkout, "a.txt"));
        Assert.Equal(0, f.Git.Run(ready.Checkout, "checkout", "--", "a.txt").ExitCode);
        Assert.Equal("A\r\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal("", f.Git.Run(ready.Checkout, "status", "--porcelain").Text);
        File.WriteAllText(Path.Combine(ready.Checkout, "a.txt"), "late\r\n");
        Assert.Equal(" M a.txt\n", f.Git.Run(ready.Checkout, "status", "--porcelain").Text);
        var preservation = f.Op();
        Assert.Equal("Preserved", (await f.Materializer().Preserve(f.Lease(T), preservation, attempt)).GetType().Name);
        var preview = Preview(f, ready, preservation);
        Assert.Equal(new[] { "a.txt" }, preview.Paths.Select(p => p.Path));
        var outcome = f.Materializer().Restore(f.Lease(T), f.Op(), attempt, preservation, f.Op(), preview.Identity);
        Assert.Equal("Restored", outcome.GetType().Name);
        Assert.Equal("A\r\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal("", f.Git.Run(ready.Checkout, "status", "--porcelain").Text);
    }

    [LinuxOrWindowsFact]
    public async Task Restore_after_a_skip_worktree_refusal_enables_publication()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("result.txt", "done\n", ready.Checkout);
        await f.Close(ready, "done\n");
        var attempt = ready.Execution.Launch.Attempt;
        f.Git.Write("result.txt", "late\n", ready.Checkout);
        Assert.Equal(0, f.Git.Run(ready.Checkout, "update-index", "--skip-worktree", "root.txt").ExitCode);
        var refused = Assert.IsType<Preservation.Blocked>(await f.Materializer().Preserve(f.Lease(T), f.Op(), attempt));
        Assert.Equal("DirtyWorktree", refused.Block.Problem.ToString());
        Assert.Equal(0, f.Git.Run(ready.Checkout, "update-index", "--no-skip-worktree", "root.txt").ExitCode);
        var preservation = f.Op();
        Assert.Equal("Preserved", (await f.Materializer().Preserve(f.Lease(T), preservation, attempt)).GetType().Name);
        Assert.Equal("Blocked", f.Materializer().Publish(f.Lease(T), f.Op(), attempt).GetType().Name);
        Assert.Equal(2, f.Read().Blocks.Values.Count(b => !b.Resolved && b.Block.Task == T));
        var preview = Preview(f, ready, preservation);
        Assert.Equal("Restored", f.Materializer().Restore(f.Lease(T), f.Op(), attempt, preservation, f.Op(), preview.Identity).GetType().Name);
        Assert.Equal("done\n", File.ReadAllText(Path.Combine(ready.Checkout, "result.txt")));
        Assert.Equal(0, f.Read().Blocks.Values.Count(b => !b.Resolved && b.Block.Task == T));
        var published = f.Materializer().Publish(f.Lease(T), f.Op(), attempt);
        Assert.Equal("Accepted", published.GetType().Name);
        Assert.Equal("done\n", Assert.IsType<Publication.Accepted>(published).Result.Report);
    }

    [LinuxOrWindowsFact]
    public async Task Preserve_and_restore_an_accepted_baseline()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("result.txt", "done\n", ready.Checkout);
        await f.Close(ready, "done\n");
        Assert.Equal("done\n", Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt)).Result.Report);
        f.Git.Write("result.txt", "late\n", ready.Checkout);
        var preservation = f.Op();
        var preserved = Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, ready.Execution.Launch.Attempt));
        var block = Assert.Single(f.Read().Blocks.Values, b => !b.Resolved).Block;
        Assert.Equal("DirtyWorktree", block.Problem.ToString());
        Assert.Equal("The checkout differs from its recorded baseline.", block.Detail);
        Assert.Equal(new BlockScope.Checkout(["result.txt"]), block.Scope);
        Assert.Equal("late\n", f.Git.Git("show", preserved.Commit.Hex + ":result.txt"));
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, f.Op(), new RunEvent.Blocked(
            new(f.Op(), T, ready.Execution.Launch.Attempt, MaterializationProblem.DirtyWorktree, ready.Execution.Inputs, [], "Legacy block.")
            { Scope = BlockScope.Unrecorded.Value })));
        var readSequence = f.Read().Sequence;
        var refsBefore = f.Git.Git("show-ref");
        var preview = Preview(f, ready, preservation);
        Assert.Equal(readSequence, f.Read().Sequence);
        Assert.Equal(refsBefore, f.Git.Git("show-ref"));
        Assert.Equal(new[] { "result.txt" }, preview.Paths.Select(p => p.Path));
        var operation = f.Op();
        var confirmation = f.Op();
        var restored = Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(T), operation, ready.Execution.Launch.Attempt,
            preservation, confirmation, preview.Identity));
        Assert.Equal("done\n", File.ReadAllText(Path.Combine(ready.Checkout, "result.txt")));
        Assert.Single(f.Read().Results);
        Assert.Equal(0, f.Read().Blocks.Values.Count(b => !b.Resolved && b.Block.Scope is not BlockScope.Unrecorded));
        Assert.Equal("Legacy block.", Assert.Single(f.Read().Blocks.Values, b => !b.Resolved).Block.Detail);
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(GitFixture.Read(f.Git.Open().IndexPath(ready.Checkout)))!, "idevelop-restore")));
        Assert.Equal(1, Moves(f, operation));
        Assert.Single(f.Read().Restorations);
        var sequence = f.Read().Sequence;
        Assert.Equal(restored, f.Materializer().Restore(f.Lease(T), operation, ready.Execution.Launch.Attempt, preservation, confirmation, preview.Identity));
        Assert.Equal(sequence, f.Read().Sequence);
    }

    [LinuxOrWindowsFact]
    public async Task Restore_a_waiting_baseline()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T) with { Conversation = ConversationMode.Chat }));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("result.txt", "staged\n", ready.Checkout);
        Assert.Equal(0, f.Git.Run(ready.Checkout, "add", "result.txt").ExitCode);
        f.Git.Write("result.txt", "done\n", ready.Checkout);
        var checkpoint = f.ObserveAndLog(ready, "done\n");
        var settled = Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(T), f.Op(), ready.Execution.Launch, checkpoint));
        Assert.IsType<CaptureDisposition.Matched>(settled.Disposition);
        var capture = f.Read().Captures[settled.Capture][0];
        f.Git.Write("result.txt", "late\n", ready.Checkout);
        Assert.Equal(0, f.Git.Run(ready.Checkout, "add", "result.txt").ExitCode);
        var preservation = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, ready.Execution.Launch.Attempt));
        var preview = Preview(f, ready, preservation);
        var operation = f.Op();
        Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(T), operation, ready.Execution.Launch.Attempt, preservation, f.Op(), preview.Identity));
        Assert.Equal("done\n", File.ReadAllText(Path.Combine(ready.Checkout, "result.txt")));
        Assert.Equal("staged\n", f.Git.Run(ready.Checkout, "show", ":result.txt").Text);
        Assert.Equal(capture.Index!.Content, GitFixture.Read(f.Git.Open().IndexDigest(ready.Checkout)));
        Assert.Empty(f.Read().Results);
        Assert.Single(f.Read().Restorations);
        Assert.Equal(2, Moves(f, operation));
    }

    [LinuxOrWindowsFact]
    public async Task Restore_resumes_a_pending_publication_from_its_original_capture()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("result.txt", "done\n", ready.Checkout);
        await f.Close(ready, "done\n");
        f.Git.Write("result.txt", "late\n", ready.Checkout);
        var publication = f.Op();
        var drift = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(f.Lease(T), publication, ready.Execution.Launch.Attempt));
        Assert.Equal("DirtyWorktree", drift.Block.Problem.ToString());
        Assert.Equal(new BlockScope.Checkout(["result.txt"]), drift.Block.Scope);
        var preservation = f.Op();
        var preserved = Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, ready.Execution.Launch.Attempt));
        var preview = Preview(f, ready, preservation);
        Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt, preservation, f.Op(), preview.Identity));
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), publication, ready.Execution.Launch.Attempt));
        Assert.Equal("done\n", accepted.Result.Report);
        Assert.Equal("done\n", File.ReadAllText(Path.Combine(ready.Checkout, "result.txt")));
        Assert.Equal("late\n", f.Git.Git("show", preserved.Commit.Hex + ":result.txt"));
        Assert.Single(f.Read().Results);
        Assert.Equal(0, f.Read().Blocks.Values.Count(b => !b.Resolved));
    }

    [LinuxOrWindowsFact]
    public async Task Restore_removes_exactly_the_preserved_extras()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await f.Close(ready);
        f.Git.Write("new.txt", "new\n", ready.Checkout);
        File.AppendAllText(f.Git.PathOf(".git/info/exclude"), "build/\n");
        f.Git.Write("build/out.bin", "ignored\n", ready.Checkout);
        var outbox = RunLayout.Outbox(ready.Execution.Launch.Attempt) + "/report.md";
        f.Git.Write(outbox, "B ready.\n", ready.Checkout);
        var preservation = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, ready.Execution.Launch.Attempt));
        var preview = Preview(f, ready, preservation);
        Assert.Equal(new[] { "new.txt" }, preview.Paths.Select(p => p.Path));
        Assert.Null(Assert.Single(preview.Paths).To);
        var operation = f.Op();
        Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(T), operation, ready.Execution.Launch.Attempt, preservation, f.Op(), preview.Identity));
        Assert.False(File.Exists(Path.Combine(ready.Checkout, "new.txt")));
        Assert.Equal("ignored\n", File.ReadAllText(Path.Combine(ready.Checkout, "build/out.bin")));
        Assert.Equal("B ready.\n", File.ReadAllText(Path.Combine(ready.Checkout, outbox)));
        Assert.Equal(1, Moves(f, operation));
    }

    [LinuxOrWindowsTheory]
    [InlineData("run.sh", "mode", "an executable file", "a file")]
    [InlineData("link", "link", "a symbolic link", "a file")]
    [InlineData("docs", "folder", "a folder", "a file")]
    [InlineData("tool.sh", "executable", "an executable file", "an executable file")]
    public async Task Restore_refuses_type_and_mode_changes_before_any_move(string path, string change, string now, string baseline)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)), configureBase: git =>
        {
            git.Write(path, "before\n");
            if (change == "executable") MakeExecutable(git, git.Folder, path);
            return git.Commit("restore kind");
        });
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await f.Close(ready);
        if (change == "mode") MakeExecutable(f.Git, ready.Checkout, path);
        else if (change == "link")
        {
            File.Delete(Path.Combine(ready.Checkout, path));
            File.CreateSymbolicLink(Path.Combine(ready.Checkout, path), "a.txt");
        }
        else if (change == "folder")
        {
            File.Delete(Path.Combine(ready.Checkout, path));
            f.Git.Write(path + "/a.txt", "nested\n", ready.Checkout);
        }
        else f.Git.Write(path, "changed\n", ready.Checkout);
        f.Git.Write("a.txt", "late\n", ready.Checkout);
        var preservation = f.Op();
        var preserved = Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, ready.Execution.Launch.Attempt));
        var read = Assert.IsType<RestorePreviewRead.Refused>(f.Materializer().PreviewRestore(f.Lease(T), ready.Execution.Launch.Attempt, preservation));
        var detail = $"Restore changes only regular, non-executable files. {path} is {now} now and {baseline} in the baseline. Change it outside iDevelop, then preserve again.";
        Assert.Equal("DirtyWorktree", read.Problem.ToString());
        Assert.Equal(detail, read.Detail);
        var operation = f.Op();
        var blocked = Assert.IsType<Restoration.Blocked>(f.Materializer().Restore(f.Lease(T), operation, ready.Execution.Launch.Attempt,
            preservation, f.Op(), Revision.Hash("refused preview")));
        Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
        Assert.Equal(detail, blocked.Block.Detail);
        Assert.Equal(0, Moves(f, operation));
        Assert.Equal("late\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal(preserved.Commit, GitFixture.Read(f.Git.Open().ReadRef(preserved.Receipt.Ref)));
        if (change == "folder") Assert.Equal("nested\n", File.ReadAllText(Path.Combine(ready.Checkout, "docs/a.txt")));
        else if (change == "link") Assert.Equal("a.txt", new FileInfo(Path.Combine(ready.Checkout, path)).LinkTarget);
        else Assert.Equal(change == "executable" ? "changed\n" : "before\n", File.ReadAllText(Path.Combine(ready.Checkout, path)));
        if (change is "mode" or "executable")
        {
            f.Git.Write(path, "before\n", ready.Checkout);
            if (change == "mode")
            {
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(Path.Combine(ready.Checkout, path), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                Assert.Equal(0, f.Git.Run(ready.Checkout, "update-index", "--chmod=-x", path).ExitCode);
            }
        }
        else
        {
            if (change == "folder") Directory.Delete(Path.Combine(ready.Checkout, path), recursive: true);
            else File.Delete(Path.Combine(ready.Checkout, path));
            f.Git.Write(path, "before\n", ready.Checkout);
        }
        var controlPreservation = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), controlPreservation, ready.Execution.Launch.Attempt));
        var preview = Preview(f, ready, controlPreservation);
        var control = f.Op();
        Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(T), control, ready.Execution.Launch.Attempt, controlPreservation, f.Op(), preview.Identity));
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal(1, Moves(f, control));
    }

    [LinuxOrWindowsTheory]
    [InlineData("git.restore-file-a.txt.before")]
    [InlineData("restore.file.a.txt.written")]
    public async Task A_file_added_after_preservation_blocks_restore(string point)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await f.Close(ready);
        f.Git.Write("a.txt", "late\n", ready.Checkout);
        var preservation = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, ready.Execution.Launch.Attempt));
        var preview = Preview(f, ready, preservation);
        var branch = GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch));
        f.Git.Write("extra.txt", "extra\n", ready.Checkout);
        var operation = f.Op();
        var blocked = Assert.IsType<Restoration.Blocked>(f.Materializer().Restore(f.Lease(T), operation, ready.Execution.Launch.Attempt, preservation, f.Op(), preview.Identity));
        Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
        Assert.Equal("The checkout changed after it was preserved. Preserve it again.", blocked.Block.Detail);
        Assert.Equal(new BlockScope.Checkout(["extra.txt"]), blocked.Block.Scope);
        Assert.Equal(0, Moves(f, operation));
        Assert.Equal("extra\n", File.ReadAllText(Path.Combine(ready.Checkout, "extra.txt")));
        Assert.Equal(branch, GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch)));
        var replacement = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), replacement, ready.Execution.Launch.Attempt));
        var controlPreview = Preview(f, ready, replacement);
        var control = f.Op();
        var raced = Assert.IsType<Restoration.Blocked>(f.Materializer(probe: step =>
        {
            if (step == point) f.Git.Write("a.txt", "raced\n", ready.Checkout);
        }).Restore(f.Lease(T), control, ready.Execution.Launch.Attempt, replacement, f.Op(), controlPreview.Identity));
        Assert.Equal("DirtyWorktree", raced.Block.Problem.ToString());
        Assert.Equal(new BlockScope.Checkout(["a.txt"]), raced.Block.Scope);
        Assert.Equal(0, Moves(f, control));
        Assert.Equal("raced\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        var finalPreservation = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), finalPreservation, ready.Execution.Launch.Attempt));
        var finalPreview = Preview(f, ready, finalPreservation);
        var finalRestore = f.Op();
        Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(T), finalRestore, ready.Execution.Launch.Attempt,
            finalPreservation, f.Op(), finalPreview.Identity));
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.False(File.Exists(Path.Combine(ready.Checkout, "extra.txt")));
        Assert.Equal(1, Moves(f, finalRestore));
    }

    [LinuxOrWindowsFact]
    public async Task A_stale_preview_is_refused()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await f.Close(ready);
        f.Git.Write("a.txt", "late\n", ready.Checkout);
        var preservation = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, ready.Execution.Launch.Attempt));
        var preview = Preview(f, ready, preservation);
        f.Git.Git("update-ref", "refs/stash", f.A.Hex);
        var operation = f.Op();
        var rejected = Assert.IsType<Restoration.Rejected>(f.Materializer().Restore(f.Lease(T), operation, ready.Execution.Launch.Attempt, preservation, f.Op(), preview.Identity));
        Assert.Equal("EvidenceMismatch", rejected.Reason.Problem.ToString());
        Assert.Equal(0, Moves(f, operation));
        Assert.Equal("late\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        var fresh = Preview(f, ready, preservation);
        var control = f.Op();
        Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(T), control, ready.Execution.Launch.Attempt, preservation, f.Op(), fresh.Identity));
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal(1, Moves(f, control));
        Assert.Equal(f.A, GitFixture.Read(f.Git.Open().ReadRef("refs/stash")));
    }

    [Fact]
    public async Task RestoreRef_intents_are_bound_to_the_restoration_plan()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await f.Close(ready);
        f.Git.Write("foreign.txt", "foreign\n");
        var foreign = f.Git.Commit("foreign");
        f.Git.Git("update-ref", ready.Execution.Location.Owner.Branch, foreign.Hex);
        var preservation = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, ready.Execution.Launch.Attempt));
        var preview = Preview(f, ready, preservation);
        var planId = OperationIds.Derive(f.Op(), "restore-plan");
        var plan = new MaterializationPlan.Restoration(T, ready.Execution.Launch.Attempt, preservation, preview.Current, preview.To,
            preview.Paths, preview.Repairs, preview.Rechecks, f.Op(), preview.Identity);
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, planId, new RunEvent.Planned(plan)));
        var wrong = new RefChange(ready.Execution.Location.Owner.Branch, f.A, f.A);
        var rejected = Assert.IsType<RunDecision.Rejected>(f.Store.Record(f.Permit, f.Op(), new RunEvent.GitIntended(planId, new GitMutation.RestoreRef(wrong))));
        Assert.Equal("InvalidData", rejected.Reason.Problem.ToString());
        var intent = f.Op();
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, intent, new RunEvent.GitIntended(planId,
            new GitMutation.RestoreRef(new(ready.Execution.Location.Owner.Branch, foreign, f.A)))));
        Assert.False(RefOwnership.Accepts(f.Read(), f.Git.Open(), ready.Execution.Location.Owner.Branch, foreign));
        Assert.True(RefOwnership.Accepts(f.Read(), f.Git.Open(), ready.Execution.Location.Owner.Branch, f.A));
        Assert.Equal(foreign, GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch)));
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, f.Op(), new RunEvent.GitObserved(intent, new(false, f.A.Hex))));
        Assert.True(RefOwnership.Accepts(f.Read(), f.Git.Open(), ready.Execution.Location.Owner.Branch, f.A));
    }

    [LinuxOrWindowsTheory]
    [InlineData("a", "journal.branch-intent.after", "DirtyWorktree", 0, 0)]
    [InlineData("b", "git.branch.after", "DirtyWorktree", 0, 0)]
    [InlineData("c", "journal.index-intent.after", "DirtyWorktree", 0, 1)]
    [InlineData("d", "journal.branch-intent.after", "UncertainOwnership", 1, 0)]
    public async Task Restore_clears_drift_around_a_pending_publication_move(string row, string point, string problem,
        int refMoves, int indexMoves)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await DoneWriter(f);
        var attempt = ready.Execution.Launch.Attempt;
        var publication = f.Op();
        Assert.Throws<Crash>(() => f.Materializer(probe: step => { if (step == point) throw new Crash(); })
            .Publish(f.Lease(T), publication, attempt));
        CommitId? foreign = null;
        if (row == "d") foreign = MoveToForeign(f, ready);
        var branchBefore = GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch));
        f.Git.Write("result.txt", "late\n", ready.Checkout);
        if (row == "c") Assert.Equal(0, f.Git.Run(ready.Checkout, "add", "result.txt").ExitCode);
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(f.Lease(T), f.Op(), attempt));
        Assert.Equal(problem, blocked.Block.Problem.ToString());
        var blockId = Assert.Single(f.Read().Blocks).Key;
        var preservation = f.Op();
        var preserved = Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, attempt));
        var preview = Preview(f, ready, preservation);
        var operation = f.Op();
        Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(T), operation, attempt, preservation, f.Op(), preview.Identity));
        Assert.True(f.Read().Blocks[blockId].Resolved);
        Assert.Equal(refMoves, Observations<GitMutation.RestoreRef>(f, operation).Count());
        Assert.Equal(indexMoves, Observations<GitMutation.AlignIndex>(f, operation).Count());
        Assert.Equal(row == "d" ? f.A : branchBefore, GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch)));
        Assert.Equal("done\n", File.ReadAllText(Path.Combine(ready.Checkout, "result.txt")));
        Assert.Equal("late\n", f.Git.Git("show", preserved.Commit.Hex + ":result.txt"));
        if (foreign is { } tip)
        {
            Assert.Equal("foreign\n", f.Git.Git("show", tip.Hex + ":foreign.txt"));
            Assert.Equal(0, f.Git.Run(f.Git.Folder, "merge-base", "--is-ancestor", tip.Hex, preserved.Receipt.Ref).ExitCode);
        }
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), f.Op(), attempt));
        Assert.Equal("done\n", accepted.Result.Report);
        Assert.Equal("done\n", File.ReadAllText(Path.Combine(ready.Checkout, "result.txt")));
        Assert.Single(f.Read().Results);
        Assert.Equal(0, f.Read().Blocks.Values.Count(b => !b.Resolved));
    }

    [LinuxOrWindowsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_with_a_partial_reset_is_restored_as_one_component(bool withLateWrite)
    {
        var reviewer = new TaskDefinition(U, new Blueprint(new("example.review", 1), "Review",
            new WorkSpec.Review(PromptTemplate.Parse("Review"), PromptTemplate.Parse("Fix")), [],
            new(Task().Execution, ConversationMode.Autonomous))) { Title = "Review" };
        using var f = new PreparationFixture(Connect(FixtureWorkflow(Writer(T), reviewer), T, U));
        await f.Publish(T, f.A);
        var review = Assert.IsType<Preparation.Ready>(await f.Prepare(U, prompt: "Review."));
        var checkpoint = f.ObserveAndLog(review, "Changes requested.\n");
        Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(U), f.Op(), review.Execution.Launch, checkpoint));
        var fix = Assert.IsType<Preparation.Ready>(await f.Prepare(T,
            cause: new AttemptCause.ReviewFix(new(U, review.Execution.Launch.Attempt, 1, 0)), prompt: "Fix."));
        f.Git.Write("a.txt", "Fixed\n", fix.Checkout);
        await f.Close(fix, "Repaired.\n");
        var repaired = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), f.Op(), fix.Execution.Launch.Attempt));
        var newBase = Assert.IsType<CodeOutput.Produced>(repaired.Result.Code).Code.Commit;
        var oldBase = review.Execution.Location.AttemptBase;
        var operation = f.Op();
        var key = new LaunchKey(review.Execution.Launch.Attempt, 2);
        await Assert.ThrowsAsync<Crash>(async () => await f.Materializer(probe: step =>
        {
            if (step == "journal.refresh-reset-intent.after") throw new Crash();
        }).PrepareTurn(f.Lease(U), operation, key, "Review the fix."));
        Assert.Equal(0, f.Git.Run(review.Checkout, "checkout", newBase.Hex, "--", ".").ExitCode);
        Assert.Equal(0, f.Git.Run(review.Checkout, "read-tree", oldBase.Hex).ExitCode);
        if (withLateWrite) f.Git.Write("root.txt", "late\n", review.Checkout);
        var preservation = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(U), preservation, key.Attempt));
        var preview = Preview(f, review, preservation);
        Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(U), f.Op(), key.Attempt, preservation, f.Op(), preview.Identity));
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(review.Checkout, "a.txt")));
        Assert.Equal("root\n", File.ReadAllText(Path.Combine(review.Checkout, "root.txt")));
        Assert.Equal(GitFixture.Read(f.Git.Open().ReadCommit(oldBase)).Tree, GitFixture.Read(f.Git.Open().Capture(review.Checkout)).Tree);
        Assert.Equal(GitFixture.Read(f.Git.Open().ReadCommit(oldBase)).Tree, GitFixture.Read(f.Git.Open().ReadIndexTree(review.Checkout)));
        var refreshed = Assert.IsType<Preparation.Ready>(await f.Materializer().PrepareTurn(f.Lease(U), operation, key, "Review the fix."));
        Assert.Equal(newBase, refreshed.Execution.Location.AttemptBase);
        Assert.Equal("Fixed\n", File.ReadAllText(Path.Combine(refreshed.Checkout, "a.txt")));
        Assert.Equal(GitFixture.Read(f.Git.Open().ReadCommit(newBase)).Tree, GitFixture.Read(f.Git.Open().ReadIndexTree(refreshed.Checkout)));
        Assert.Single(f.Read().Restorations);
    }

    [Fact]
    public async Task A_publication_CAS_after_a_restoration_is_adopted()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await DoneWriter(f);
        var attempt = ready.Execution.Launch.Attempt;
        var publication = f.Op();
        Assert.Throws<Crash>(() => f.Materializer(probe: step => { if (step == "journal.branch-intent.after") throw new Crash(); })
            .Publish(f.Lease(T), publication, attempt));
        var candidate = Assert.Single(f.Read().Plans.Values.OfType<MaterializationPlan.Publication>()).Commit;
        var foreign = MoveToForeign(f, ready);
        var preservation = f.Op();
        var preserved = Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, attempt));
        var preview = Preview(f, ready, preservation);
        Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(T), f.Op(), attempt, preservation, f.Op(), preview.Identity));
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch))?.Hex);
        Assert.Equal(0, f.Git.Run(f.Git.Folder, "merge-base", "--is-ancestor", foreign.Hex, preserved.Receipt.Ref).ExitCode);
        Assert.Throws<Crash>(() => f.Materializer(probe: step => { if (step == "git.branch.after") throw new Crash(); })
            .Publish(f.Lease(T), publication, attempt));
        Assert.Equal(candidate, GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch)));
        Assert.Empty(f.Read().Results);
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), publication, attempt));
        Assert.Equal("done\n", accepted.Result.Report);
        Assert.Equal("done\n", File.ReadAllText(Path.Combine(ready.Checkout, "result.txt")));
        Assert.Equal(candidate, GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch)));
        Assert.True(f.Read().GitObservations[OperationIds.Derive(publication, "branch-intent")].Adopted);
        Assert.Single(f.Read().Results);
    }

    [Fact]
    public Task An_unfinished_retry_without_a_lock_refuses_restore() => LockOnlyRestore(false);

    [LinuxOrWindowsFact]
    public Task A_lock_only_restore_unblocks_an_unfinished_retry() => LockOnlyRestore(true);

    private static async Task LockOnlyRestore(bool withLock)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await SalvageTests.FailedWriter(f);
        var attempt = ready.Execution.Launch.Attempt;
        var salvage = Assert.IsType<Salvage.Retained>(await f.Materializer().Salvage(f.Lease(T), f.Op(), attempt));
        var reset = f.Op();
        var indexPath = GitFixture.Read(f.Git.Open().IndexPath(ready.Checkout));
        Assert.Throws<Crash>(() => f.Materializer(probe: step =>
        {
            if (step != "journal.retry-reset-intent.after") return;
            if (withLock) File.WriteAllText(indexPath + ".lock", "partial\n");
            throw new Crash();
        }).ResetForRetry(f.Lease(T), reset, salvage.Receipt.Plan, f.Op()));
        var preservation = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, attempt));
        var operation = f.Op();
        if (!withLock)
        {
            var detail = $"Retry reset {OperationIds.Derive(reset, "retry-plan").Value:D} has an unfinished Git step on this checkout. Run it again, or salvage and retry.";
            var refusal = Assert.IsType<RestorePreviewRead.Refused>(f.Materializer().PreviewRestore(f.Lease(T), attempt, preservation));
            Assert.Equal("UncertainOwnership", refusal.Problem.ToString());
            Assert.Equal(detail, refusal.Detail);
            var blocked = Assert.IsType<Restoration.Blocked>(f.Materializer().Restore(f.Lease(T), operation, attempt,
                preservation, f.Op(), Revision.Hash("unfinished preview")));
            Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
            Assert.Equal(detail, blocked.Block.Detail);
            Assert.Equal(0, Moves(f, operation));
        }
        else
        {
            var preview = Preview(f, ready, preservation);
            Assert.Equal("partial\n", File.ReadAllText(indexPath + ".lock"));
            Assert.Equal(preview.Current with { IndexLock = null }, preview.To);
            Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(T), operation, attempt, preservation, f.Op(), preview.Identity));
            Assert.Single(Observations<GitMutation.RemoveIndexLock>(f, operation));
            Assert.Equal(1, Moves(f, operation));
            Assert.False(File.Exists(indexPath + ".lock"));
        }
        Assert.Equal("modified\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal("unfinished\n", File.ReadAllText(Path.Combine(ready.Checkout, "new.txt")));
        var again = Assert.IsType<Salvage.Retained>(await f.Materializer().Salvage(f.Lease(T), f.Op(), attempt));
        Assert.IsType<RetryReset.Reset>(f.Materializer().ResetForRetry(f.Lease(T), f.Op(), again.Receipt.Plan, f.Op()));
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.False(File.Exists(Path.Combine(ready.Checkout, "new.txt")));
        Assert.Equal("cache\n", File.ReadAllText(Path.Combine(ready.Checkout, "cache.txt")));
        Assert.Equal("", f.Git.Run(ready.Checkout, "status", "--porcelain").Text);
    }

    [LinuxOrWindowsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restore_recovers_from_its_own_index_lock(bool publicationLock)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await DoneWriter(f);
        var attempt = ready.Execution.Launch.Attempt;
        var indexPath = GitFixture.Read(f.Git.Open().IndexPath(ready.Checkout));
        var capture = f.Read().Captures[f.Read().Settlements[ready.Execution.Launch]][0];
        var operation = f.Op();
        var confirmation = f.Op();
        OperationId preservation;
        RestorePreview? firstPreview = null;
        if (publicationLock)
        {
            Assert.Throws<Crash>(() => f.Materializer(probe: step =>
            {
                if (step != "git.align-index.before") return;
                File.WriteAllText(indexPath + ".lock", "partial\n");
                throw new Crash();
            }).Publish(f.Lease(T), operation, attempt));
            preservation = f.Op();
        }
        else
        {
            f.Git.Write("result.txt", "late\n", ready.Checkout);
            Assert.Equal(0, f.Git.Run(ready.Checkout, "add", "result.txt").ExitCode);
            preservation = f.Op();
            Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, attempt));
            firstPreview = Preview(f, ready, preservation);
            Assert.Throws<Crash>(() => f.Materializer(probe: step =>
            {
                if (step != "git.restore-index.before") return;
                File.WriteAllText(indexPath + ".lock", "partial\n");
                throw new Crash();
            }).Restore(f.Lease(T), operation, attempt, preservation, confirmation, firstPreview.Identity));
            var before = Moves(f, operation);
            var blocked = Assert.IsType<Restoration.Blocked>(f.Materializer().Restore(f.Lease(T), operation, attempt,
                preservation, confirmation, firstPreview.Identity));
            Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
            Assert.Equal(new BlockScope.Checkout([], IndexLock: true), blocked.Block.Scope);
            Assert.Equal(before, Moves(f, operation));
            Assert.Equal(1, before);
            preservation = f.Op();
        }
        Assert.Equal("partial\n", File.ReadAllText(indexPath + ".lock"));
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, attempt));
        var preview = Preview(f, ready, preservation);
        var replacement = f.Op();
        var planId = OperationIds.Derive(operation, "restore-plan");
        if (!publicationLock)
        {
            Assert.Equal(planId, preview.Supersedes);
            var candidate = new MaterializationPlan.Restoration(T, attempt, preservation, preview.Current, preview.To,
                preview.Paths, preview.Repairs, preview.Rechecks, f.Op(), preview.Identity) { Supersedes = planId };
            Assert.Equal("InvalidData", Assert.IsType<RunDecision.Rejected>(f.Store.Record(f.Permit, f.Op(),
                new RunEvent.Planned(candidate with { Supersedes = null }))).Reason.Problem.ToString());
            Assert.Equal("InvalidData", Assert.IsType<RunDecision.Rejected>(f.Store.Record(f.Permit, f.Op(),
                new RunEvent.Planned(candidate with { Supersedes = f.Op() }))).Reason.Problem.ToString());
            var old = Assert.IsType<MaterializationPlan.Restoration>(f.Read().Plans[planId]);
            Assert.Equal("InvalidData", Assert.IsType<RunDecision.Rejected>(f.Store.Record(f.Permit, f.Op(),
                new RunEvent.Planned(old with { Supersedes = planId }))).Reason.Problem.ToString());
        }
        Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(T), replacement, attempt, preservation, f.Op(), preview.Identity));
        Assert.Single(Observations<GitMutation.RemoveIndexLock>(f, replacement));
        Assert.Equal(publicationLock ? 0 : 1, Observations<GitMutation.AlignIndex>(f, replacement).Count());
        Assert.Equal(capture.Index!.Content, GitFixture.Read(f.Git.Open().IndexDigest(ready.Checkout)));
        Assert.False(File.Exists(indexPath + ".lock"));
        Assert.Single(f.Read().Restorations);
        if (!publicationLock)
        {
            Assert.Equal(planId, Assert.IsType<MaterializationPlan.Restoration>(f.Read().Plans[OperationIds.Derive(replacement, "restore-plan")]).Supersedes);
            var before = Moves(f, operation);
            Assert.Equal("ReplacementConflict", Assert.IsType<Restoration.Rejected>(f.Materializer().Restore(f.Lease(T), operation,
                attempt, Assert.IsType<MaterializationPlan.Restoration>(f.Read().Plans[planId]).Preservation, confirmation, firstPreview!.Identity)).Reason.Problem.ToString());
            Assert.Equal(before, Moves(f, operation));
            var intent = Assert.Single(f.Read().GitIntents, p => p.Value.Plan == planId && p.Value.Mutation is GitMutation.AlignIndex);
            Assert.Equal("ReplacementConflict", Assert.IsType<RunDecision.Rejected>(f.Store.Record(f.Permit, f.Op(),
                new RunEvent.GitObserved(intent.Key, new(false, capture.IndexTree!.Value.Hex)))).Reason.Problem.ToString());
            Assert.Equal("ReplacementConflict", Assert.IsType<RunDecision.Rejected>(f.Store.Record(f.Permit, f.Op(),
                new RunEvent.GitIntended(planId, intent.Value.Mutation))).Reason.Problem.ToString());
        }
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), publicationLock ? operation : f.Op(), attempt));
        Assert.Equal("done\n", accepted.Result.Report);
        Assert.Equal("done\n", File.ReadAllText(Path.Combine(ready.Checkout, "result.txt")));
        Assert.Single(f.Read().Results);
    }

    [Fact]
    public async Task Restore_of_an_unexplained_tip_never_explains_it_to_others()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(C)));
        var writer = await DoneWriter(f);
        var sibling = Assert.IsType<Preparation.Ready>(await f.Prepare(C));
        f.Git.Write("c.txt", "C\n", sibling.Checkout);
        await f.Close(sibling, "C ready.\n");
        var foreign = MoveToForeign(f, writer);
        var preservation = f.Op();
        var preserved = Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, writer.Execution.Launch.Attempt));
        var preview = Preview(f, writer, preservation);
        var operation = f.Op();
        var confirmation = f.Op();
        Assert.Throws<Crash>(() => f.Materializer(probe: step => { if (step == "journal.restore-branch-intent.after") throw new Crash(); })
            .Restore(f.Lease(T), operation, writer.Execution.Launch.Attempt, preservation, confirmation, preview.Identity));
        f.ReleaseControl();
        var publication = f.Op();
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(f.Lease(C), publication, sibling.Execution.Launch.Attempt));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Equal(new BlockScope.Refs(["refs/heads/idp/93f23689/task/90d5b0a2"]), blocked.Block.Scope);
        Assert.Contains(writer.Execution.Location.Owner.Branch, blocked.Block.Detail);
        Assert.Equal(foreign, GitFixture.Read(f.Git.Open().ReadRef(writer.Execution.Location.Owner.Branch)));
        Assert.Equal(0, f.Read().Results.Count(r => r.Task == C));
        Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(T), operation, writer.Execution.Launch.Attempt,
            preservation, confirmation, preview.Identity));
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", GitFixture.Read(f.Git.Open().ReadRef(writer.Execution.Location.Owner.Branch))?.Hex);
        Assert.Equal("foreign\n", f.Git.Git("show", foreign.Hex + ":foreign.txt"));
        Assert.Equal(0, f.Git.Run(f.Git.Folder, "merge-base", "--is-ancestor", foreign.Hex, preserved.Receipt.Ref).ExitCode);
        var siblingPreservation = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(C), siblingPreservation, sibling.Execution.Launch.Attempt));
        var siblingPreview = Preview(f, sibling, siblingPreservation);
        Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(C), f.Op(), sibling.Execution.Launch.Attempt,
            siblingPreservation, f.Op(), siblingPreview.Identity));
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(C), publication, sibling.Execution.Launch.Attempt));
        Assert.Equal("C ready.\n", accepted.Result.Report);
        Assert.Equal("C\n", File.ReadAllText(Path.Combine(sibling.Checkout, "c.txt")));
        Assert.Equal(1, f.Read().Results.Count(r => r.Task == C));
    }

    [Fact]
    public async Task A_shared_ref_block_needs_external_repair_then_a_recheck()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("result.txt", "X\n", ready.Checkout);
        await f.Close(ready, "X\n");
        f.Git.Git("update-ref", "refs/stash", f.A.Hex);
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Equal(new BlockScope.Refs(["refs/stash"]), blocked.Block.Scope);
        Assert.Contains("refs/stash", blocked.Block.Detail);
        var blockId = Assert.Single(f.Read().Blocks).Key;
        var preservation = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, ready.Execution.Launch.Attempt));
        var preview = Preview(f, ready, preservation);
        Assert.Empty(preview.Paths);
        Assert.Empty(preview.Repairs);
        Assert.Equal(new[] { blockId }, preview.Rechecks);
        var operation = f.Op();
        Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(T), operation, ready.Execution.Launch.Attempt, preservation, f.Op(), preview.Identity));
        Assert.Equal(0, Moves(f, operation));
        Assert.Single(f.Read().Restorations);
        Assert.False(f.Read().Blocks[blockId].Resolved);
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", GitFixture.Read(f.Git.Open().ReadRef("refs/stash"))?.Hex);
        Assert.IsType<Publication.Blocked>(f.Materializer().Publish(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt));
        f.Git.Git("update-ref", "-d", "refs/stash");
        var replacement = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), replacement, ready.Execution.Launch.Attempt));
        var recheck = Preview(f, ready, replacement);
        Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt, replacement, f.Op(), recheck.Identity));
        Assert.True(f.Read().Blocks[blockId].Resolved);
        Assert.Null(GitFixture.Read(f.Git.Open().ReadRef("refs/stash")));
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt));
        Assert.Equal("X\n", accepted.Result.Report);
        Assert.Equal("X\n", File.ReadAllText(Path.Combine(ready.Checkout, "result.txt")));
        Assert.Single(f.Read().Results);
    }

    private static readonly Lazy<string[]> RecordedRestorePoints = new(() =>
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var restore = RestoreMatrixSetup(f).GetAwaiter().GetResult();
        var points = new List<string>();
        Assert.IsType<Restoration.Restored>(f.Materializer(probe: points.Add).Restore(f.Lease(T), restore.Operation,
            restore.Ready.Execution.Launch.Attempt, restore.Preservation, restore.Confirmation, restore.Preview.Identity));
        AssertMatrixBaseline(f, restore);
        Assert.Equal(4, Moves(f, restore.Operation));
        Assert.Single(Observations<GitMutation.RemoveIndexLock>(f, restore.Operation));
        Assert.Single(Observations<GitMutation.RestoreRef>(f, restore.Operation));
        Assert.Single(Observations<GitMutation.RestoreFiles>(f, restore.Operation));
        Assert.Single(Observations<GitMutation.AlignIndex>(f, restore.Operation));
        Assert.Equal(4, Observations<GitMutation>(f, restore.Operation).Count(o => !o.Adopted));
        Assert.Equal(new[] { "a.txt", "extra.txt" }, restore.Preview.Paths.Select(p => p.Path));
        Assert.Contains("journal.restore-plan.after", points);
        Assert.Contains("git.restore-lock.after", points);
        Assert.Contains("git.restore-branch.after", points);
        Assert.Contains("restore.file.a.txt.written", points);
        Assert.Contains("git.restore-file-extra.txt.after", points);
        Assert.Contains("git.restore-index.after", points);
        Assert.Contains("journal.restored.after", points);
        return points.Distinct().ToArray();
    });

    public static IEnumerable<object[]> RestoreCrashPoints => RecordedRestorePoints.Value.Select(point => new object[] { point });

    [LinuxOrWindowsTheory]
    [MemberData(nameof(RestoreCrashPoints), DisableDiscoveryEnumeration = true)]
    public async Task Every_restore_probe_converges_on_the_baseline(string point)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var restore = await RestoreMatrixSetup(f);
        var attempt = restore.Ready.Execution.Launch.Attempt;
        Assert.Throws<Crash>(() => f.Materializer(probe: step => { if (step == point) throw new Crash(); })
            .Restore(f.Lease(T), restore.Operation, attempt, restore.Preservation, restore.Confirmation, restore.Preview.Identity));
        var written = point == "restore.file.a.txt.written";
        if (written)
        {
            var scratch = Path.Combine(restore.ScratchRoot, OperationIds.Derive(restore.Operation, "restore-plan").Value.ToString("D"));
            Assert.True(Directory.Exists(scratch));
            Assert.Equal("A\n", File.ReadAllText(Assert.Single(Directory.GetFiles(scratch))));
            Assert.Equal("changed\n", File.ReadAllText(Path.Combine(restore.Ready.Checkout, "a.txt")));
        }
        var cleanupChecked = false;
        var outcome = f.Materializer(probe: step =>
        {
            if (!written || step != "git.restore-file-a.txt.before") return;
            cleanupChecked = true;
            Assert.False(Directory.Exists(Path.Combine(restore.ScratchRoot, OperationIds.Derive(restore.Operation, "restore-plan").Value.ToString("D"))));
        }).Restore(f.Lease(T), restore.Operation, attempt, restore.Preservation, restore.Confirmation, restore.Preview.Identity);
        var operations = new List<OperationId> { restore.Operation };
        if (outcome is Restoration.Blocked blocked)
        {
            Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
            var preservation = f.Op();
            Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, attempt));
            var preview = Preview(f, restore.Ready, preservation);
            Assert.Equal(OperationIds.Derive(restore.Operation, "restore-plan"), preview.Supersedes);
            var replacement = f.Op();
            operations.Add(replacement);
            outcome = f.Materializer().Restore(f.Lease(T), replacement, attempt, preservation, f.Op(), preview.Identity);
            Assert.Equal("ReplacementConflict", Assert.IsType<Restoration.Rejected>(f.Materializer().Restore(f.Lease(T), restore.Operation,
                attempt, restore.Preservation, restore.Confirmation, restore.Preview.Identity)).Reason.Problem.ToString());
        }
        Assert.IsType<Restoration.Restored>(outcome);
        AssertMatrixBaseline(f, restore);
        Assert.Single(f.Read().Restorations, p => operations.Any(o => OperationIds.Derive(o, "restore-plan") == p.Key));
        Assert.Single(f.Read().Restorations);
        Assert.Equal(0, f.Read().Blocks.Values.Count(b => !b.Resolved));
        var observations = operations.SelectMany(o => Observations<GitMutation>(f, o)).ToArray();
        var adopted = point is "git.restore-lock.after" or "journal.restore-lock-observed.before" or
            "git.restore-branch.after" or "journal.restore-branch-observed.before" or
            "git.restore-file-extra.txt.after" or "journal.restore-files-observed.before" or
            "git.restore-index.after" or "journal.restore-index-observed.before" ? 1 : 0;
        Assert.Equal(4, observations.Length);
        Assert.Equal(adopted, observations.Count(o => o.Adopted));
        Assert.Equal(4 - adopted, observations.Count(o => !o.Adopted));
        if (written) Assert.True(cleanupChecked);
        var sequence = f.Read().Sequence;
        var last = operations[^1];
        var plan = Assert.IsType<MaterializationPlan.Restoration>(f.Read().Plans[OperationIds.Derive(last, "restore-plan")]);
        Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(T), last, attempt, plan.Preservation, plan.Confirmation, plan.Preview));
        Assert.Equal(sequence, f.Read().Sequence);
    }

    [LinuxOrWindowsTheory]
    [MemberData(nameof(RestoreCrashPoints), DisableDiscoveryEnumeration = true)]
    public async Task A_late_write_at_every_restore_probe_blocks_until_the_receipt_is_recorded(string point)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var restore = await RestoreMatrixSetup(f);
        var attempt = restore.Ready.Execution.Launch.Attempt;
        Assert.Throws<Crash>(() => f.Materializer(probe: step =>
        {
            if (step != point) return;
            f.Git.Write("root.txt", "late\n", restore.Ready.Checkout);
            throw new Crash();
        }).Restore(f.Lease(T), restore.Operation, attempt, restore.Preservation, restore.Confirmation, restore.Preview.Identity));
        var before = Moves(f, restore.Operation);
        var branch = GitFixture.Read(f.Git.Open().ReadRef(restore.Ready.Execution.Location.Owner.Branch));
        var index = GitFixture.Read(f.Git.Open().IndexDigest(restore.Ready.Checkout));
        var a = File.ReadAllBytes(Path.Combine(restore.Ready.Checkout, "a.txt"));
        var extraPath = Path.Combine(restore.Ready.Checkout, "extra.txt");
        var extra = File.Exists(extraPath) ? File.ReadAllBytes(extraPath) : null;
        var sequence = f.Read().Sequence;
        var outcome = f.Materializer().Restore(f.Lease(T), restore.Operation, attempt,
            restore.Preservation, restore.Confirmation, restore.Preview.Identity);
        if (point == "journal.restored.after")
        {
            Assert.Equal("Restored", outcome.GetType().Name);
            Assert.Equal(4, before);
            Assert.Equal(sequence, f.Read().Sequence);
            Assert.Equal(0, f.Read().Blocks.Values.Count(b => !b.Resolved));
        }
        else
        {
            var blocked = Assert.IsType<Restoration.Blocked>(outcome);
            Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
            Assert.Equal("The checkout changed after it was preserved. Preserve it again.", blocked.Block.Detail);
            Assert.Contains("root.txt", Assert.IsType<BlockScope.Checkout>(blocked.Block.Scope).Paths);
        }
        Assert.Equal(before, Moves(f, restore.Operation));
        Assert.Equal("late\n", File.ReadAllText(Path.Combine(restore.Ready.Checkout, "root.txt")));
        Assert.Equal(branch, GitFixture.Read(f.Git.Open().ReadRef(restore.Ready.Execution.Location.Owner.Branch)));
        Assert.Equal(index, GitFixture.Read(f.Git.Open().IndexDigest(restore.Ready.Checkout)));
        Assert.Equal(a, File.ReadAllBytes(Path.Combine(restore.Ready.Checkout, "a.txt")));
        Assert.Equal(extra, File.Exists(extraPath) ? File.ReadAllBytes(extraPath) : null);
        var preservation = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, attempt));
        var preview = Preview(f, restore.Ready, preservation);
        var replacement = f.Op();
        Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(T), replacement, attempt, preservation, f.Op(), preview.Identity));
        Assert.Single(Observations<GitMutation.RestoreFiles>(f, replacement));
        AssertMatrixBaseline(f, restore);
    }

    [UnixTheory]
    [InlineData("executable", false, "true")]
    [InlineData("executable", true, "true")]
    [InlineData("unchanged", false, "true")]
    [InlineData("directory", false, "true")]
    [InlineData("executable", false, "false")]
    [InlineData("executable", false, "unset")]
    [UnsupportedOSPlatform("windows")]
    public async Task Restore_keeps_a_later_paths_changed_type_or_executable_mode(string change, bool crash, string fileMode)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await f.Close(ready);
        var attempt = ready.Execution.Launch.Attempt;
        Assert.Equal(0, fileMode == "unset"
            ? f.Git.Run(f.Git.Folder, "config", "--unset", "core.fileMode").ExitCode
            : f.Git.Run(f.Git.Folder, "config", "core.fileMode", fileMode).ExitCode);
        f.Git.Write("a.txt", "late\n", ready.Checkout);
        f.Git.Write("b.txt", "extra\n", ready.Checkout);
        var preservation = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, attempt));
        var materializer = f.Materializer(volumes: _ => 1UL);
        var preview = Assert.IsType<RestorePreviewRead.Previewed>(materializer.PreviewRestore(f.Lease(T), attempt, preservation)).Preview;
        Assert.Equal(new[] { "a.txt", "b.txt" }, preview.Paths.Select(path => path.Path));
        var operation = f.Op();
        var confirmation = f.Op();
        var b = Path.Combine(ready.Checkout, "b.txt");
        void Change()
        {
            if (change == "executable")
                File.SetUnixFileMode(b, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            if (change == "directory")
            {
                File.Delete(b);
                Directory.CreateDirectory(b);
                File.WriteAllText(Path.Combine(b, "inner.txt"), "inner\n");
            }
        }
        Restoration outcome;
        if (crash)
        {
            Assert.Throws<Crash>(() => f.Materializer(volumes: _ => 1UL, probe: step =>
            {
                if (step == "git.restore-file-a.txt.after") throw new Crash();
            }).Restore(f.Lease(T), operation, attempt, preservation, confirmation, preview.Identity));
            Change();
            outcome = materializer.Restore(f.Lease(T), operation, attempt, preservation, confirmation, preview.Identity);
        }
        else
        {
            var fired = false;
            outcome = f.Materializer(volumes: _ => 1UL, probe: step =>
            {
                if (step != "git.restore-file-a.txt.after" || fired) return;
                fired = true;
                Change();
            }).Restore(f.Lease(T), operation, attempt, preservation, confirmation, preview.Identity);
            Assert.True(fired);
        }
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        if (change == "unchanged" || fileMode == "false")
        {
            Assert.IsType<Restoration.Restored>(outcome);
            Assert.False(Path.Exists(b));
        }
        else
        {
            Assert.Equal("DirtyWorktree", Assert.IsType<Restoration.Blocked>(outcome).Block.Problem.ToString());
            if (change == "directory") Assert.Equal("inner\n", File.ReadAllText(Path.Combine(b, "inner.txt")));
            else
            {
                Assert.Equal("extra\n", File.ReadAllText(b));
                Assert.Equal(UnixFileMode.UserExecute, File.GetUnixFileMode(b) & UnixFileMode.UserExecute);
            }
        }
    }

    [Fact]
    public Task Restore_forgets_cached_stat_when_autocrlf_changes_after_checkout() => RestoreAutocrlf(null);

    [Theory]
    [InlineData("journal.restore-files-observed.after")]
    [InlineData("git.restore-stat.after")]
    public Task Restore_forgets_cached_stat_after_a_files_or_stat_step_crash(string crashPoint) => RestoreAutocrlf(crashPoint);

    private static async Task RestoreAutocrlf(string? crashPoint)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await f.Close(ready);
        var attempt = ready.Execution.Launch.Attempt;
        Assert.Equal(0, f.Git.Run(f.Git.Folder, "config", "core.autocrlf", "true").ExitCode);
        File.WriteAllBytes(Path.Combine(ready.Checkout, "a.txt"), [108, 97, 116, 101, 13, 10]);
        f.Git.Write("u.txt", "untracked\n", ready.Checkout);
        var preservation = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, attempt));
        var materializer = f.Materializer(volumes: _ => 1UL);
        var preview = Assert.IsType<RestorePreviewRead.Previewed>(materializer.PreviewRestore(f.Lease(T), attempt, preservation)).Preview;
        Assert.Equal(new[] { "a.txt", "u.txt" }, preview.Paths.Select(path => path.Path));
        var operation = f.Op();
        var confirmation = f.Op();
        if (crashPoint is not null)
            Assert.Throws<Crash>(() => f.Materializer(volumes: _ => 1UL, probe: step =>
            {
                if (step == crashPoint) throw new Crash();
            }).Restore(f.Lease(T), operation, attempt, preservation, confirmation, preview.Identity));
        Assert.IsType<Restoration.Restored>(materializer.Restore(f.Lease(T), operation, attempt, preservation, confirmation, preview.Identity));
        Assert.Equal(new byte[] { 65, 13, 10 }, File.ReadAllBytes(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal("", f.Git.Run(ready.Checkout, "status", "--porcelain", "--untracked-files=no").Text);
        Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), f.Op(), attempt));
        Assert.Single(f.Read().Results);
        var again = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), again, attempt));
        var second = Assert.IsType<RestorePreviewRead.Previewed>(materializer.PreviewRestore(f.Lease(T), attempt, again)).Preview;
        Assert.Empty(second.Paths);
    }

    private static async Task<RestoreCase> RestoreMatrixSetup(PreparationFixture f)
    {
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await f.Close(ready);
        File.AppendAllText(f.Git.PathOf(".git/info/exclude"), "a.txt.idp-restore-0000\n");
        f.Git.Write("a.txt.idp-restore-0000", "ignored\n", ready.Checkout);
        MoveToForeign(f, ready);
        f.Git.Write("a.txt", "changed\n", ready.Checkout);
        f.Git.Write("extra.txt", "extra\n", ready.Checkout);
        Assert.Equal(0, f.Git.Run(ready.Checkout, "add", "a.txt", "extra.txt").ExitCode);
        var index = GitFixture.Read(f.Git.Open().IndexPath(ready.Checkout));
        File.WriteAllText(index + ".lock", "partial\n");
        var preservation = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, ready.Execution.Launch.Attempt));
        var preview = Preview(f, ready, preservation);
        return new(ready, preservation, f.Op(), f.Op(), preview, index, Path.Combine(Path.GetDirectoryName(index)!, "idevelop-restore"));
    }

    private static void AssertMatrixBaseline(PreparationFixture f, RestoreCase restore)
    {
        var checkout = restore.Ready.Checkout;
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", GitFixture.Read(f.Git.Open().ReadRef(restore.Ready.Execution.Location.Owner.Branch))?.Hex);
        Assert.Equal("refs/heads/idp/93f23689/task/90d5b0a2\n", f.Git.Run(checkout, "symbolic-ref", "HEAD").Text);
        Assert.Equal(new byte[] { 65, 10 }, File.ReadAllBytes(Path.Combine(checkout, "a.txt")));
        Assert.Equal(new byte[] { 114, 111, 111, 116, 10 }, File.ReadAllBytes(Path.Combine(checkout, "root.txt")));
        Assert.Equal(new byte[] { 97, 112, 112, 114, 111, 118, 101, 100, 10 }, File.ReadAllBytes(Path.Combine(checkout, "plan.txt")));
        Assert.False(File.Exists(Path.Combine(checkout, "extra.txt")));
        Assert.Equal("a.txt\nplan.txt\nroot.txt\n", f.Git.Run(checkout, "ls-files").Text);
        Assert.Equal("", f.Git.Run(checkout, "status", "--porcelain").Text);
        Assert.False(File.Exists(restore.IndexPath + ".lock"));
        Assert.False(Directory.Exists(restore.ScratchRoot));
        Assert.Equal("ignored\n", File.ReadAllText(Path.Combine(checkout, "a.txt.idp-restore-0000")));
    }

    private static async Task<Preparation.Ready> DoneWriter(PreparationFixture f)
    {
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("result.txt", "done\n", ready.Checkout);
        await f.Close(ready, "done\n");
        return ready;
    }

    private static CommitId MoveToForeign(PreparationFixture f, Preparation.Ready ready)
    {
        f.Git.Write("foreign.txt", "foreign\n");
        var foreign = f.Git.Commit("foreign");
        f.Git.Git("update-ref", ready.Execution.Location.Owner.Branch, foreign.Hex);
        return foreign;
    }

    private static IEnumerable<GitObservation> Observations<T>(PreparationFixture f, OperationId operation) where T : GitMutation
    {
        var record = f.Read();
        var plan = OperationIds.Derive(operation, "restore-plan");
        return record.GitObservations.Where(p => record.GitIntents[p.Key].Plan == plan && record.GitIntents[p.Key].Mutation is T).Select(p => p.Value);
    }

    private static void MakeExecutable(GitFixture git, string checkout, string path)
    {
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(Path.Combine(checkout, path), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        Assert.Equal(0, git.Run(checkout, "add", path).ExitCode);
        Assert.Equal(0, git.Run(checkout, "update-index", "--chmod=+x", path).ExitCode);
    }

    internal static RestorePreview Preview(PreparationFixture f, Preparation.Ready ready, OperationId preservation) =>
        Assert.IsType<RestorePreviewRead.Previewed>(f.Materializer().PreviewRestore(f.Lease(ready.Execution.Location.Owner.Task), ready.Execution.Launch.Attempt, preservation)).Preview;

    internal static int Moves(PreparationFixture f, OperationId operation)
    {
        var plan = OperationIds.Derive(operation, "restore-plan");
        var record = f.Read();
        return record.GitObservations.Keys.Count(intent => record.GitIntents[intent].Plan == plan);
    }
}
