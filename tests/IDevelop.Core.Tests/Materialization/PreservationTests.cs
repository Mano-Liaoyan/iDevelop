using System.Text;
using IDevelop.Core.Tests.Git;
using IDevelop.Execution;
using IDevelop.TestSupport;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Materialization;

public sealed class PreservationTests
{
    private static readonly OperationId Operation = new(Id(2000));
    private const string CleanCommit = "3ccf3357ae3c273f9aa79b45e263663f02975c76";
    private sealed class Crash : Exception;

    [Theory]
    [InlineData("salvage")]
    [InlineData("preserve")]
    public async Task Changing_preservation_inventory_blocks_every_move(string label)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("changing.txt", "before\n", ready.Checkout);
        await f.Close(ready, outcome: TerminalAttemptOutcome.Failed);
        var branch = GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch));
        var materializer = f.Materializer(probe: point =>
        {
            if (point == "git." + label + "-observe-1.after") f.Git.Write("changing.txt", "changing\n", ready.Checkout);
        });
        var block = label == "salvage"
            ? Assert.IsType<Salvage.Blocked>(await materializer.Salvage(f.Lease(T), Operation, ready.Execution.Launch.Attempt)).Block
            : Assert.IsType<Preservation.Blocked>(await materializer.Preserve(f.Lease(T), Operation, ready.Execution.Launch.Attempt)).Block;
        Assert.Equal("DirtyWorktree", block.Problem.ToString());
        Assert.Equal("The checkout changed between preservation observations.", block.Detail);
        Assert.Equal(new[] { "changing.txt" }, block.Scope!.Paths);
        var pins = Pins(f, ready, Operation);
        Assert.Equal("before\n", f.Git.Git("show", pins[0] + ":changing.txt"));
        Assert.Equal("changing\n", f.Git.Git("show", pins[1] + ":changing.txt"));
        Assert.Empty(f.Read().Plans.Values.OfType<MaterializationPlan.Salvage>());
        Assert.Empty(f.Read().Plans.Values.OfType<MaterializationPlan.Preservation>());
        if (label == "salvage")
        {
            var reset = Assert.IsType<RetryReset.Rejected>(f.Materializer().ResetForRetry(f.Lease(T), f.Op(),
                OperationIds.Derive(Operation, "salvage-plan"), f.Op()));
            Assert.Equal("InvalidData", reset.Reason.Problem.ToString());
            Assert.Equal(branch, GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch)));
        }
        Assert.Equal("changing\n", File.ReadAllText(Path.Combine(ready.Checkout, "changing.txt")));
        var sequence = f.Read().Sequence;
        var replay = label == "salvage"
            ? Assert.IsType<Salvage.Blocked>(await materializer.Salvage(f.Lease(T), Operation, ready.Execution.Launch.Attempt)).Block
            : Assert.IsType<Preservation.Blocked>(await materializer.Preserve(f.Lease(T), Operation, ready.Execution.Launch.Attempt)).Block;
        Assert.True(RunReducer.Same(block, replay));
        Assert.Equal(sequence, f.Read().Sequence);
        Assert.Equal(pins, Pins(f, ready, Operation));
        if (label == "salvage")
        {
            var retained = Assert.IsType<Salvage.Retained>(await f.Materializer().Salvage(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt));
            Assert.Equal("changing\n", f.Git.Git("show", retained.Commit.Hex + ":changing.txt"));
            Assert.Single(f.Read().Plans.Values.OfType<MaterializationPlan.Salvage>());
        }
        else
        {
            var retained = Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt));
            Assert.Equal("changing\n", f.Git.Git("show", retained.Commit.Hex + ":changing.txt"));
            Assert.Single(f.Read().Plans.Values.OfType<MaterializationPlan.Preservation>());
        }
    }

    [Fact]
    public async Task Preservation_retains_the_exact_inventory()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await f.Close(ready);
        f.Git.Write("a.txt", "late\n", ready.Checkout);
        f.Git.Write("new.txt", "new\n", ready.Checkout);
        StageOnly(f, ready);
        File.AppendAllText(f.Git.PathOf(".git/info/exclude"), "build/\n");
        f.Git.Write("build/out.bin", "ignored\n", ready.Checkout);
        f.Git.Write(RunLayout.Outbox(ready.Execution.Launch.Attempt) + "/report.md", "B ready.\n", ready.Checkout);
        f.Git.Write(RunLayout.Outbox(ready.Execution.Launch.Attempt) + "/nested/raw.bin", "raw\n", ready.Checkout);
        var preserved = Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
        var plan = Assert.IsType<MaterializationPlan.Preservation>(f.Read().Plans[preserved.Receipt.Plan]);
        Assert.Equal(new[] { "a.txt", "new.txt", "plan.txt", "root.txt" }, TreePaths(f, preserved.Receipt.Ref));
        Assert.Equal("late\n", f.Git.Git("show", preserved.Commit.Hex + ":a.txt"));
        Assert.Equal("new\n", f.Git.Git("show", preserved.Commit.Hex + ":new.txt"));
        var parents = GitFixture.Read(f.Git.Open().ReadCommit(preserved.Commit)).Parents;
        Assert.Equal(2, parents.Length);
        Assert.Equal("staged\n", f.Git.Git("show", parents[1].Hex + ":s.txt"));
        Assert.Equal(new[] { "a.txt", "plan.txt", "root.txt", "s.txt" }, TreePaths(f, parents[1].Hex));
        Assert.All(parents.Append(preserved.Commit), commit => Assert.DoesNotContain("build/out.bin", TreePaths(f, commit.Hex)));
        Assert.Equal("ignored\n", File.ReadAllText(Path.Combine(ready.Checkout, "build/out.bin")));
        Assert.Equal(new[] { "new.txt" }, plan.Preserved.Untracked.Select(file => file.RelativePath));
        Assert.Equal(new[] { "nested/raw.bin", "report.md" }, plan.Outbox.Select(file => file.Name));
        Assert.Equal("B ready.\n", Stored(f, Assert.Single(plan.Outbox, file => file.Name == "report.md")));
        Assert.Equal("raw\n", Stored(f, Assert.Single(plan.Outbox, file => file.Name == "nested/raw.bin")));
    }

    [Theory]
    [InlineData("pinned")]
    [InlineData("recipe")]
    [InlineData("pruned")]
    public async Task A_preservation_plan_recorded_before_its_ref_reuses_the_pinned_commit(string mode)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await f.Close(ready);
        StageOnly(f, ready);
        await Assert.ThrowsAsync<Crash>(async () => await f.Materializer(probe: point =>
        {
            if (point == "journal.preserve-ref-intent.before") throw new Crash();
        }).Preserve(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
        var plan = Assert.Single(f.Read().Plans.Values.OfType<MaterializationPlan.Preservation>());
        Assert.Equal(2, plan.Recipe.Parents.Length);
        Assert.Empty(f.Read().Preservations);
        var names = PinNames(f, ready, Operation);
        if (mode != "pinned") foreach (var name in names) f.Git.Git("update-ref", "-d", name);
        if (mode == "pruned")
        {
            Assert.Equal(0, f.Git.Run(ready.Checkout, "read-tree", "HEAD").ExitCode);
            f.Git.Git("-c", "gc.reflogExpire=now", "-c", "gc.reflogExpireUnreachable=now", "gc", "--prune=now");
            Assert.NotEqual(0, f.Git.Run(f.Git.Folder, "cat-file", "-e", plan.Commit.Hex).ExitCode);
            Assert.NotEqual(0, f.Git.Run(f.Git.Folder, "cat-file", "-e", plan.Recipe.Parents[1].Hex).ExitCode);
            var blocked = Assert.IsType<Preservation.Blocked>(await f.Materializer().Preserve(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
            Assert.Equal("InputUnavailable", blocked.Block.Problem.ToString());
            Assert.Equal("The preservation commit is missing. Preserve again.", blocked.Block.Detail);
            Assert.Null(GitFixture.Read(f.Git.Open().ReadRef(plan.Ref)));
            var control = Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt));
            Assert.Equal(control.Commit, GitFixture.Read(f.Git.Open().ReadRef(control.Receipt.Ref)));
        }
        else
        {
            f.Git.Write("later.txt", "later\n", ready.Checkout);
            var preserved = Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
            Assert.Equal(plan.Commit, preserved.Commit);
            Assert.Equal(plan.Commit, GitFixture.Read(f.Git.Open().ReadRef(plan.Ref)));
            Assert.Single(f.Read().Preservations);
            Assert.DoesNotContain("later.txt", TreePaths(f, preserved.Commit.Hex));
            Assert.Equal("later\n", File.ReadAllText(Path.Combine(ready.Checkout, "later.txt")));
            Assert.Equal("staged\n", f.Git.Git("show", plan.Recipe.Parents[1].Hex + ":s.txt"));
        }
    }

    [Fact]
    public async Task Preservation_retains_conflict_stages()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await f.Close(ready);
        var entries = new StringBuilder();
        var expected = new[] { "base\n", "ours\n", "theirs\n" };
        for (var stage = 1; stage <= 3; stage++)
        {
            f.Git.Write("blob.txt", expected[stage - 1], ready.Checkout);
            var blob = f.Git.Run(ready.Checkout, "hash-object", "-w", "blob.txt").Text.Trim();
            entries.Append($"100644 {blob} {stage}\tc.txt\n");
        }
        File.Delete(Path.Combine(ready.Checkout, "blob.txt"));
        var start = new System.Diagnostics.ProcessStartInfo("git", ["update-index", "--index-info"])
        { WorkingDirectory = ready.Checkout, RedirectStandardInput = true, RedirectStandardError = true };
        foreach (var (key, value) in f.Git.Environment) start.Environment[key] = value;
        using (var process = System.Diagnostics.Process.Start(start)!)
        {
            process.StandardInput.Write(entries.ToString());
            process.StandardInput.Close();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, error);
        }
        f.Git.Write("c.txt", "conflicted\n", ready.Checkout);
        var preserved = Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
        var parents = GitFixture.Read(f.Git.Open().ReadCommit(preserved.Commit)).Parents;
        var stages = parents[^1];
        Assert.Equal(new[] { "stages/1/c.txt", "stages/2/c.txt", "stages/3/c.txt" }, TreePaths(f, stages.Hex));
        Assert.Equal("base\n", f.Git.Git("show", stages.Hex + ":stages/1/c.txt"));
        Assert.Equal("ours\n", f.Git.Git("show", stages.Hex + ":stages/2/c.txt"));
        Assert.Equal("theirs\n", f.Git.Git("show", stages.Hex + ":stages/3/c.txt"));
        Assert.Equal("conflicted\n", f.Git.Git("show", preserved.Commit.Hex + ":c.txt"));
        Assert.Equal(3, GitFixture.Read(f.Git.Open().UnmergedEntries(ready.Checkout)).Length);
    }

    [Fact]
    public async Task Existing_index_lock()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await f.Close(ready);
        var lockPath = GitFixture.Read(f.Git.Open().IndexPath(ready.Checkout)) + ".lock";
        File.WriteAllText(lockPath, "lock\n");
        var blocked = Assert.IsType<Salvage.Blocked>(await f.Materializer().Salvage(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt));
        Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
        Assert.Equal("lock\n", File.ReadAllText(lockPath));
        Assert.Equal(0, f.Read().GitIntents.Values.Count(intent => intent.Mutation.GetType().Name == "RemoveIndexLock"));
        var preserved = Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
        var plan = Assert.IsType<MaterializationPlan.Preservation>(f.Read().Plans[preserved.Receipt.Plan]);
        var evidence = plan.Preserved.IndexLock!;
        Assert.Equal("lock\n", Stored(f, evidence.Bytes));
        Assert.Equal("lock\n", File.ReadAllText(lockPath));
        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
        {
            Assert.NotNull(evidence.Identity);
            Assert.Equal(FileIdentities.ReadFile(lockPath)!.Value.Identity, evidence.Identity);
        }
    }

    [Fact]
    public async Task Replacing_an_index_lock_with_identical_bytes_is_a_divergence()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await f.Close(ready);
        var lockPath = GitFixture.Read(f.Git.Open().IndexPath(ready.Checkout)) + ".lock";
        File.WriteAllText(lockPath, "lock\n");
        var replacement = lockPath + ".replacement";
        File.WriteAllText(replacement, "lock\n");
        var blocked = Assert.IsType<Preservation.Blocked>(await f.Materializer(probe: point =>
        {
            if (point == "git.preserve-observe-1.after") File.Move(replacement, lockPath, overwrite: true);
        }).Preserve(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
        Assert.True(blocked.Block.Scope!.IndexLock);
        Assert.Empty(blocked.Block.Scope.Paths);
        var divergence = f.Read().PreservationDivergences[Operation];
        Assert.Equal(divergence.First.State.IndexLock!.Bytes.Content, divergence.Second.State.IndexLock!.Bytes.Content);
        Assert.NotEqual(divergence.First.State.IndexLock.Identity, divergence.Second.State.IndexLock.Identity);
        Assert.Equal("lock\n", File.ReadAllText(lockPath));
        var preserved = Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt));
        Assert.Equal("lock\n", Stored(f, Assert.IsType<MaterializationPlan.Preservation>(f.Read().Plans[preserved.Receipt.Plan]).Preserved.IndexLock!.Bytes));
    }

    [Fact]
    public async Task The_second_observation_waits_250_ms_after_the_first_completes()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await f.Close(ready);
        var clock = new ManualTimeProvider();
        var second = false;
        DateTimeOffset? completed = null;
        DateTimeOffset? started = null;
        var command = f.Materializer(clock: clock, probe: point =>
        {
            if (point == "git.preserve-observe-1.after") completed = clock.GetUtcNow();
            if (point == "git.preserve-observe-2.before") { second = true; started = clock.GetUtcNow(); }
        }).Preserve(f.Lease(T), Operation, ready.Execution.Launch.Attempt);
        Assert.NotNull(completed);
        Assert.False(command.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(249));
        Assert.False(second);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var preserved = Assert.IsType<Preservation.Preserved>(await command);
        Assert.True(second);
        Assert.Equal(TimeSpan.FromMilliseconds(250), started - completed);
        Assert.Equal(preserved.Commit, GitFixture.Read(f.Git.Open().ReadRef(preserved.Receipt.Ref)));
    }

    [Fact]
    public async Task Every_preserve_probe_converges()
    {
        var steps = new List<string>();
        using (var baseline = new PreparationFixture(FixtureWorkflow(Writer(T))))
        {
            var ready = Assert.IsType<Preparation.Ready>(await baseline.Prepare(T));
            await baseline.Close(ready);
            var receipt = Assert.IsType<Preservation.Preserved>(await baseline.Materializer(probe: steps.Add)
                .Preserve(baseline.Lease(T), Operation, ready.Execution.Launch.Attempt));
            Assert.Equal(CleanCommit, receipt.Commit.Hex);
        }
        Assert.Contains("journal.preserve-ref-intent.before", steps);
        Assert.Contains("journal.preserved.after", steps);
        Assert.Contains("git.preserve-observe-2.after", steps);
        foreach (var point in steps.Distinct())
        {
            using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
            var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
            await f.Close(ready);
            await Assert.ThrowsAsync<Crash>(async () => await f.Materializer(probe: step => { if (point == step) throw new Crash(); })
                .Preserve(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
            var preserved = Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
            Assert.Equal(CleanCommit, preserved.Commit.Hex);
            Assert.Single(f.Read().Preservations);
            Assert.Single(f.Read().Receipts.Values, receipt => receipt.Event is RunEvent.Preserved);
            Assert.Single(f.Read().Plans.Values.OfType<MaterializationPlan.Preservation>());
            Assert.Equal("A\n", f.Git.Git("show", preserved.Commit.Hex + ":a.txt"));
            var sequence = f.Read().Sequence;
            Assert.Equal(preserved, await f.Materializer().Preserve(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
            Assert.Equal(sequence, f.Read().Sequence);
        }
    }

    [Fact]
    public async Task A_fenced_claim_can_be_preserved()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        Assert.IsType<RunDecision.Granted>(f.Store.Claim(f.Lease(T), f.Op(), ready.Execution.Launch,
            f.Read().Inputs[ready.Execution.Inputs], ready.Execution.PromptHash));
        f.Git.Write("lost.txt", "lost\n", ready.Checkout);
        f.ReleaseControl();
        _ = f.Permit;
        Assert.Contains(ready.Execution.Launch, f.Read().Fenced);
        Assert.Empty(f.Read().RootExits);
        var preserved = Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("lost\n", f.Git.Git("show", preserved.Commit.Hex + ":lost.txt"));
        var recovery = Assert.Single(Assert.IsType<RecoveryRead.Loaded>(f.Store.InspectRecovery(W, f.RunId)).Attempts);
        Assert.Equal("Uncertain", recovery.State.ToString());
        Assert.Equal(new[] { ready.Execution.Launch }, recovery.Claims);
        Assert.IsType<RunDecision.Recorded>(f.Store.Recover(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt,
            RecoveryOutcome.Stopped, f.Op(), "Stopped."));
        Assert.Equal("Closed", Assert.Single(Assert.IsType<RecoveryRead.Loaded>(f.Store.InspectRecovery(W, f.RunId)).Attempts).State.ToString());
    }

    [Fact]
    public async Task Preserved_rejects_a_commit_that_differs_from_its_plan()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await f.Close(ready);
        await Assert.ThrowsAsync<Crash>(async () => await f.Materializer(probe: point =>
        {
            if (point == "journal.preserved.before") throw new Crash();
        }).Preserve(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
        var planId = OperationIds.Derive(Operation, "preserve-plan");
        var plan = Assert.IsType<MaterializationPlan.Preservation>(f.Read().Plans[planId]);
        var wrongMove = f.Op();
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, wrongMove,
            new RunEvent.GitIntended(planId, new GitMutation.MoveRef(new(plan.Ref, null, f.A)))));
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, f.Op(),
            new RunEvent.GitObserved(wrongMove, new(false, f.A.Hex))));
        var rejected = Assert.IsType<RunDecision.Rejected>(f.Store.Record(f.Permit, f.Op(), new RunEvent.Preserved(planId, plan.Ref, f.A)));
        Assert.Equal("InvalidData", rejected.Reason.Problem.ToString());
        Assert.Empty(f.Read().Preservations);
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, OperationIds.Derive(Operation, "preserved"),
            new RunEvent.Preserved(planId, plan.Ref, plan.Commit)));
        var receipt = Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal(CleanCommit, receipt.Commit.Hex);
        Assert.Single(f.Read().Preservations);
    }

    [Fact]
    public async Task A_pinned_commit_must_match_the_persisted_recipe()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await f.Close(ready);
        var original = Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt));
        var source = Assert.IsType<MaterializationPlan.Preservation>(f.Read().Plans[original.Receipt.Plan]);
        var plan = source with
        {
            Recipe = source.Recipe with { Parents = [source.Commit] },
            Ref = RunLayout.PreserveRef(f.Read().RunKey!, f.Read().TaskKeys[T], Operation),
        };
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, OperationIds.Derive(Operation, "preserve-plan"), new RunEvent.Planned(plan)));
        foreach (var name in PinNames(f, ready, Operation)) f.Git.Git("update-ref", name, source.Commit.Hex);
        var blocked = Assert.IsType<Preservation.Blocked>(await f.Materializer().Preserve(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("InputUnavailable", blocked.Block.Problem.ToString());
        Assert.Equal("The preservation commit is missing. Preserve again.", blocked.Block.Detail);
        Assert.Null(GitFixture.Read(f.Git.Open().ReadRef(plan.Ref)));
        Assert.Equal(source.Commit, GitFixture.Read(f.Git.Open().ReadRef(original.Receipt.Ref)));
        var control = Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt));
        Assert.Equal(control.Commit, GitFixture.Read(f.Git.Open().ReadRef(control.Receipt.Ref)));
    }

    private static void StageOnly(PreparationFixture f, Preparation.Ready ready)
    {
        f.Git.Write("s.txt", "staged\n", ready.Checkout);
        Assert.Equal(0, f.Git.Run(ready.Checkout, "add", "s.txt").ExitCode);
        File.Delete(Path.Combine(ready.Checkout, "s.txt"));
    }

    private static string[] TreePaths(PreparationFixture f, string reference) =>
        f.Git.Git("ls-tree", "-r", "--name-only", reference).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static string Stored(PreparationFixture f, ArtifactRecord file) =>
        Stored(f, new EvidenceFile(file.StoredPath, file.Content, file.ByteLength));

    private static string Stored(PreparationFixture f, EvidenceFile file) => Encoding.UTF8.GetString(RunStorage.Read(
        new RunStorage(f.Git.Folder, W, f.RunId).Folder, file.RelativePath, file.Content, file.ByteLength));

    private static string[] PinNames(PreparationFixture f, Preparation.Ready ready, OperationId operation) =>
        Enumerable.Range(1, 2).Select(ordinal => RunLayout.PreservationPin(f.Read().RunKey!, f.Read().TaskKeys[T],
            ready.Execution.Launch.Attempt, operation, ordinal)).ToArray();

    private static string[] Pins(PreparationFixture f, Preparation.Ready ready, OperationId operation) =>
        PinNames(f, ready, operation).Select(name => GitFixture.Read(f.Git.Open().ReadRef(name))!.Value.Hex).ToArray();
}
