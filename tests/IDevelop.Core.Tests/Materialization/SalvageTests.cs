using IDevelop.Core.Tests.Git;
using IDevelop.Execution;
using IDevelop.Projects;
using IDevelop.TestSupport;
using static IDevelop.Core.Tests.Runs.RunFixtures;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;
using Crash = IDevelop.Core.Tests.Materialization.PublicationTests.Crash;

namespace IDevelop.Core.Tests.Materialization;

[Collection(ProcessCollection.Name)]
public sealed class SalvageTests
{
    private static readonly OperationId Operation = new(Id(2000));
    private static readonly OperationId ResetOperation = new(Id(2001));
    private const string Commit = "0dbf5cbc9310ef3abbc28073142652917c69dc2f";
    private const string Target = "adfe40b30c176fb407933286f51d15ea9b54cdc3";

    internal static async Task<Preparation.Ready> FailedWriter(PreparationFixture f, bool ownCommit = false)
    {
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        File.AppendAllText(Path.Combine(f.Git.Open().CommonDirectory, "info/exclude"), "/cache.txt\n");
        if (ownCommit)
        {
            f.Git.Write("b.txt", "B\n", ready.Checkout);
            Assert.Equal(0, f.Git.Run(ready.Checkout, "add", "b.txt").ExitCode);
            Assert.Equal(0, f.Git.Run(ready.Checkout, "-c", "commit.gpgSign=false", "commit", "-qm", "b").ExitCode);
        }
        f.Git.Write("a.txt", "modified\n", ready.Checkout);
        f.Git.Write("new.txt", "unfinished\n", ready.Checkout);
        f.Git.Write("cache.txt", "cache\n", ready.Checkout);
        f.Close(ready, outcome: TerminalAttemptOutcome.Failed);
        return ready;
    }

    [Fact]
    public async Task Failed_code_is_retained_without_acceptance_and_confirmed_retry_restores_only_salvaged_paths()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await FailedWriter(f);
        var repository = f.Git.Open();
        var index = GitFixture.Read(repository.IndexPath(ready.Checkout));
        var before = File.ReadAllBytes(index);
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(W, f.RunId, Operation, ready.Execution.Launch.Attempt));
        Assert.Equal(Commit, retained.Commit.Hex);
        Assert.Equal("unfinished\n", f.Git.Git("show", Commit + ":new.txt"));
        Assert.Equal("modified\n", f.Git.Git("show", Commit + ":a.txt"));
        Assert.Equal(before, File.ReadAllBytes(index));
        var plan = Assert.IsType<MaterializationPlan.Salvage>(f.Read().Plans[retained.Receipt.Plan]);
        Assert.Equal(new EvidenceFile("new.txt", new("be02c0270dc16cf866391d81369ce9b50c9bd1c9cb0834a5c2788c70b35ead2e"), 11),
            Assert.Single(plan.Untracked));
        Assert.Equal("unfinished\n", File.ReadAllText(Path.Combine(ready.Checkout, "new.txt")));
        var reset = Assert.IsType<RetryReset.Reset>(f.Materializer().ResetForRetry(W, f.RunId, ResetOperation, retained.Receipt.Plan, f.Op()));
        Assert.Equal(Target, reset.Target.Hex);
        Assert.False(File.Exists(Path.Combine(ready.Checkout, "new.txt")));
        Assert.Equal("cache\n", File.ReadAllText(Path.Combine(ready.Checkout, "cache.txt")));
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal("", f.Git.Run(ready.Checkout, "status", "--porcelain").Text);
        Assert.Empty(f.Read().Results);
        var retry = Assert.IsType<Preparation.Ready>(await f.Prepare(T, cause: new AttemptCause.Retry(ready.Execution.Launch.Attempt, f.Op())));
        Assert.Equal(ready.Execution.Location.Owner, retry.Execution.Location.Owner);
        Assert.Equal(Target, retry.Execution.Location.AttemptBase.Hex);
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("new")]
    [InlineData("tracked")]
    [InlineData("staged")]
    public async Task Any_inventory_change_blocks_reset_and_preserves_new_work(string mode)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await FailedWriter(f);
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(W, f.RunId, Operation, ready.Execution.Launch.Attempt));
        var path = mode == "new" ? "other.txt" : mode == "tracked" ? "a.txt" : "new.txt";
        f.Git.Write(path, "changed\n", ready.Checkout);
        if (mode == "staged") Assert.Equal(0, f.Git.Run(ready.Checkout, "add", "new.txt").ExitCode);
        var before = File.ReadAllBytes(GitFixture.Read(f.Git.Open().IndexPath(ready.Checkout)));
        var blocked = Assert.IsType<RetryReset.Blocked>(f.Materializer().ResetForRetry(W, f.RunId, ResetOperation, retained.Receipt.Plan, f.Op()));
        Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
        Assert.Equal("changed\n", File.ReadAllText(Path.Combine(ready.Checkout, path)));
        Assert.Equal(before, File.ReadAllBytes(GitFixture.Read(f.Git.Open().IndexPath(ready.Checkout))));
        Assert.Equal("cache\n", File.ReadAllText(Path.Combine(ready.Checkout, "cache.txt")));
        Assert.Equal(Target, GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch))?.Hex);
    }

    [Fact]
    public async Task Index_only_changes_after_salvage_block_retry_and_preserve_staged_bytes()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await FailedWriter(f);
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(W, f.RunId, Operation, ready.Execution.Launch.Attempt));
        f.Git.Write("a.txt", "staged\n", ready.Checkout);
        Assert.Equal(0, f.Git.Run(ready.Checkout, "add", "a.txt").ExitCode);
        f.Git.Write("a.txt", "modified\n", ready.Checkout);
        var blocked = Assert.IsType<RetryReset.Blocked>(f.Materializer().ResetForRetry(W, f.RunId, ResetOperation, retained.Receipt.Plan, f.Op()));
        Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
        Assert.Equal("The tracked or nonignored untracked inventory differs from retained salvage.", blocked.Block.Detail);
        Assert.Equal(Target, GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch))?.Hex);
        Assert.Equal("staged\n", f.Git.Run(ready.Checkout, "show", ":a.txt").Text);
        Assert.Equal("modified\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
    }

    [Fact]
    public async Task Edits_after_retry_plan_crash_block_resumed_reset_and_preserve_bytes()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await FailedWriter(f);
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(W, f.RunId, Operation, ready.Execution.Launch.Attempt));
        var confirmation = f.Op();
        Assert.Throws<Crash>(() => f.Materializer(probe: step => { if (step == "journal.retry-plan.after") throw new Crash(); })
            .ResetForRetry(W, f.RunId, ResetOperation, retained.Receipt.Plan, confirmation));
        f.Git.Write("a.txt", "changed\n", ready.Checkout);
        var blocked = Assert.IsType<RetryReset.Blocked>(f.Materializer().ResetForRetry(W, f.RunId, ResetOperation, retained.Receipt.Plan, confirmation));
        Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
        Assert.Equal("The tracked or nonignored untracked inventory differs from retained salvage.", blocked.Block.Detail);
        Assert.Equal("changed\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal(Target, GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch))?.Hex);
    }

    private static async Task<Preparation.Ready> DivergedWriter(PreparationFixture f, bool detached)
    {
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        if (detached)
        {
            f.Git.Write("b.txt", "B\n", ready.Checkout);
            Assert.Equal(0, f.Git.Run(ready.Checkout, "add", "b.txt").ExitCode);
            Assert.Equal(0, f.Git.Run(ready.Checkout, "-c", "commit.gpgSign=false", "commit", "-qm", "b").ExitCode);
            Assert.Equal("2f1d113f78fb3fe0c4c6d9ad1d7dc2788acecf67", f.Git.Run(ready.Checkout, "rev-parse", "HEAD").Text.Trim());
        }
        const string parent = "81ddb7c330112c7f16700ed002803a04b0bce693";
        Assert.Equal(0, (detached ? f.Git.Run(ready.Checkout, "checkout", "--detach", parent) :
            f.Git.Run(ready.Checkout, "reset", "--hard", parent)).ExitCode);
        f.Git.Write("new.txt", "unfinished\n", ready.Checkout);
        f.Close(ready, outcome: TerminalAttemptOutcome.Failed);
        return ready;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Detached_or_rewritten_writer_can_be_salvaged_and_reattached_for_retry(bool detached)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await DivergedWriter(f, detached);
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(W, f.RunId, Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("81ddb7c330112c7f16700ed002803a04b0bce693", Assert.Single(GitFixture.Read(f.Git.Open().ReadCommit(retained.Commit)).Parents).Hex);
        Assert.Equal("unfinished\n", f.Git.Git("show", retained.Commit.Hex + ":new.txt"));
        Assert.Equal("approved\n", f.Git.Git("show", retained.Commit.Hex + ":plan.txt"));
        var reset = Assert.IsType<RetryReset.Reset>(f.Materializer().ResetForRetry(W, f.RunId, ResetOperation, retained.Receipt.Plan, f.Op()));
        Assert.Equal(Target, reset.Target.Hex);
        Assert.Equal("refs/heads/idp/93f23689/task/90d5b0a2\n", f.Git.Run(ready.Checkout, "symbolic-ref", "HEAD").Text);
        Assert.Equal(Target, GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch))?.Hex);
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.False(File.Exists(Path.Combine(ready.Checkout, "new.txt")));
        Assert.Equal(reset, f.Materializer().ResetForRetry(W, f.RunId, ResetOperation, retained.Receipt.Plan, f.Op()));
    }

    [Fact]
    public async Task Retry_preserves_the_branch_when_it_is_registered_at_another_checkout()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await DivergedWriter(f, true);
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(W, f.RunId, Operation, ready.Execution.Launch.Attempt));
        f.Git.Git("worktree", "add", ".worktrees/foreign", "idp/93f23689/task/90d5b0a2");
        var blocked = Assert.IsType<RetryReset.Blocked>(f.Materializer().ResetForRetry(W, f.RunId, ResetOperation, retained.Receipt.Plan, f.Op()));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Equal("The task branch is registered at another checkout.", blocked.Block.Detail);
        Assert.Equal("2f1d113f78fb3fe0c4c6d9ad1d7dc2788acecf67", GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch))?.Hex);
        Assert.Equal("unfinished\n", File.ReadAllText(Path.Combine(ready.Checkout, "new.txt")));
    }

    [Fact]
    public async Task Resalvage_keeps_the_first_retention_ref_immutable()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await FailedWriter(f);
        var first = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(W, f.RunId, Operation, ready.Execution.Launch.Attempt));
        f.Git.Write("new.txt", "changed\n", ready.Checkout);
        var second = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(W, f.RunId, f.Op(), ready.Execution.Launch.Attempt));
        Assert.Equal(Commit, GitFixture.Read(f.Git.Open().ReadRef(first.Receipt.Ref))?.Hex);
        Assert.Equal("1df94d83e2d8a4355dd2a2024eca4475c2b11b64", second.Commit.Hex);
        Assert.Contains("/resalvage/", second.Receipt.Ref);
        Assert.Equal("unfinished\n", f.Git.Git("show", Commit + ":new.txt"));
        Assert.Equal("changed\n", f.Git.Git("show", second.Commit.Hex + ":new.txt"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_salvage_probe_replays_to_one_literal_receipt_even_after_settlement(bool terminal)
    {
        async Task<Preparation.Ready> Setup(PreparationFixture fixture)
        {
            var writer = await FailedWriter(fixture);
            if (terminal) Assert.IsType<RunDecision.Recorded>(fixture.Store.Settle(W, fixture.RunId, fixture.Op(), RunOutcome.Stopped));
            return writer;
        }
        var steps = new List<string>();
        using (var baseline = new PreparationFixture(FixtureWorkflow(Writer(T))))
        {
            var writer = await Setup(baseline);
            Assert.IsType<Salvage.Retained>(baseline.Materializer(probe: steps.Add).Salvage(W, baseline.RunId, Operation, writer.Execution.Launch.Attempt));
        }
        Assert.Contains("git.salvage-ref.after", steps);
        Assert.Contains("journal.salvage-retained.after", steps);
        foreach (var point in steps.Distinct())
        {
            using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
            var ready = await Setup(f);
            var index = GitFixture.Read(f.Git.Open().IndexPath(ready.Checkout));
            var bytes = File.ReadAllBytes(index);
            Assert.Throws<Crash>(() => f.Materializer(probe: step => { if (step == point) throw new Crash(); })
                .Salvage(W, f.RunId, Operation, ready.Execution.Launch.Attempt));
            var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(W, f.RunId, Operation, ready.Execution.Launch.Attempt));
            Assert.Equal(Commit, retained.Commit.Hex);
            Assert.Equal(bytes, File.ReadAllBytes(index));
            Assert.Single(f.Read().Receipts.Values, entry => entry.Event is RunEvent.SalvageRetained);
            Assert.Equal("unfinished\n", f.Git.Git("show", Commit + ":new.txt"));
            Assert.Equal(terminal ? "Stopped" : "Approved", f.Read().Phase.ToString());
            if (terminal)
            {
                var next = ready.Execution.Launch with { Turn = 2 };
                var claim = f.Store.Claim(W, f.RunId, f.Op(), next, f.Read().Inputs[ready.Execution.Inputs], ready.Execution.PromptHash);
                Assert.Equal("RunStopped", Assert.IsType<RunDecision.Rejected>(claim).Reason.Problem.ToString());
            }
            Assert.Equal(retained, Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(W, f.RunId, Operation, ready.Execution.Launch.Attempt)));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Every_confirmed_retry_probe_converges_without_recapturing_after_observed_reset(bool ownCommit, bool detached)
    {
        var steps = new List<string>();
        using (var baseline = new PreparationFixture(FixtureWorkflow(Writer(T))))
        {
            var ready = detached ? await DivergedWriter(baseline, true) : await FailedWriter(baseline, ownCommit);
            var retained = Assert.IsType<Salvage.Retained>(baseline.Materializer().Salvage(W, baseline.RunId, Operation, ready.Execution.Launch.Attempt));
            Assert.IsType<RetryReset.Reset>(baseline.Materializer(probe: steps.Add).ResetForRetry(W, baseline.RunId, ResetOperation, retained.Receipt.Plan, baseline.Op()));
        }
        Assert.Contains("git.retry-reset.after", steps);
        Assert.Contains("git.retry-branch.after", steps);
        Assert.Contains("git.retry-attach.after", steps);
        Assert.Contains("journal.retry-attach-observed.after", steps);
        Assert.Contains("git.retry-remove-new.txt.after", steps);
        foreach (var point in steps.Distinct())
        {
            using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
            var ready = detached ? await DivergedWriter(f, true) : await FailedWriter(f, ownCommit);
            var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(W, f.RunId, Operation, ready.Execution.Launch.Attempt));
            if (!detached) Assert.Equal(ownCommit ? "fafba3f02353ba47a6c4d4f4a26a9dd16ecf023b" : Commit, retained.Commit.Hex);
            Assert.Equal(detached ? "81ddb7c330112c7f16700ed002803a04b0bce693" : ownCommit ? "2f1d113f78fb3fe0c4c6d9ad1d7dc2788acecf67" : Target,
                Assert.Single(GitFixture.Read(f.Git.Open().ReadCommit(retained.Commit)).Parents).Hex);
            var confirmation = f.Op();
            Assert.Throws<Crash>(() => f.Materializer(probe: step => { if (step == point) throw new Crash(); })
                .ResetForRetry(W, f.RunId, ResetOperation, retained.Receipt.Plan, confirmation));
            var outcome = f.Materializer().ResetForRetry(W, f.RunId, ResetOperation, retained.Receipt.Plan, confirmation);
            Assert.True(outcome is RetryReset.Reset, point + ": " + outcome);
            var reset = Assert.IsType<RetryReset.Reset>(outcome);
            Assert.Equal(Target, reset.Target.Hex);
            Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
            Assert.False(File.Exists(Path.Combine(ready.Checkout, "new.txt")));
            if (!detached) Assert.Equal("cache\n", File.ReadAllText(Path.Combine(ready.Checkout, "cache.txt")));
            Assert.Equal("refs/heads/idp/93f23689/task/90d5b0a2\n", f.Git.Run(ready.Checkout, "symbolic-ref", "HEAD").Text);
            Assert.Equal(Target, GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch))?.Hex);
            Assert.Equal("", f.Git.Run(ready.Checkout, "status", "--porcelain").Text);
            Assert.Equal(4, f.Read().GitIntents.Values.Count(intent => intent.Plan == OperationIds.Derive(ResetOperation, "retry-plan")));
            Assert.Single(f.Read().Plans.Values.OfType<MaterializationPlan.RetryReset>());
            Assert.Single(f.Read().Receipts.Values, entry => entry.Event is RunEvent.SalvageRetained);
            Assert.Equal(reset, Assert.IsType<RetryReset.Reset>(f.Materializer().ResetForRetry(W, f.RunId, ResetOperation, retained.Receipt.Plan, confirmation)));
        }
    }

    [Theory]
    [InlineData("git.retry-reset.after")]
    [InlineData("journal.retry-reset-observed.after")]
    [InlineData("git.retry-remove-new.txt.before")]
    public async Task Interrupted_reset_preserves_later_changed_paths_and_blocks_cleanup(string point)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await FailedWriter(f);
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(W, f.RunId, Operation, ready.Execution.Launch.Attempt));
        var confirmation = f.Op();
        Assert.Throws<Crash>(() => f.Materializer(probe: step => { if (step == point) throw new Crash(); })
            .ResetForRetry(W, f.RunId, ResetOperation, retained.Receipt.Plan, confirmation));
        f.Git.Write("new.txt", "changed\n", ready.Checkout);
        var blocked = Assert.IsType<RetryReset.Blocked>(f.Materializer().ResetForRetry(W, f.RunId, ResetOperation, retained.Receipt.Plan, confirmation));
        Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
        Assert.Equal("changed\n", File.ReadAllText(Path.Combine(ready.Checkout, "new.txt")));
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal("cache\n", File.ReadAllText(Path.Combine(ready.Checkout, "cache.txt")));
        Assert.Equal("unfinished\n", f.Git.Git("show", Commit + ":new.txt"));
    }

    [Fact]
    public async Task Retry_requires_confirmation_retention_approved_phase_quiescence_and_both_locks()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await FailedWriter(f);
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(W, f.RunId, Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("ConfirmationRequired", Assert.IsType<RetryReset.Rejected>(f.Materializer().ResetForRetry(W, f.RunId, ResetOperation,
            retained.Receipt.Plan, default)).Reason.Problem.ToString());
        Assert.Equal("InvalidData", Assert.IsType<RetryReset.Rejected>(f.Materializer().ResetForRetry(W, f.RunId, ResetOperation,
            f.Op(), f.Op())).Reason.Problem.ToString());
        Assert.Equal("LiveWriter", Assert.IsType<RetryReset.Blocked>(f.Materializer(boundary: new UnprovenBoundary()).ResetForRetry(W, f.RunId,
            ResetOperation, retained.Receipt.Plan, f.Op())).Block.Problem.ToString());
        using (var held = RunLock.TryTake(DataFolder.Attempts(f.Git.Folder), T))
            Assert.Equal("LiveWriter", Assert.IsType<RetryReset.Blocked>(f.Materializer().ResetForRetry(W, f.RunId,
                ResetOperation, retained.Receipt.Plan, f.Op())).Block.Problem.ToString());
        using (var held = f.Git.Open().TakeMutationLock())
            Assert.Equal("JournalBusy", Assert.IsType<RetryReset.Rejected>(f.Materializer().ResetForRetry(W, f.RunId,
                ResetOperation, retained.Receipt.Plan, f.Op())).Reason.Problem.ToString());
        Assert.Equal("modified\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal("unfinished\n", File.ReadAllText(Path.Combine(ready.Checkout, "new.txt")));
        Assert.IsType<RetryReset.Reset>(f.Materializer().ResetForRetry(W, f.RunId, ResetOperation, retained.Receipt.Plan, f.Op()));
        Assert.IsType<RunDecision.Recorded>(f.Store.Settle(W, f.RunId, f.Op(), RunOutcome.Stopped));
        Assert.Equal("RunStopped", Assert.IsType<RetryReset.Rejected>(f.Materializer().ResetForRetry(W, f.RunId, ResetOperation,
            retained.Receipt.Plan, f.Op())).Reason.Problem.ToString());
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
    }

    [Fact]
    public async Task Retry_uses_latest_accepted_produced_code_and_keeps_target_tracked_and_ignored_paths()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var result = await f.Publish(T, f.A);
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T, cause: new AttemptCause.Retry(A1, f.Op())));
        File.AppendAllText(Path.Combine(f.Git.Open().CommonDirectory, "info/exclude"), "/cache.txt\n");
        Assert.Equal(0, f.Git.Run(ready.Checkout, "rm", "a.txt").ExitCode);
        Assert.Equal(0, f.Git.Run(ready.Checkout, "-c", "commit.gpgSign=false", "commit", "-qm", "remove a").ExitCode);
        f.Git.Write("a.txt", "unfinished\n", ready.Checkout);
        f.Git.Write("new.txt", "unfinished\n", ready.Checkout);
        f.Git.Write("cache.txt", "cache\n", ready.Checkout);
        f.Close(ready, outcome: TerminalAttemptOutcome.Failed);
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(W, f.RunId, Operation, ready.Execution.Launch.Attempt));
        Assert.Throws<Crash>(() => f.Materializer(probe: step => { if (step == "git.retry-reset.after") throw new Crash(); })
            .ResetForRetry(W, f.RunId, ResetOperation, retained.Receipt.Plan, f.Op()));
        var reset = Assert.IsType<RetryReset.Reset>(f.Materializer().ResetForRetry(W, f.RunId, ResetOperation, retained.Receipt.Plan, f.Op()));
        Assert.Equal("d4d26ecdf72779dbc9c5c983025fb51546c8f9ea", reset.Target.Hex);
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.False(File.Exists(Path.Combine(ready.Checkout, "new.txt")));
        Assert.Equal("cache\n", File.ReadAllText(Path.Combine(ready.Checkout, "cache.txt")));
        Assert.Equal(result.Id, f.Read().CurrentResults[T].Id);
    }

    [Fact]
    public async Task Observed_reset_is_not_repeated_over_later_tracked_or_untracked_work()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await FailedWriter(f);
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(W, f.RunId, Operation, ready.Execution.Launch.Attempt));
        Assert.Throws<Crash>(() => f.Materializer(probe: step => { if (step == "journal.retry-reset-observed.after") throw new Crash(); })
            .ResetForRetry(W, f.RunId, ResetOperation, retained.Receipt.Plan, f.Op()));
        f.Git.Write("a.txt", "later tracked\n", ready.Checkout);
        f.Git.Write("later.txt", "later untracked\n", ready.Checkout);
        var reset = Assert.IsType<RetryReset.Reset>(f.Materializer().ResetForRetry(W, f.RunId, ResetOperation, retained.Receipt.Plan, f.Op()));
        Assert.Equal(Target, reset.Target.Hex);
        Assert.Equal("later tracked\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal("later untracked\n", File.ReadAllText(Path.Combine(ready.Checkout, "later.txt")));
        Assert.False(File.Exists(Path.Combine(ready.Checkout, "new.txt")));
        Assert.Equal("cache\n", File.ReadAllText(Path.Combine(ready.Checkout, "cache.txt")));
        Assert.Equal("DirtyWorktree", Assert.IsType<Preparation.Blocked>(await f.Prepare(T,
            cause: new AttemptCause.Retry(ready.Execution.Launch.Attempt, f.Op()))).Block.Problem.ToString());
    }

    [Fact]
    public async Task Ignored_obstruction_to_target_is_preserved_and_blocks_hard_reset()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        Assert.Equal(0, f.Git.Run(ready.Checkout, "rm", "a.txt").ExitCode);
        Assert.Equal(0, f.Git.Run(ready.Checkout, "-c", "commit.gpgSign=false", "commit", "-qm", "remove a").ExitCode);
        File.AppendAllText(Path.Combine(f.Git.Open().CommonDirectory, "info/exclude"), "/a.txt\n");
        f.Git.Write("a.txt", "ignored unfinished\n", ready.Checkout);
        f.Close(ready, outcome: TerminalAttemptOutcome.Failed);
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(W, f.RunId, Operation, ready.Execution.Launch.Attempt));
        var blocked = Assert.IsType<RetryReset.Blocked>(f.Materializer().ResetForRetry(W, f.RunId, ResetOperation, retained.Receipt.Plan, f.Op()));
        Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
        Assert.Equal("ignored unfinished\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal(1, f.Git.Run(ready.Checkout, "ls-files", "--error-unmatch", "a.txt").ExitCode);
        Assert.Equal("A\n", f.Git.Git("show", Target + ":a.txt"));
    }

    [Fact]
    public async Task Salvage_preserves_dirty_submodule_contents_and_real_index_bytes()
    {
        using var module = new GitFixture();
        module.Write("module.txt", "module\n");
        module.Commit("module");
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        Assert.Equal(0, f.Git.Run(ready.Checkout, "-c", "protocol.file.allow=always", "submodule", "add", "-q", module.Folder, "m").ExitCode);
        Assert.Equal(0, f.Git.Run(ready.Checkout, "-c", "commit.gpgSign=false", "commit", "-qm", "module").ExitCode);
        f.Git.Write("m/module.txt", "dirty module\n", ready.Checkout);
        f.Git.Write("m/new.txt", "module unfinished\n", ready.Checkout);
        f.Git.Write("new.txt", "outer unfinished\n", ready.Checkout);
        f.Close(ready, outcome: TerminalAttemptOutcome.Failed);
        var before = File.ReadAllBytes(GitFixture.Read(f.Git.Open().IndexPath(ready.Checkout)));
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(W, f.RunId, Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("outer unfinished\n", f.Git.Git("show", retained.Commit.Hex + ":new.txt"));
        Assert.Equal("dirty module\n", File.ReadAllText(Path.Combine(ready.Checkout, "m/module.txt")));
        Assert.Equal("module unfinished\n", File.ReadAllText(Path.Combine(ready.Checkout, "m/new.txt")));
        Assert.Equal(before, File.ReadAllBytes(GitFixture.Read(f.Git.Open().IndexPath(ready.Checkout))));
        Assert.Equal("new.txt", Assert.Single(Assert.IsType<MaterializationPlan.Salvage>(f.Read().Plans[retained.Receipt.Plan]).Untracked).RelativePath);
    }

    [Theory]
    [InlineData("Approved")]
    [InlineData("StopRequested")]
    [InlineData("Completed")]
    [InlineData("Stopped")]
    [InlineData("Failed")]
    [InlineData("Abandoned")]
    public async Task Salvage_is_preservation_maintenance_in_every_run_phase(string phase)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var result = await f.Publish(T, f.A);
        var owner = f.Read().Preparations[new(A1, 1)].Location.Owner;
        var checkout = Path.Combine(f.Git.Folder, owner.RelativePath);
        if (phase == "StopRequested") Assert.IsType<RunDecision.Recorded>(f.Store.Stop(W, f.RunId, f.Op()));
        else if (phase == "Abandoned") Assert.IsType<RunDecision.Recorded>(f.Store.Abandon(W, f.RunId, f.Op(), f.Op(), "Preserve work."));
        else if (Enum.TryParse<RunOutcome>(phase, out var outcome)) Assert.IsType<RunDecision.Recorded>(f.Store.Settle(W, f.RunId, f.Op(), outcome));
        f.Git.Write("a.txt", "modified\n", checkout);
        f.Git.Write("new.txt", "unfinished\n", checkout);
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(W, f.RunId, Operation, A1));
        Assert.Equal("627595566cb613dbc7757977f238ad71adad6983", retained.Commit.Hex);
        Assert.Equal(phase, f.Read().Phase.ToString());
        Assert.Equal("unfinished\n", f.Git.Git("show", retained.Commit.Hex + ":new.txt"));
        Assert.Equal(result.Id, Assert.Single(f.Read().Results).Id);
        Assert.Equal("modified\n", File.ReadAllText(Path.Combine(checkout, "a.txt")));
    }

    [Fact]
    public async Task Unclosed_quiescent_attempt_can_be_salvaged_without_inventing_a_successful_closure()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("a.txt", "modified\n", ready.Checkout);
        f.Git.Write("new.txt", "unfinished\n", ready.Checkout);
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(W, f.RunId, Operation, ready.Execution.Launch.Attempt));
        Assert.Equal(Commit, retained.Commit.Hex);
        Assert.False(f.Read().Closures.ContainsKey(ready.Execution.Launch.Attempt));
        Assert.Empty(f.Read().Results);
        Assert.Equal("unfinished\n", f.Git.Git("show", Commit + ":new.txt"));
        Assert.Equal("OutcomeMismatch", Assert.IsType<Publication.Rejected>(f.Materializer().Publish(W, f.RunId, f.Op(), ready.Execution.Launch.Attempt))
            .Reason.Problem.ToString());
    }

    [Theory]
    [InlineData(false, "before")]
    [InlineData(false, "after")]
    [InlineData(true, "before")]
    [InlineData(true, "after")]
    public async Task Lost_maintenance_block_and_resolution_acknowledgements_converge(bool resolution, string side)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await FailedWriter(f);
        if (resolution)
        {
            Assert.Equal("LiveWriter", Assert.IsType<Salvage.Blocked>(f.Materializer(boundary: new UnprovenBoundary())
                .Salvage(W, f.RunId, Operation, ready.Execution.Launch.Attempt)).Block.Problem.ToString());
        }
        Assert.Throws<Crash>(() => f.Materializer(boundary: resolution ? null : new UnprovenBoundary(), probe: step =>
        {
            if (resolution ? step.StartsWith("journal.maintenance-resolve-", StringComparison.Ordinal) && step.EndsWith("." + side, StringComparison.Ordinal)
                : step == "journal.salvage-quiescence-blocked." + side) throw new Crash();
        }).Salvage(W, f.RunId, Operation, ready.Execution.Launch.Attempt));
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(W, f.RunId, Operation, ready.Execution.Launch.Attempt));
        Assert.Equal(Commit, retained.Commit.Hex);
        Assert.Single(f.Read().Salvages);
        Assert.All(f.Read().Blocks.Values, block => Assert.True(block.Resolved));
        Assert.Equal("unfinished\n", File.ReadAllText(Path.Combine(ready.Checkout, "new.txt")));
        var reset = Assert.IsType<RetryReset.Blocked>(f.Materializer(boundary: new UnprovenBoundary()).ResetForRetry(W, f.RunId, ResetOperation,
            retained.Receipt.Plan, f.Op()));
        Assert.Equal("LiveWriter", reset.Block.Problem.ToString());
        Assert.Throws<Crash>(() => f.Materializer(probe: step =>
        {
            if (step.StartsWith("journal.maintenance-resolve-", StringComparison.Ordinal) && step.EndsWith("." + side, StringComparison.Ordinal)) throw new Crash();
        }).ResetForRetry(W, f.RunId, ResetOperation, retained.Receipt.Plan, f.Op()));
        Assert.Equal(Target, Assert.IsType<RetryReset.Reset>(f.Materializer().ResetForRetry(W, f.RunId, ResetOperation,
            retained.Receipt.Plan, f.Op())).Target.Hex);
        Assert.All(f.Read().Blocks.Values, block => Assert.True(block.Resolved));
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal("cache\n", File.ReadAllText(Path.Combine(ready.Checkout, "cache.txt")));
    }

    [Fact]
    public async Task Redirected_checkout_git_directory_cannot_authorize_removing_another_repository_index_lock()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await FailedWriter(f);
        using var other = new GitFixture(initialize: false);
        Assert.Equal(0, f.Git.Run(f.Git.Folder, "clone", "--no-hardlinks", "-q", f.Git.Folder, other.Folder).ExitCode);
        other.Git("update-ref", ready.Execution.Location.Owner.Branch, f.A.Hex);
        other.Git("symbolic-ref", "HEAD", ready.Execution.Location.Owner.Branch);
        var foreignLock = GitFixture.Read(other.Open().IndexPath(other.Folder)) + ".lock";
        File.WriteAllText(foreignLock, "foreign lock\n");
        var gitFile = Path.Combine(ready.Checkout, ".git");
        var original = File.ReadAllBytes(gitFile);
        var attributes = File.GetAttributes(gitFile);
        // Git for Windows hides the .git file, and Windows refuses to overwrite a hidden file.
        File.SetAttributes(gitFile, FileAttributes.Normal);
        File.WriteAllText(gitFile, "gitdir: " + other.Open().CommonDirectory + "\n");
        var blocked = Assert.IsType<Salvage.Blocked>(f.Materializer().Salvage(W, f.RunId, Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Equal("foreign lock\n", File.ReadAllText(foreignLock));
        Assert.Equal("unfinished\n", File.ReadAllText(Path.Combine(ready.Checkout, "new.txt")));
        File.WriteAllBytes(gitFile, original);
        File.SetAttributes(gitFile, attributes);
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(W, f.RunId, Operation, ready.Execution.Launch.Attempt));
        Assert.Equal(Commit, retained.Commit.Hex);
        Assert.Equal("foreign lock\n", File.ReadAllText(foreignLock));
    }

    [Theory]
    [InlineData("boundary")]
    [InlineData("task-lock")]
    [InlineData("ownership")]
    public async Task Stale_index_lock_is_removed_only_with_quiescence_and_checkout_ownership(string mode)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await FailedWriter(f);
        var indexLock = GitFixture.Read(f.Git.Open().IndexPath(ready.Checkout)) + ".lock";
        File.WriteAllText(indexLock, "stale\n");
        using (var held = mode == "task-lock" ? RunLock.TryTake(DataFolder.Attempts(f.Git.Folder), T) : null)
        {
            if (mode == "ownership") f.Git.Git("worktree", "unlock", ready.Execution.Location.Owner.RelativePath);
            var blocked = Assert.IsType<Salvage.Blocked>(f.Materializer(boundary: mode == "boundary" ? new UnprovenBoundary() : null)
                .Salvage(W, f.RunId, Operation, ready.Execution.Launch.Attempt));
            Assert.Equal(mode == "ownership" ? "UncertainOwnership" : "LiveWriter", blocked.Block.Problem.ToString());
            Assert.Equal("stale\n", File.ReadAllText(indexLock));
        }
        if (mode == "ownership") f.Git.Git("worktree", "lock", "--reason", "idevelop 93f23689/90d5b0a2", ready.Execution.Location.Owner.RelativePath);
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(W, f.RunId, Operation, ready.Execution.Launch.Attempt));
        Assert.Equal(Commit, retained.Commit.Hex);
        Assert.False(File.Exists(indexLock));
        Assert.True(File.Exists(Path.Combine(DataFolder.Attempts(f.Git.Folder), T.ToString(), "run.lock")));
        Assert.Equal("unfinished\n", File.ReadAllText(Path.Combine(ready.Checkout, "new.txt")));
    }
}
