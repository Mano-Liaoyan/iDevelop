using IDevelop.Core.Tests.Git;
using IDevelop.Execution;
using IDevelop.Projects;
using IDevelop.TestSupport;
using static IDevelop.Core.Tests.Runs.RunFixtures;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;

namespace IDevelop.Core.Tests.Materialization;

public sealed class PublicationTests
{
    private static readonly OperationId Operation = new(Id(2000));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publish_captures_edits_hidden_by_a_writer_fsmonitor_hook(bool untrackedCache)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)), configureBase: git =>
        {
            git.Write("a/inside.txt", "inside\n");
            return git.Commit("layout");
        });
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        var hook = Path.Combine(Path.GetDirectoryName(f.Git.Folder)!, "fsmonitor-hook");
        Executable.Write(hook, "#!/bin/sh\nprintf 'token\\0'\n");
        var environment = new Dictionary<string, string>(f.Git.Environment) { ["GIT_OPTIONAL_LOCKS"] = "1" };
        Assert.Equal(0, f.Git.Run(ready.Checkout, environment, "config", "core.fsmonitor", hook).ExitCode);
        if (untrackedCache) Assert.Equal(0, f.Git.Run(ready.Checkout, environment, "config", "core.untrackedCache", "true").ExitCode);
        Assert.Equal(0, f.Git.Run(ready.Checkout, environment, "status", "--porcelain").ExitCode);
        f.Git.Write("a/inside.txt", "edited\n", ready.Checkout);
        f.Git.Write("a/new.txt", "new\n", ready.Checkout);
        Assert.Equal(0, f.Git.Run(ready.Checkout, environment, "status", "--porcelain").ExitCode);
        await f.Close(ready);
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(ready.Execution.Location.Owner.Task), f.Op(), ready.Execution.Launch.Attempt));
        var commit = Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex;
        Assert.Equal("edited\n", f.Git.Git("show", commit + ":a/inside.txt"));
        Assert.Equal("new\n", f.Git.Git("show", commit + ":a/new.txt"));
    }

    [Fact]
    public async Task Publish_captures_a_same_size_edit_hidden_by_relaxed_stat_checks()
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
        await f.Close(ready);
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(ready.Execution.Location.Owner.Task), f.Op(), ready.Execution.Launch.Attempt));
        var commit = Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex;
        Assert.Equal("INSIDE\n", f.Git.Git("show", commit + ":a/inside.txt"));
    }

    [Theory]
    [InlineData("--assume-unchanged", true)]
    [InlineData("--skip-worktree", true)]
    [InlineData("--assume-unchanged", false)]
    [InlineData("--skip-worktree", false)]
    public async Task Hidden_index_entries_block_publication_and_preserve_writer_bytes(string flag, bool ownCommit)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = ownCommit ? await ChangedWriter(f) : Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        if (!ownCommit)
        {
            f.Git.Write("a.txt", "A captured\n", ready.Checkout);
            await f.Close(ready);
        }
        Assert.Equal(0, f.Git.Run(ready.Checkout, "update-index", flag, "a.txt").ExitCode);
        var index = GitFixture.Read(f.Git.Open().IndexPath(ready.Checkout));
        var bytes = File.ReadAllBytes(index);
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
        Assert.Equal("The index hides changes to a.txt with assume-unchanged or skip-worktree.", blocked.Block.Detail);
        Assert.Equal("A captured\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        if (ownCommit) Assert.Equal("new\n", File.ReadAllText(Path.Combine(ready.Checkout, "new.txt")));
        Assert.Equal(bytes, File.ReadAllBytes(index));
        Assert.Equal(ownCommit ? "2f1d113f78fb3fe0c4c6d9ad1d7dc2788acecf67" : "adfe40b30c176fb407933286f51d15ea9b54cdc3",
            GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch))?.Hex);
        Assert.Equal("A\n", f.Git.Git("show", "adfe40b30c176fb407933286f51d15ea9b54cdc3:a.txt"));
        Assert.Equal(blocked, f.Materializer().Publish(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
    }

    [Fact]
    public async Task A_write_after_settlement_blocks_without_moving_the_branch_or_index_and_can_be_repaired()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("result.txt", "done\n", ready.Checkout);
        await f.Close(ready);
        var repository = f.Git.Open();
        var tip = GitFixture.Read(repository.ReadRef(ready.Execution.Location.Owner.Branch));
        var index = GitFixture.Read(repository.IndexPath(ready.Checkout));
        var bytes = File.ReadAllBytes(index);
        f.Git.Write("result.txt", "late\n", ready.Checkout);
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
        Assert.Equal("Writer files or index changed after the turn-end capture.", blocked.Block.Detail);
        Assert.Equal("late\n", File.ReadAllText(Path.Combine(ready.Checkout, "result.txt")));
        Assert.Equal(tip, GitFixture.Read(repository.ReadRef(ready.Execution.Location.Owner.Branch)));
        Assert.Equal(bytes, File.ReadAllBytes(index));
        Assert.Empty(f.Read().Results);
        f.Git.Write("result.txt", "done\n", ready.Checkout);
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("done\n", f.Git.Git("show", Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex + ":result.txt"));
        Assert.Empty(f.Materializer().Inspect(W, f.RunId, T)!.Blocks);
        Assert.Equal("Publication verified.", Assert.Single(f.Read().Receipts.Values.Select(e => e.Event).OfType<RunEvent.BlockResolved>()).Reason);
    }

    [Theory]
    [InlineData(".idp/outbox/00000000-0000-0000-0000-000000000102/manifest.json", "Tracked execution data: .idp/outbox/00000000-0000-0000-0000-000000000102/manifest.json")]
    [InlineData(".idp/inputs/forced.txt", "Tracked execution data: .idp/inputs/forced.txt")]
    [InlineData(".worktrees/forced.txt", "Tracked execution data: .worktrees/forced.txt")]
    public async Task Force_staged_execution_data_blocks_publication_before_capture(string path, string detail)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write(path, "{\"schema\":1,\"artifacts\":[]}", ready.Checkout);
        Assert.Equal(0, f.Git.Run(ready.Checkout, "add", "-f", "--", path).ExitCode);
        await f.Close(ready, assertMatched: false);
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
        Assert.Equal(detail, blocked.Block.Detail);
        var repository = f.Git.Open();
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", GitFixture.Read(repository.ReadRef(ready.Execution.Location.Owner.Branch))?.Hex);
        Assert.Null(GitFixture.Read(repository.ReadRef(RunLayout.ResultRef(f.Read().RunKey!, f.Read().TaskKeys[T], ready.Execution.Launch.Attempt))));
        Assert.Empty(f.Read().Results);
        Assert.Equal(blocked, f.Materializer().Publish(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
    }

    [Fact]
    public async Task Own_branch_commit_edits_and_additions_publish_exact_code_and_align_the_real_index()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await ChangedWriter(f);
        var published = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("f7d21fe0-9370-801e-a122-38820dfbc203", published.Result.Id.Value.ToString("D"));
        var code = Assert.IsType<CodeOutput.Produced>(published.Result.Code).Code;
        Assert.Equal(T, code.Owner);
        Assert.Equal("81cae59086bf9597301026b65f1bb57380b74686", code.Commit.Hex);
        Assert.Equal("435dc9f6defa1902ff86e44aca393b16fdf82718", code.Tree.Hex);
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", code.AttemptBase.Hex);
        Assert.Equal("81cae59086bf9597301026b65f1bb57380b74686", GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch))?.Hex);
        Assert.Equal("81cae59086bf9597301026b65f1bb57380b74686", GitFixture.Read(f.Git.Open().ReadRef(code.ResultRef))?.Hex);
        Assert.Equal("", f.Git.Run(ready.Checkout, "status", "--porcelain").Text);
        Assert.Equal("435dc9f6defa1902ff86e44aca393b16fdf82718", GitFixture.Read(f.Git.Open().ReadIndexTree(ready.Checkout)).Hex);
        Assert.Equal("A captured\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal("new\n", File.ReadAllText(Path.Combine(ready.Checkout, "new.txt")));
        Assert.Equal("B ready.\n", published.Result.Report);
        Assert.Equal(new[] { "2f1d113f78fb3fe0c4c6d9ad1d7dc2788acecf67" }, GitFixture.Read(f.Git.Open().ReadCommit(code.Commit)).Parents.Select(p => p.Hex));
        File.WriteAllText(Path.Combine(ready.Checkout, "new.txt"), "later\n");
        Assert.Equal(published, Assert.IsType<Publication.Accepted>(f.Materializer()
            .Publish(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt)));
        Assert.Single(f.Read().Receipts.Values, entry => entry.Event is RunEvent.ResultAccepted);
    }

    [Fact]
    public async Task A_late_file_is_preserved_and_blocks_acceptance_then_the_same_plan_can_be_verified()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await ChangedWriter(f);
        var late = Path.Combine(ready.Checkout, "late.txt");
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer(probe: step =>
        {
            if (step == "git.branch.after") File.WriteAllText(late, "late\n");
        }).Publish(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
        Assert.Equal(OperationIds.Derive(Operation, "plan"), blocked.Block.Operation);
        Assert.Empty(f.Read().Results);
        Assert.Equal("late\n", File.ReadAllText(late));
        Assert.Equal("81cae59086bf9597301026b65f1bb57380b74686", GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch))?.Hex);
        File.Delete(late);
        var published = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("81cae59086bf9597301026b65f1bb57380b74686", Assert.IsType<CodeOutput.Produced>(published.Result.Code).Code.Commit.Hex);
        Assert.True(Assert.Single(f.Read().Blocks).Value.Resolved);
        Assert.Equal("Publication verified.", Assert.Single(f.Read().Receipts.Values.Select(e => e.Event).OfType<RunEvent.BlockResolved>()).Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publication_requires_a_capture_of_the_final_turn(bool secondTurn)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T) with
        { Conversation = secondTurn ? IDevelop.Workflows.ConversationMode.Chat : IDevelop.Workflows.ConversationMode.Autonomous }));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("result.txt", "done\n", ready.Checkout);
        var checkpoint = f.ObserveAndLog(ready);
        if (secondTurn)
            Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(T), f.Op(), ready.Execution.Launch, checkpoint));
        else
            Assert.IsType<RunDecision.Recorded>(f.Store.CloseTurn(f.Permit, f.Op(), ready.Execution.Launch, checkpoint));
        if (secondTurn)
        {
            ready = Assert.IsType<Preparation.Ready>(await f.Materializer().PrepareTurn(f.Lease(T), f.Op(), new(ready.Execution.Launch.Attempt, 2), "Continue."));
            RootExitTests.Claim(f, ready);
            Assert.IsType<RootObservation.Observed>(f.Materializer().ObserveRootExit(f.Lease(T), f.Op(), ready.Execution.Launch, new RootExit.Exited(0)));
            var folder = f.Store.AttemptFolder(W, f.RunId, T, ready.Execution.Launch.Attempt);
            using (var log = AttemptLog.Open(folder))
            {
                log.Append(new AttemptEvent.TurnRequested(At, "Continue.", "codex", []) { Conversation = IDevelop.Workflows.ConversationMode.Autonomous });
                log.Append(new AttemptEvent.Agent(At, new AgentEvent.Succeeded("B ready.\n")));
                log.Append(new AttemptEvent.Exited(At, 0, ""));
            }
            checkpoint = Checkpoint(folder);
            Assert.IsType<RunDecision.Recorded>(f.Store.CloseTurn(f.Permit, f.Op(), ready.Execution.Launch, checkpoint));
        }
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), ready.Execution.Launch.Attempt, TerminalAttemptOutcome.Succeeded, checkpoint));
        var journal = Path.Combine(new RunStorage(f.Git.Folder, W, f.RunId).Folder, "events.jsonl");
        var bytes = File.ReadAllBytes(journal);
        Assert.Equal("OutcomeMismatch", Assert.IsType<Publication.Rejected>(f.Materializer().Publish(f.Lease(T), Operation, ready.Execution.Launch.Attempt)).Reason.Problem.ToString());
        Assert.Empty(f.Read().Results);
        Assert.Equal(bytes, File.ReadAllBytes(journal));
        using var control = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var valid = Assert.IsType<Preparation.Ready>(await control.Prepare(T));
        control.Git.Write("result.txt", "done\n", valid.Checkout);
        await control.Close(valid);
        var accepted = Assert.IsType<Publication.Accepted>(control.Materializer().Publish(control.Lease(T), Operation, valid.Execution.Launch.Attempt));
        Assert.Equal("done\n", control.Git.Git("show", Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex + ":result.txt"));
        Assert.Single(control.Read().Results);
    }

    [Fact]
    public async Task Publication_requires_the_task_lock_and_accepts_after_its_release()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await ChangedWriter(f);
        var lease = f.Lease(T);
        f.Release(T);
        var before = f.Read().Sequence;
        using (var held = StandaloneLease.TryTake(f.Git.Folder, T))
        {
            Assert.NotNull(held);
            Assert.Equal("TaskBusy", Assert.IsType<Publication.Rejected>(f.Materializer().Publish(lease, Operation, ready.Execution.Launch.Attempt)).Reason.Problem.ToString());
            Assert.Equal(before, f.Read().Sequence);
            Assert.Empty(f.Read().Results);
        }
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("new\n", f.Git.Git("show", Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex + ":new.txt"));
        Assert.Single(f.Read().Results);
    }

    [Fact]
    public async Task Preconditions_reject_read_only_and_unclosed_or_failed_attempts()
    {
        using var reader = new PreparationFixture(FixtureWorkflow(Task()));
        var read = Assert.IsType<Preparation.Ready>(await reader.Prepare(T));
        Assert.Equal("UnsupportedResult", Assert.IsType<Publication.Rejected>(reader.Materializer().Publish(reader.Lease(read.Execution.Location.Owner.Task), Operation,
            read.Execution.Launch.Attempt)).Reason.Problem.ToString());
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        Assert.Equal("OutcomeMismatch", Assert.IsType<Publication.Rejected>(f.Materializer().Publish(f.Lease(ready.Execution.Location.Owner.Task), Operation,
            ready.Execution.Launch.Attempt)).Reason.Problem.ToString());
        await f.Close(ready, outcome: TerminalAttemptOutcome.Failed);
        Assert.Equal("OutcomeMismatch", Assert.IsType<Publication.Rejected>(f.Materializer().Publish(f.Lease(ready.Execution.Location.Owner.Task), Operation,
            ready.Execution.Launch.Attempt)).Reason.Problem.ToString());
        Assert.Equal("UnknownAttempt", Assert.IsType<Publication.Rejected>(f.Materializer().Publish(f.Lease(T), Operation, new(Id(999)))).Reason.Problem.ToString());
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
    }

    [Fact]
    public async Task Every_emitted_publication_probe_converges_on_the_same_literal_result_and_commit()
    {
        var steps = new List<string>();
        using (var baseline = new PreparationFixture(FixtureWorkflow(Writer(T))))
        {
            var ready = await ChangedWriter(baseline, artifact: true);
            File.WriteAllBytes(Path.Combine(ready.Checkout, ready.Execution.OutboxPath, "payload.bin"), [88]);
            Assert.IsType<Publication.Accepted>(baseline.Materializer(probe: steps.Add).Publish(baseline.Lease(ready.Execution.Location.Owner.Task), Operation,
                ready.Execution.Launch.Attempt));
        }
        Assert.Contains("journal.accepted.after", steps);
        Assert.Contains("git.align-index.after", steps);
        Assert.Contains("artifact.payload.after", steps);
        foreach (var point in steps.Distinct())
        {
            using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
            var ready = await ChangedWriter(f, artifact: true);
            File.WriteAllBytes(Path.Combine(ready.Checkout, ready.Execution.OutboxPath, "payload.bin"), [88]);
            Assert.Throws<Crash>(() => f.Materializer(probe: step => { if (step == point) throw new Crash(); })
                .Publish(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
            var published = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
            Assert.Equal("f7d21fe0-9370-801e-a122-38820dfbc203", published.Result.Id.Value.ToString("D"));
            Assert.Equal(RunJournal.Canonical(published.Result), RunJournal.Canonical(Assert.IsType<Publication.Accepted>(
                f.Materializer().Publish(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt)).Result));
            Assert.Equal("81cae59086bf9597301026b65f1bb57380b74686", Assert.IsType<CodeOutput.Produced>(published.Result.Code).Code.Commit.Hex);
            Assert.Single(f.Read().Receipts.Values, entry => entry.Event is RunEvent.ResultAccepted);
            Assert.Equal("", f.Git.Run(ready.Checkout, "status", "--porcelain").Text);
            Assert.Equal(new byte[] { 67, 0, 127 }, new RunStorage(f.Git.Folder, W, f.RunId).ReadArtifact(published.Result.Id, published.Result.Artifacts.Single()));
        }
    }

    [Fact]
    public async Task Unexpected_index_after_the_plan_is_preserved_and_never_overwritten()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await ChangedWriter(f);
        byte[]? changed = null;
        var index = GitFixture.Read(f.Git.Open().IndexPath(ready.Checkout));
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer(probe: step =>
        {
            if (step != "journal.plan.after") return;
            Assert.Equal(0, f.Git.Run(ready.Checkout, "update-index", "--force-remove", "a.txt").ExitCode);
            changed = File.ReadAllBytes(index);
        }).Publish(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
        Assert.Equal(changed, File.ReadAllBytes(index));
        Assert.Equal("A captured\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Empty(f.Read().Results);
        Assert.Equal("DirtyWorktree", Assert.IsType<Publication.Blocked>(f.Materializer().Publish(f.Lease(ready.Execution.Location.Owner.Task), Operation,
            ready.Execution.Launch.Attempt)).Block.Problem.ToString());
        Assert.Equal(changed, File.ReadAllBytes(index));
    }

    [Fact]
    public async Task An_invalid_index_after_alignment_blocks_as_dirty_and_keeps_the_unexpected_bytes()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await ChangedWriter(f);
        var index = GitFixture.Read(f.Git.Open().IndexPath(ready.Checkout));
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer(probe: step =>
        {
            if (step == "git.align-index.after") File.WriteAllBytes(index, [68, 73, 82, 67]);
        }).Publish(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
        Assert.Equal(new byte[] { 68, 73, 82, 67 }, File.ReadAllBytes(index));
        Assert.Equal("new\n", File.ReadAllText(Path.Combine(ready.Checkout, "new.txt")));
        Assert.Empty(f.Read().Results);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("after")]
    public async Task Interrupted_block_recording_retains_the_plan_and_verifies_it_after_the_late_file_is_removed(string side)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await ChangedWriter(f);
        var late = Path.Combine(ready.Checkout, "late.txt");
        Assert.Throws<Crash>(() => f.Materializer(probe: step =>
        {
            if (step == "git.branch.after") File.WriteAllText(late, "late\n");
            if (step == "journal.verify-blocked." + side) throw new Crash();
        }).Publish(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("late\n", File.ReadAllText(late));
        Assert.Empty(f.Read().Results);
        File.Delete(late);
        var published = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("81cae59086bf9597301026b65f1bb57380b74686", Assert.IsType<CodeOutput.Produced>(published.Result.Code).Code.Commit.Hex);
        Assert.Single(f.Read().Results);
        Assert.All(f.Read().Blocks.Values, block => Assert.True(block.Resolved));
    }

    [Theory]
    [InlineData("before")]
    [InlineData("after")]
    public async Task Lost_block_resolution_acknowledgement_keeps_one_acceptance_and_resolves_once(string side)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await ChangedWriter(f);
        var late = Path.Combine(ready.Checkout, "late.txt");
        Assert.Equal("DirtyWorktree", Assert.IsType<Publication.Blocked>(f.Materializer(probe: step =>
        {
            if (step == "git.branch.after") File.WriteAllText(late, "late\n");
        }).Publish(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt)).Block.Problem.ToString());
        File.Delete(late);
        Assert.Throws<Crash>(() => f.Materializer(probe: step =>
        {
            if (step.StartsWith("journal.resolve-", StringComparison.Ordinal) && step.EndsWith("." + side, StringComparison.Ordinal)) throw new Crash();
        }).Publish(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
        var published = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("f7d21fe0-9370-801e-a122-38820dfbc203", published.Result.Id.Value.ToString("D"));
        Assert.Equal("81cae59086bf9597301026b65f1bb57380b74686", Assert.IsType<CodeOutput.Produced>(published.Result.Code).Code.Commit.Hex);
        Assert.Single(f.Read().Receipts.Values, entry => entry.Event is RunEvent.ResultAccepted);
        Assert.Single(f.Read().Receipts.Values, entry => entry.Event is RunEvent.BlockResolved);
        Assert.True(Assert.Single(f.Read().Blocks).Value.Resolved);
    }

    [Fact]
    public async Task Duplicate_publication_returns_the_original_receipt_and_leaves_a_later_block_unresolved()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        Assert.IsType<RunDecision.Granted>(f.Store.Claim(f.Lease(T), f.Op(), ready.Execution.Launch,
            f.Read().Inputs[ready.Execution.Inputs], ready.Execution.PromptHash));
        f.Git.Write("result.txt", "done\n", ready.Checkout);
        await f.Close(ready);
        var original = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
        File.WriteAllText(Path.Combine(ready.Checkout, "result.txt"), "late\n");
        var plan = Assert.Single(f.Read().Plans, pair => pair.Value is MaterializationPlan.Publication).Key;
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, f.Op(), new RunEvent.Blocked(new(plan, T,
            ready.Execution.Launch.Attempt, MaterializationProblem.DirtyWorktree, ready.Execution.Inputs, [], "Later writer drift."))));
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal(original, accepted);
        Assert.Equal(original.Result, accepted.Result);
        Assert.Equal("done\n", f.Git.Git("show", Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex + ":result.txt"));
        Assert.Equal("late\n", File.ReadAllText(Path.Combine(ready.Checkout, "result.txt")));
        Assert.Single(f.Read().Results);
        Assert.False(Assert.Single(f.Read().Blocks).Value.Resolved);
        Assert.Equal(0, f.Read().Receipts.Values.Count(entry => entry.Event is RunEvent.BlockResolved));
    }

    [Fact]
    public async Task A_diverged_capture_blocks_once_with_its_changed_paths_and_a_matching_control_publishes()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("result.txt", "first\n", ready.Checkout);
        var log = f.ObserveAndLog(ready);
        var settled = Assert.IsType<Settlement.Closed>(await f.Materializer(probe: step =>
        {
            if (step == "journal.capture-1.after") f.Git.Write("result.txt", "second\n", ready.Checkout);
        }).Settle(f.Lease(T), f.Op(), ready.Execution.Launch, log));
        Assert.Equal("DirtyWorktree", Assert.IsType<CaptureDisposition.Diverged>(settled.Disposition).Problem.ToString());
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), ready.Execution.Launch.Attempt, TerminalAttemptOutcome.Succeeded, log));
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
        Assert.Contains("result.txt", blocked.Block.Detail);
        Assert.Equal(blocked, f.Materializer().Publish(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
        Assert.Single(f.Read().Blocks);
        Assert.Equal(0, f.Read().Plans.Values.Count(plan => plan is MaterializationPlan.Publication));
        Assert.Empty(f.Read().Results);
        using var control = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var valid = Assert.IsType<Preparation.Ready>(await control.Prepare(T));
        control.Git.Write("result.txt", "done\n", valid.Checkout);
        await control.Close(valid);
        var accepted = Assert.IsType<Publication.Accepted>(control.Materializer().Publish(control.Lease(T), Operation, valid.Execution.Launch.Attempt));
        Assert.Equal("done\n", control.Git.Git("show", Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex + ":result.txt"));
    }

    internal static async Task<Preparation.Ready> ChangedWriter(PreparationFixture f, bool artifact = false, bool assertMatched = true)
    {
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("b.txt", "B\n", ready.Checkout);
        Assert.Equal(0, f.Git.Run(ready.Checkout, "add", "b.txt").ExitCode);
        Assert.Equal(0, f.Git.Run(ready.Checkout, "-c", "commit.gpgSign=false", "commit", "-q", "-m", "b").ExitCode);
        Assert.Equal("2f1d113f78fb3fe0c4c6d9ad1d7dc2788acecf67\n", f.Git.Run(ready.Checkout, "rev-parse", "HEAD").Text);
        f.Git.Write("a.txt", "A captured\n", ready.Checkout);
        f.Git.Write("new.txt", "new\n", ready.Checkout);
        if (artifact)
        {
            var outbox = Path.Combine(ready.Checkout, ready.Execution.OutboxPath);
            File.WriteAllText(Path.Combine(outbox, "manifest.json"), "{\"schema\":1,\"artifacts\":[{\"name\":\"payload\",\"path\":\"payload.bin\"}]}");
            File.WriteAllBytes(Path.Combine(outbox, "payload.bin"), [67, 0, 127]);
        }
        await f.Close(ready, assertMatched: assertMatched);
        return ready;
    }

    internal sealed class Crash : Exception;
}
