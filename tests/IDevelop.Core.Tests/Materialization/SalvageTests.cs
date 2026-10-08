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

    [Fact]
    public async Task Salvage_captures_edits_hidden_by_a_writer_fsmonitor_hook()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)), configureBase: git =>
        {
            git.Write("a/inside.txt", "inside\n");
            return git.Commit("layout");
        });
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        var hook = Path.Combine(Path.GetDirectoryName(f.Git.Folder)!, "fsmonitor-hook");
        File.WriteAllText(hook, "#!/bin/sh\nprintf 'token\\0'\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var environment = new Dictionary<string, string>(f.Git.Environment) { ["GIT_OPTIONAL_LOCKS"] = "1" };
        Assert.Equal(0, f.Git.Run(ready.Checkout, environment, "config", "core.fsmonitor", hook).ExitCode);
        Assert.Equal(0, f.Git.Run(ready.Checkout, environment, "status", "--porcelain").ExitCode);
        f.Git.Write("a/inside.txt", "edited\n", ready.Checkout);
        f.Git.Write("a/new.txt", "new\n", ready.Checkout);
        Assert.Equal(0, f.Git.Run(ready.Checkout, environment, "status", "--porcelain").ExitCode);
        f.Close(ready, outcome: TerminalAttemptOutcome.Failed);
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), f.Op(), ready.Execution.Launch.Attempt));
        var commit = retained.Commit.Hex;
        Assert.Equal("edited\n", f.Git.Git("show", commit + ":a/inside.txt"));
        Assert.Equal("new\n", f.Git.Git("show", commit + ":a/new.txt"));
    }

    [Fact]
    public async Task Salvage_captures_a_same_size_edit_hidden_by_relaxed_stat_checks()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)), configureBase: git =>
        {
            git.Write("a/inside.txt", "inside\n");
            return git.Commit("layout");
        });
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        var file = Path.Combine(ready.Checkout, "a", "inside.txt");
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, old);
        await System.Threading.Tasks.Task.Delay(1100);
        var environment = new Dictionary<string, string>(f.Git.Environment) { ["GIT_OPTIONAL_LOCKS"] = "1" };
        Assert.Equal(0, f.Git.Run(ready.Checkout, environment, "config", "core.checkStat", "minimal").ExitCode);
        Assert.Equal(0, f.Git.Run(ready.Checkout, environment, "config", "core.trustctime", "false").ExitCode);
        Assert.Equal(0, f.Git.Run(ready.Checkout, environment, "update-index", "--refresh").ExitCode);
        File.WriteAllText(file, "INSIDE\n");
        File.SetLastWriteTimeUtc(file, old);
        f.Close(ready, outcome: TerminalAttemptOutcome.Failed);
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), f.Op(), ready.Execution.Launch.Attempt));
        var commit = retained.Commit.Hex;
        Assert.Equal("INSIDE\n", f.Git.Git("show", commit + ":a/inside.txt"));
    }

    [Fact]
    public async Task A_symbolic_task_ref_after_salvage_blocks_retry_without_moving_the_foreign_branch()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await FailedWriter(f, ownCommit: true);
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
        f.Git.Git("branch", "foreign", "2f1d113f78fb3fe0c4c6d9ad1d7dc2788acecf67");
        f.Git.Git("symbolic-ref", ready.Execution.Location.Owner.Branch, "refs/heads/foreign");
        var confirmation = f.Op();
        var blocked = Assert.IsType<RetryReset.Blocked>(f.Materializer().ResetForRetry(f.Lease(T), ResetOperation, retained.Receipt.Plan, confirmation));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Equal("Ref refs/heads/idp/93f23689/task/90d5b0a2 is symbolic to refs/heads/foreign.", blocked.Block.Detail);
        Assert.Equal("2f1d113f78fb3fe0c4c6d9ad1d7dc2788acecf67", Ref(f, "refs/heads/foreign"));
        Assert.Equal("refs/heads/foreign\n", f.Git.Git("symbolic-ref", ready.Execution.Location.Owner.Branch));
        Assert.Equal("modified\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal(blocked, f.Materializer().ResetForRetry(f.Lease(T), ResetOperation, retained.Receipt.Plan, confirmation));
    }

    [Theory]
    [InlineData("--assume-unchanged", false)]
    [InlineData("--skip-worktree", false)]
    [InlineData("--assume-unchanged", true)]
    [InlineData("--skip-worktree", true)]
    public async Task Hidden_index_entries_block_salvage_or_retry_and_preserve_writer_bytes(string flag, bool afterSalvage)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await FailedWriter(f, ownCommit: true);
        var retained = afterSalvage ? Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), Operation,
            ready.Execution.Launch.Attempt)) : null;
        Assert.Equal(0, f.Git.Run(ready.Checkout, "update-index", flag, "a.txt").ExitCode);
        var index = GitFixture.Read(f.Git.Open().IndexPath(ready.Checkout));
        var bytes = File.ReadAllBytes(index);
        var confirmation = f.Op();
        var materializer = f.Materializer();
        MaterializationBlock block;
        if (retained is null)
        {
            var blocked = Assert.IsType<Salvage.Blocked>(materializer.Salvage(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
            block = blocked.Block;
            Assert.Equal(blocked, materializer.Salvage(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
        }
        else
        {
            var blocked = Assert.IsType<RetryReset.Blocked>(materializer.ResetForRetry(f.Lease(T), ResetOperation, retained.Receipt.Plan, confirmation));
            block = blocked.Block;
            Assert.Equal(blocked, materializer.ResetForRetry(f.Lease(T), ResetOperation, retained.Receipt.Plan, confirmation));
            Assert.Equal("fafba3f02353ba47a6c4d4f4a26a9dd16ecf023b", Ref(f, retained.Receipt.Ref));
        }
        Assert.Equal("DirtyWorktree", block.Problem.ToString());
        Assert.Equal("The index hides changes to a.txt with assume-unchanged or skip-worktree.", block.Detail);
        Assert.Equal("modified\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal("unfinished\n", File.ReadAllText(Path.Combine(ready.Checkout, "new.txt")));
        Assert.Equal(bytes, File.ReadAllBytes(index));
        Assert.Equal("2f1d113f78fb3fe0c4c6d9ad1d7dc2788acecf67", Ref(f, ready.Execution.Location.Owner.Branch));
        Assert.Equal("B\n", f.Git.Git("show", "2f1d113f78fb3fe0c4c6d9ad1d7dc2788acecf67:b.txt"));
    }

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
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal(Commit, retained.Commit.Hex);
        Assert.Equal("unfinished\n", f.Git.Git("show", Commit + ":new.txt"));
        Assert.Equal("modified\n", f.Git.Git("show", Commit + ":a.txt"));
        Assert.Equal(before, File.ReadAllBytes(index));
        var plan = Assert.IsType<MaterializationPlan.Salvage>(f.Read().Plans[retained.Receipt.Plan]);
        Assert.Equal(new EvidenceFile("new.txt", new("be02c0270dc16cf866391d81369ce9b50c9bd1c9cb0834a5c2788c70b35ead2e"), 11),
            Assert.Single(plan.Untracked));
        Assert.Equal("unfinished\n", File.ReadAllText(Path.Combine(ready.Checkout, "new.txt")));
        var reset = Assert.IsType<RetryReset.Reset>(f.Materializer().ResetForRetry(f.Lease(T), ResetOperation, retained.Receipt.Plan, f.Op()));
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
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
        var path = mode == "new" ? "other.txt" : mode == "tracked" ? "a.txt" : "new.txt";
        f.Git.Write(path, "changed\n", ready.Checkout);
        if (mode == "staged") Assert.Equal(0, f.Git.Run(ready.Checkout, "add", "new.txt").ExitCode);
        var before = File.ReadAllBytes(GitFixture.Read(f.Git.Open().IndexPath(ready.Checkout)));
        var blocked = Assert.IsType<RetryReset.Blocked>(f.Materializer().ResetForRetry(f.Lease(T), ResetOperation, retained.Receipt.Plan, f.Op()));
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
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
        f.Git.Write("a.txt", "staged\n", ready.Checkout);
        Assert.Equal(0, f.Git.Run(ready.Checkout, "add", "a.txt").ExitCode);
        f.Git.Write("a.txt", "modified\n", ready.Checkout);
        var blocked = Assert.IsType<RetryReset.Blocked>(f.Materializer().ResetForRetry(f.Lease(T), ResetOperation, retained.Receipt.Plan, f.Op()));
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
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
        var confirmation = f.Op();
        Assert.Throws<Crash>(() => f.Materializer(probe: step => { if (step == "journal.retry-plan.after") throw new Crash(); })
            .ResetForRetry(f.Lease(T), ResetOperation, retained.Receipt.Plan, confirmation));
        f.Git.Write("a.txt", "changed\n", ready.Checkout);
        var blocked = Assert.IsType<RetryReset.Blocked>(f.Materializer().ResetForRetry(f.Lease(T), ResetOperation, retained.Receipt.Plan, confirmation));
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
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal(detached ? new[] { "81ddb7c330112c7f16700ed002803a04b0bce693", "2f1d113f78fb3fe0c4c6d9ad1d7dc2788acecf67" } :
            new[] { "81ddb7c330112c7f16700ed002803a04b0bce693" }, GitFixture.Read(f.Git.Open().ReadCommit(retained.Commit)).Parents.Select(parent => parent.Hex));
        Assert.Equal("unfinished\n", f.Git.Git("show", retained.Commit.Hex + ":new.txt"));
        Assert.Equal("approved\n", f.Git.Git("show", retained.Commit.Hex + ":plan.txt"));
        var reset = Assert.IsType<RetryReset.Reset>(f.Materializer().ResetForRetry(f.Lease(T), ResetOperation, retained.Receipt.Plan, f.Op()));
        Assert.Equal(Target, reset.Target.Hex);
        Assert.Equal("refs/heads/idp/93f23689/task/90d5b0a2\n", f.Git.Run(ready.Checkout, "symbolic-ref", "HEAD").Text);
        Assert.Equal(Target, GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch))?.Hex);
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.False(File.Exists(Path.Combine(ready.Checkout, "new.txt")));
        Assert.Equal(reset, f.Materializer().ResetForRetry(f.Lease(T), ResetOperation, retained.Receipt.Plan, f.Op()));
    }

    [Fact]
    public async Task Retry_preserves_the_branch_when_it_is_registered_at_another_checkout()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await DivergedWriter(f, true);
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
        f.Git.Git("worktree", "add", ".worktrees/foreign", "idp/93f23689/task/90d5b0a2");
        var blocked = Assert.IsType<RetryReset.Blocked>(f.Materializer().ResetForRetry(f.Lease(T), ResetOperation, retained.Receipt.Plan, f.Op()));
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
        var first = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
        f.Git.Write("new.txt", "changed\n", ready.Checkout);
        var second = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), f.Op(), ready.Execution.Launch.Attempt));
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
            if (terminal) Assert.IsType<RunDecision.Recorded>(fixture.Store.Settle(fixture.Permit, fixture.Op(), RunOutcome.Stopped));
            return writer;
        }
        var steps = new List<string>();
        using (var baseline = new PreparationFixture(FixtureWorkflow(Writer(T))))
        {
            var writer = await Setup(baseline);
            Assert.IsType<Salvage.Retained>(baseline.Materializer(probe: steps.Add).Salvage(baseline.Lease(writer.Execution.Location.Owner.Task), Operation,
                writer.Execution.Launch.Attempt));
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
                .Salvage(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
            var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
            Assert.Equal(Commit, retained.Commit.Hex);
            Assert.Equal(bytes, File.ReadAllBytes(index));
            Assert.Single(f.Read().Receipts.Values, entry => entry.Event is RunEvent.SalvageRetained);
            Assert.Equal("unfinished\n", f.Git.Git("show", Commit + ":new.txt"));
            Assert.Equal(terminal ? "Stopped" : "Approved", f.Read().Phase.ToString());
            if (terminal)
            {
                var next = ready.Execution.Launch with { Turn = 2 };
                var claim = f.Store.Claim(f.Lease(f.Read().Attempts[next.Attempt].Task), f.Op(), next, f.Read().Inputs[ready.Execution.Inputs], ready.Execution.PromptHash);
                Assert.Equal("RunStopped", Assert.IsType<RunDecision.Rejected>(claim).Reason.Problem.ToString());
            }
            Assert.Equal(retained, Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), Operation,
                ready.Execution.Launch.Attempt)));
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
            var retained = Assert.IsType<Salvage.Retained>(baseline.Materializer().Salvage(baseline.Lease(ready.Execution.Location.Owner.Task), Operation,
                ready.Execution.Launch.Attempt));
            Assert.IsType<RetryReset.Reset>(baseline.Materializer(probe: steps.Add).ResetForRetry(baseline.Lease(T), ResetOperation, retained.Receipt.Plan, baseline.Op()));
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
            var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
            if (!detached) Assert.Equal(ownCommit ? "fafba3f02353ba47a6c4d4f4a26a9dd16ecf023b" : Commit, retained.Commit.Hex);
            Assert.Equal(detached ? new[] { "81ddb7c330112c7f16700ed002803a04b0bce693", "2f1d113f78fb3fe0c4c6d9ad1d7dc2788acecf67" } :
                new[] { ownCommit ? "2f1d113f78fb3fe0c4c6d9ad1d7dc2788acecf67" : Target },
                GitFixture.Read(f.Git.Open().ReadCommit(retained.Commit)).Parents.Select(parent => parent.Hex));
            var confirmation = f.Op();
            Assert.Throws<Crash>(() => f.Materializer(probe: step => { if (step == point) throw new Crash(); })
                .ResetForRetry(f.Lease(T), ResetOperation, retained.Receipt.Plan, confirmation));
            var outcome = f.Materializer().ResetForRetry(f.Lease(T), ResetOperation, retained.Receipt.Plan, confirmation);
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
            Assert.Equal(reset, Assert.IsType<RetryReset.Reset>(f.Materializer().ResetForRetry(f.Lease(T), ResetOperation, retained.Receipt.Plan, confirmation)));
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
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
        var confirmation = f.Op();
        Assert.Throws<Crash>(() => f.Materializer(probe: step => { if (step == point) throw new Crash(); })
            .ResetForRetry(f.Lease(T), ResetOperation, retained.Receipt.Plan, confirmation));
        f.Git.Write("new.txt", "changed\n", ready.Checkout);
        var blocked = Assert.IsType<RetryReset.Blocked>(f.Materializer().ResetForRetry(f.Lease(T), ResetOperation, retained.Receipt.Plan, confirmation));
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
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("ConfirmationRequired", Assert.IsType<RetryReset.Rejected>(f.Materializer().ResetForRetry(f.Lease(T), ResetOperation,
            retained.Receipt.Plan, default)).Reason.Problem.ToString());
        Assert.Equal("InvalidData", Assert.IsType<RetryReset.Rejected>(f.Materializer().ResetForRetry(f.Lease(T), ResetOperation, f.Op(), f.Op())).Reason.Problem.ToString());
        Assert.Equal("LiveWriter", Assert.IsType<RetryReset.Blocked>(f.Materializer(boundary: new UnprovenBoundary()).ResetForRetry(f.Lease(T), ResetOperation,
            retained.Receipt.Plan, f.Op())).Block.Problem.ToString());
        var lease = f.Lease(T);
        f.Release(T);
        var before = f.Read().Sequence;
        using (var held = StandaloneLease.TryTake(f.Git.Folder, T))
        {
            Assert.NotNull(held);
            Assert.Equal("TaskBusy", Assert.IsType<RetryReset.Rejected>(f.Materializer().ResetForRetry(lease, ResetOperation,
                retained.Receipt.Plan, f.Op())).Reason.Problem.ToString());
            Assert.Equal(before, f.Read().Sequence);
        }
        using (var held = f.Git.Open().TakeMutationLock())
            Assert.Equal("JournalBusy", Assert.IsType<RetryReset.Rejected>(f.Materializer().ResetForRetry(f.Lease(T), ResetOperation,
                retained.Receipt.Plan, f.Op())).Reason.Problem.ToString());
        Assert.Equal("modified\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal("unfinished\n", File.ReadAllText(Path.Combine(ready.Checkout, "new.txt")));
        Assert.IsType<RetryReset.Reset>(f.Materializer().ResetForRetry(f.Lease(T), ResetOperation, retained.Receipt.Plan, f.Op()));
        Assert.IsType<RunDecision.Recorded>(f.Store.Settle(f.Permit, f.Op(), RunOutcome.Stopped));
        Assert.Equal("RunStopped", Assert.IsType<RetryReset.Rejected>(f.Materializer().ResetForRetry(f.Lease(T), ResetOperation,
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
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
        Assert.Throws<Crash>(() => f.Materializer(probe: step => { if (step == "git.retry-reset.after") throw new Crash(); })
            .ResetForRetry(f.Lease(T), ResetOperation, retained.Receipt.Plan, f.Op()));
        var reset = Assert.IsType<RetryReset.Reset>(f.Materializer().ResetForRetry(f.Lease(T), ResetOperation, retained.Receipt.Plan, f.Op()));
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
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
        Assert.Throws<Crash>(() => f.Materializer(probe: step => { if (step == "journal.retry-reset-observed.after") throw new Crash(); })
            .ResetForRetry(f.Lease(T), ResetOperation, retained.Receipt.Plan, f.Op()));
        f.Git.Write("a.txt", "later tracked\n", ready.Checkout);
        f.Git.Write("later.txt", "later untracked\n", ready.Checkout);
        var reset = Assert.IsType<RetryReset.Reset>(f.Materializer().ResetForRetry(f.Lease(T), ResetOperation, retained.Receipt.Plan, f.Op()));
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
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
        var blocked = Assert.IsType<RetryReset.Blocked>(f.Materializer().ResetForRetry(f.Lease(T), ResetOperation, retained.Receipt.Plan, f.Op()));
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
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
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
        if (phase == "StopRequested") Assert.IsType<RunDecision.Recorded>(f.Store.Stop(f.Permit, f.Op()));
        else if (phase == "Abandoned") Assert.IsType<RunDecision.Recorded>(f.Store.Abandon(f.Permit, f.Op(), f.Op(), "Preserve work."));
        else if (Enum.TryParse<RunOutcome>(phase, out var outcome)) Assert.IsType<RunDecision.Recorded>(f.Store.Settle(f.Permit, f.Op(), outcome));
        f.Git.Write("a.txt", "modified\n", checkout);
        f.Git.Write("new.txt", "unfinished\n", checkout);
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(T), Operation, A1));
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
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal(Commit, retained.Commit.Hex);
        Assert.False(f.Read().Closures.ContainsKey(ready.Execution.Launch.Attempt));
        Assert.Empty(f.Read().Results);
        Assert.Equal("unfinished\n", f.Git.Git("show", Commit + ":new.txt"));
        Assert.Equal("OutcomeMismatch", Assert.IsType<Publication.Rejected>(f.Materializer().Publish(f.Lease(ready.Execution.Location.Owner.Task), f.Op(),
            ready.Execution.Launch.Attempt))
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
                .Salvage(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt)).Block.Problem.ToString());
        }
        Assert.Throws<Crash>(() => f.Materializer(boundary: resolution ? null : new UnprovenBoundary(), probe: step =>
        {
            if (resolution ? step.StartsWith("journal.maintenance-resolve-", StringComparison.Ordinal) && step.EndsWith("." + side, StringComparison.Ordinal)
                : step == "journal.salvage-quiescence-blocked." + side) throw new Crash();
        }).Salvage(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal(Commit, retained.Commit.Hex);
        Assert.Single(f.Read().Salvages);
        Assert.All(f.Read().Blocks.Values, block => Assert.True(block.Resolved));
        Assert.Equal("unfinished\n", File.ReadAllText(Path.Combine(ready.Checkout, "new.txt")));
        var reset = Assert.IsType<RetryReset.Blocked>(f.Materializer(boundary: new UnprovenBoundary()).ResetForRetry(f.Lease(T), ResetOperation, retained.Receipt.Plan, f.Op()));
        Assert.Equal("LiveWriter", reset.Block.Problem.ToString());
        Assert.Throws<Crash>(() => f.Materializer(probe: step =>
        {
            if (step.StartsWith("journal.maintenance-resolve-", StringComparison.Ordinal) && step.EndsWith("." + side, StringComparison.Ordinal)) throw new Crash();
        }).ResetForRetry(f.Lease(T), ResetOperation, retained.Receipt.Plan, f.Op()));
        Assert.Equal(Target, Assert.IsType<RetryReset.Reset>(f.Materializer().ResetForRetry(f.Lease(T), ResetOperation, retained.Receipt.Plan, f.Op())).Target.Hex);
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
        var blocked = Assert.IsType<Salvage.Blocked>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Equal("foreign lock\n", File.ReadAllText(foreignLock));
        Assert.Equal("unfinished\n", File.ReadAllText(Path.Combine(ready.Checkout, "new.txt")));
        File.WriteAllBytes(gitFile, original);
        File.SetAttributes(gitFile, attributes);
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
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
        var lease = f.Lease(T);
        var before = f.Read().Sequence;
        if (mode == "task-lock") f.Release(T);
        using (var held = mode == "task-lock" ? StandaloneLease.TryTake(f.Git.Folder, T) : null)
        {
            if (mode == "ownership") f.Git.Git("worktree", "unlock", ready.Execution.Location.Owner.RelativePath);
            var result = f.Materializer(boundary: mode == "boundary" ? new UnprovenBoundary() : null)
                .Salvage(lease, Operation, ready.Execution.Launch.Attempt);
            if (mode == "task-lock")
            {
                Assert.NotNull(held);
                Assert.Equal("TaskBusy", Assert.IsType<Salvage.Rejected>(result).Reason.Problem.ToString());
                Assert.Equal(before, f.Read().Sequence);
            }
            else Assert.Equal(mode == "ownership" ? "UncertainOwnership" : "LiveWriter", Assert.IsType<Salvage.Blocked>(result).Block.Problem.ToString());
            Assert.Equal("stale\n", File.ReadAllText(indexLock));
        }
        if (mode == "ownership") f.Git.Git("worktree", "lock", "--reason", "idevelop 93f23689/90d5b0a2", ready.Execution.Location.Owner.RelativePath);
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal(Commit, retained.Commit.Hex);
        Assert.False(File.Exists(indexLock));
        Assert.True(File.Exists(Path.Combine(DataFolder.Attempts(f.Git.Folder), T.ToString(), "run.lock")));
        Assert.Equal("unfinished\n", File.ReadAllText(Path.Combine(ready.Checkout, "new.txt")));
    }

    private static string? Ref(PreparationFixture f, string name) => GitFixture.Read(f.Git.Open().ReadRef(name))?.Hex;
    private static string Head(PreparationFixture f, string checkout) => f.Git.Run(checkout, "rev-parse", "HEAD").Text.Trim();
    private static bool Reachable(PreparationFixture f, string commit) => f.Git.Git("for-each-ref", "--contains", commit, "--format=%(refname)").Trim().Length != 0;
    private static void CommitFile(PreparationFixture f, string checkout, string file, string text)
    {
        f.Git.Write(file, text, checkout);
        Assert.Equal(0, f.Git.Run(checkout, "add", file).ExitCode);
        Assert.Equal(0, f.Git.Run(checkout, "-c", "commit.gpgSign=false", "commit", "-qm", file).ExitCode);
    }
    private const string PlanCommit = "81ddb7c330112c7f16700ed002803a04b0bce693";

    [Theory]
    [InlineData("detached")]
    [InlineData("rewound")]
    [InlineData("side-branch")]
    public async Task Salvage_keeps_a_branch_commit_that_head_does_not_contain_reachable_through_retry(string mode)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        CommitFile(f, ready.Checkout, "b.txt", "B\n");
        var branchCommit = Head(f, ready.Checkout);
        Assert.Equal(0, (mode switch
        {
            "detached" => f.Git.Run(ready.Checkout, "checkout", "-q", "--detach", "HEAD~1"),
            "rewound" => f.Git.Run(ready.Checkout, "reset", "-q", "--hard", "HEAD~1"),
            _ => f.Git.Run(ready.Checkout, "checkout", "-q", "-b", "side", "HEAD~1"),
        }).ExitCode);
        f.Git.Write("new.txt", "unfinished\n", ready.Checkout);
        f.Close(ready, outcome: TerminalAttemptOutcome.Failed);
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), f.Op(), ready.Execution.Launch.Attempt));
        Assert.Equal("unfinished\n", f.Git.Git("show", retained.Commit.Hex + ":new.txt"));
        Assert.Equal("3292b7a2adf71a45a545bbfb04cefc7d663bbe93", branchCommit);
        Assert.Equal(mode == "rewound" ? "76e7826be3fb35d52157bac1d88e8f6a188758e6" : "33893cedcad25a1a43d30ed215d4813b66e66853", retained.Commit.Hex);
        var plan = Assert.IsType<MaterializationPlan.Salvage>(f.Read().Plans[retained.Receipt.Plan]);
        Assert.Equal(mode == "rewound" ? "adfe40b30c176fb407933286f51d15ea9b54cdc3" : "3292b7a2adf71a45a545bbfb04cefc7d663bbe93", plan.BranchTip?.Hex);
        var parents = GitFixture.Read(f.Git.Open().ReadCommit(retained.Commit)).Parents.Select(parent => parent.Hex);
        Assert.Equal(mode == "rewound" ? new[] { "adfe40b30c176fb407933286f51d15ea9b54cdc3" } :
            new[] { "adfe40b30c176fb407933286f51d15ea9b54cdc3", "3292b7a2adf71a45a545bbfb04cefc7d663bbe93" }, parents);
        Assert.Equal(mode != "rewound", Reachable(f, branchCommit));
        Assert.IsType<RetryReset.Reset>(f.Materializer().ResetForRetry(f.Lease(T), f.Op(), retained.Receipt.Plan, f.Op()));
        Assert.Equal("refs/heads/idp/93f23689/task/90d5b0a2\n", f.Git.Run(ready.Checkout, "symbolic-ref", "HEAD").Text);
        Assert.Equal(mode != "rewound", Reachable(f, branchCommit));
        if (mode == "side-branch") Assert.Equal(Target, Ref(f, "refs/heads/side"));
    }

    [Fact]
    public async Task A_plain_git_status_after_salvage_does_not_block_retry()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await SalvageTests.FailedWriter(f);
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), f.Op(), ready.Execution.Launch.Attempt));
        var index = GitFixture.Read(f.Git.Open().IndexPath(ready.Checkout));
        var before = File.ReadAllBytes(index);
        File.SetLastWriteTimeUtc(Path.Combine(ready.Checkout, "plan.txt"), DateTime.UtcNow.AddMinutes(1));
        var status = new System.Diagnostics.ProcessStartInfo("git", ["status", "--short"])
            { WorkingDirectory = ready.Checkout, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var (key, value) in f.Git.Environment.Where(pair => pair.Key != "GIT_OPTIONAL_LOCKS")) status.Environment[key] = value;
        using (var process = System.Diagnostics.Process.Start(status)!)
        {
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();
        }
        Assert.False(before.SequenceEqual(File.ReadAllBytes(index)));
        var reset = Assert.IsType<RetryReset.Reset>(f.Materializer().ResetForRetry(f.Lease(T), f.Op(), retained.Receipt.Plan, f.Op()));
        Assert.Equal(Target, reset.Target.Hex);
    }

    [Fact]
    public async Task A_foreign_head_after_a_recorded_attach_blocks_retry_and_keeps_the_foreign_branch()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await SalvageTests.FailedWriter(f);
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), f.Op(), ready.Execution.Launch.Attempt));
        var operation = f.Op();
        var confirmation = f.Op();
        Assert.Throws<Crash>(() => f.Materializer(probe: step => { if (step == "journal.retry-attach-observed.after") throw new Crash(); })
            .ResetForRetry(f.Lease(T), operation, retained.Receipt.Plan, confirmation));
        f.Git.Git("branch", "foreign", PlanCommit);
        Assert.Equal(0, f.Git.Run(ready.Checkout, "symbolic-ref", "HEAD", "refs/heads/foreign").ExitCode);
        var blocked = Assert.IsType<RetryReset.Blocked>(f.Materializer().ResetForRetry(f.Lease(T), operation, retained.Receipt.Plan, confirmation));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Equal("The retry HEAD moved outside the recorded attachment.", blocked.Block.Detail);
        Assert.Equal(PlanCommit, Ref(f, "refs/heads/foreign"));
        Assert.Equal("unfinished\n", File.ReadAllText(Path.Combine(ready.Checkout, "new.txt")));
    }

    [Fact]
    public async Task A_branch_moved_after_salvage_blocks_retry_and_stays_reachable()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await SalvageTests.FailedWriter(f);
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), f.Op(), ready.Execution.Launch.Attempt));
        var fresh = f.Git.Run(f.Git.Folder, "commit-tree", Target + "^{tree}", "-p", Target, "-m", "out of band").Text.Trim();
        Assert.Equal("8d14445e6226129e327b4f1f807c957d99479399", fresh);
        f.Git.Git("update-ref", ready.Execution.Location.Owner.Branch, fresh);
        var outcome = f.Materializer().ResetForRetry(f.Lease(T), f.Op(), retained.Receipt.Plan, f.Op());
        Assert.True(Reachable(f, fresh), outcome.ToString());
        var blocked = Assert.IsType<RetryReset.Blocked>(outcome);
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Equal("The retry branch moved outside the recorded reset.", blocked.Block.Detail);
        Assert.Equal("8d14445e6226129e327b4f1f807c957d99479399", Ref(f, ready.Execution.Location.Owner.Branch));
        Assert.Equal("unfinished\n", File.ReadAllText(Path.Combine(ready.Checkout, "new.txt")));
    }

    [Fact]
    public async Task A_foreign_head_after_an_unrecorded_attach_blocks_retry()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await SalvageTests.FailedWriter(f);
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), f.Op(), ready.Execution.Launch.Attempt));
        var operation = f.Op();
        var confirmation = f.Op();
        Assert.Throws<Crash>(() => f.Materializer(probe: step => { if (step == "git.retry-attach.after") throw new Crash(); })
            .ResetForRetry(f.Lease(T), operation, retained.Receipt.Plan, confirmation));
        f.Git.Git("branch", "foreign", PlanCommit);
        Assert.Equal(0, f.Git.Run(ready.Checkout, "symbolic-ref", "HEAD", "refs/heads/foreign").ExitCode);
        var blocked = Assert.IsType<RetryReset.Blocked>(f.Materializer().ResetForRetry(f.Lease(T), operation, retained.Receipt.Plan, confirmation));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Equal("The retry HEAD moved outside the recorded attachment.", blocked.Block.Detail);
        Assert.Equal(PlanCommit, Ref(f, "refs/heads/foreign"));
        Assert.Equal("unfinished\n", File.ReadAllText(Path.Combine(ready.Checkout, "new.txt")));
    }

    [Fact]
    public async Task A_salvage_ref_created_out_of_band_at_its_target_blocks_salvage()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await SalvageTests.FailedWriter(f);
        var operation = f.Op();
        Assert.Throws<Crash>(() => f.Materializer(probe: step => { if (step == "journal.salvage-plan.after") throw new Crash(); })
            .Salvage(f.Lease(ready.Execution.Location.Owner.Task), operation, ready.Execution.Launch.Attempt));
        var plan = Assert.Single(f.Read().Plans.Values.OfType<MaterializationPlan.Salvage>());
        f.Git.Git("update-ref", plan.Ref, plan.Commit.Hex);
        var blocked = Assert.IsType<Salvage.Blocked>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), operation, ready.Execution.Launch.Attempt));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Equal("Publication ref refs/idp/93f23689/salvage/90d5b0a2/00000000-0000-0000-0000-000000000102 has unexpected value 0dbf5cbc9310ef3abbc28073142652917c69dc2f.", blocked.Block.Detail);
        Assert.Equal(0, f.Read().Receipts.Values.Count(entry => entry.Event is RunEvent.SalvageRetained));
    }

    [Fact]
    public async Task A_branch_rewound_after_salvage_to_a_retained_ancestor_blocks_retry()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await SalvageTests.FailedWriter(f, ownCommit: true);
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), f.Op(), ready.Execution.Launch.Attempt));
        f.Git.Git("update-ref", ready.Execution.Location.Owner.Branch, Target);
        var blocked = Assert.IsType<RetryReset.Blocked>(f.Materializer().ResetForRetry(f.Lease(T), f.Op(), retained.Receipt.Plan, f.Op()));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Equal("The retry branch moved outside the recorded reset.", blocked.Block.Detail);
        Assert.Equal("unfinished\n", File.ReadAllText(Path.Combine(ready.Checkout, "new.txt")));
    }

    [Fact]
    public async Task A_foreign_head_after_an_observed_reset_blocks_cleanup()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await SalvageTests.FailedWriter(f);
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), f.Op(), ready.Execution.Launch.Attempt));
        var operation = f.Op();
        var confirmation = f.Op();
        Assert.Throws<Crash>(() => f.Materializer(probe: step => { if (step == "journal.retry-reset-observed.after") throw new Crash(); })
            .ResetForRetry(f.Lease(T), operation, retained.Receipt.Plan, confirmation));
        f.Git.Git("branch", "foreign", PlanCommit);
        Assert.Equal(0, f.Git.Run(ready.Checkout, "symbolic-ref", "HEAD", "refs/heads/foreign").ExitCode);
        var blocked = Assert.IsType<RetryReset.Blocked>(f.Materializer().ResetForRetry(f.Lease(T), operation, retained.Receipt.Plan, confirmation));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Equal("The reset HEAD or task branch differs from its recorded target.", blocked.Block.Detail);
        Assert.Equal(PlanCommit, Ref(f, "refs/heads/foreign"));
        Assert.Equal("unfinished\n", File.ReadAllText(Path.Combine(ready.Checkout, "new.txt")));
    }

    [Theory]
    [InlineData("git.retry-attach.before", "The retry HEAD moved outside the recorded attachment.")]
    [InlineData("git.retry-reset.before", "The reset HEAD or task branch differs from its recorded target.")]
    [InlineData("git.retry-remove-new.txt.before", "The reset HEAD or task branch differs from its recorded target.")]
    public async Task Live_head_is_checked_before_each_retry_mutation(string point, string detail)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await FailedWriter(f);
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
        f.Git.Git("branch", "foreign", "81ddb7c330112c7f16700ed002803a04b0bce693");
        var blocked = Assert.IsType<RetryReset.Blocked>(f.Materializer(probe: step =>
        {
            if (step == point) Assert.Equal(0, f.Git.Run(ready.Checkout, "symbolic-ref", "HEAD", "refs/heads/foreign").ExitCode);
        }).ResetForRetry(f.Lease(T), ResetOperation, retained.Receipt.Plan, f.Op()));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Equal(detail, blocked.Block.Detail);
        Assert.Equal("81ddb7c330112c7f16700ed002803a04b0bce693", Ref(f, "refs/heads/foreign"));
        Assert.Equal("unfinished\n", File.ReadAllText(Path.Combine(ready.Checkout, "new.txt")));
    }

    [Fact]
    public async Task Same_capture_with_a_new_operation_uses_a_new_retention_ref()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await FailedWriter(f);
        var first = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
        var second = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), f.Op(), ready.Execution.Launch.Attempt));
        Assert.Equal("0dbf5cbc9310ef3abbc28073142652917c69dc2f", first.Commit.Hex);
        Assert.Equal("0dbf5cbc9310ef3abbc28073142652917c69dc2f", second.Commit.Hex);
        Assert.Equal("refs/idp/93f23689/resalvage/90d5b0a2/00000000-0000-0000-0000-000000000102/00000000-0000-0000-0000-000000001005", second.Receipt.Ref);
        Assert.Equal("0dbf5cbc9310ef3abbc28073142652917c69dc2f", Ref(f, first.Receipt.Ref));
        Assert.Equal("unfinished\n", File.ReadAllText(Path.Combine(ready.Checkout, "new.txt")));
        Assert.Equal(2, f.Read().Salvages.Count);
    }

    [Fact]
    public async Task Salvage_records_an_absent_branch_and_retry_creates_it_by_cas()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await FailedWriter(f, ownCommit: true);
        Assert.Equal(0, f.Git.Run(ready.Checkout, "checkout", "--detach", "HEAD").ExitCode);
        f.Git.Git("update-ref", "-d", ready.Execution.Location.Owner.Branch);
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("fafba3f02353ba47a6c4d4f4a26a9dd16ecf023b", retained.Commit.Hex);
        Assert.Null(Assert.IsType<MaterializationPlan.Salvage>(f.Read().Plans[retained.Receipt.Plan]).BranchTip);
        Assert.Equal(new[] { "2f1d113f78fb3fe0c4c6d9ad1d7dc2788acecf67" }, GitFixture.Read(f.Git.Open().ReadCommit(retained.Commit)).Parents.Select(parent => parent.Hex));
        var reset = Assert.IsType<RetryReset.Reset>(f.Materializer().ResetForRetry(f.Lease(T), ResetOperation, retained.Receipt.Plan, f.Op()));
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", reset.Target.Hex);
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", Ref(f, ready.Execution.Location.Owner.Branch));
        Assert.Equal("refs/heads/idp/93f23689/task/90d5b0a2", GitFixture.Read(f.Git.Open().SymbolicHead(ready.Checkout)));
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal("unfinished\n", f.Git.Git("show", retained.Commit.Hex + ":new.txt"));
    }
}
