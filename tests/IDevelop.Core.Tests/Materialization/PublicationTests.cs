using IDevelop.Core.Tests.Git;
using IDevelop.Execution;
using IDevelop.Projects;
using IDevelop.TestSupport;
using static IDevelop.Core.Tests.Runs.RunFixtures;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;

namespace IDevelop.Core.Tests.Materialization;

public sealed class PublicationTests
{
    [Fact]
    public async Task A_staged_only_change_blocks_before_publication_moves_and_restored_index_bytes_publish()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("result.txt", "done\n", ready.Checkout);
        await f.Close(ready);
        var repository = f.Git.Open();
        var indexPath = GitFixture.Read(repository.IndexPath(ready.Checkout));
        var index = File.ReadAllBytes(indexPath);
        var branch = GitFixture.Read(repository.ReadRef(ready.Execution.Location.Owner.Branch));
        var moves = f.Read().GitIntents.Count;
        Assert.Equal(0, f.Git.Run(ready.Checkout, "add", "result.txt").ExitCode);
        var operation = f.Op();
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(f.Lease(T), operation, ready.Execution.Launch.Attempt));
        Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
        Assert.Equal("Writer files or index changed after the turn-end capture.", blocked.Block.Detail);
        Assert.Equal(0, f.Read().GitIntents.Count - moves);
        Assert.Equal(branch, GitFixture.Read(repository.ReadRef(ready.Execution.Location.Owner.Branch)));
        Assert.Empty(f.Read().Results);
        Assert.Equal("done\n", File.ReadAllText(Path.Combine(ready.Checkout, "result.txt")));
        File.WriteAllBytes(indexPath, index);
        Recheck(f, Assert.Single(f.Read().Blocks).Key);
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), operation, ready.Execution.Launch.Attempt));
        Assert.Equal("done\n", f.Git.Git("show", Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex + ":result.txt"));
        Assert.Equal(3, f.Read().GitIntents.Count - moves);
        Assert.Single(f.Read().Results);
    }

    [Fact]
    public async Task Publication_plans_must_name_the_settled_capture_and_valid_persisted_plans_resume()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("result.txt", "done\n", ready.Checkout);
        await f.Close(ready);
        var record = f.Read();
        var capture = record.Settlements[ready.Execution.Launch];
        var first = record.Captures[capture][0];
        var operation = f.Op();
        var result = new ResultId(OperationIds.Derive(operation, "result").Value);
        var plan = new MaterializationPlan.Publication(ready.Execution.Launch.Attempt, result, null,
            record.RootExits[ready.Execution.Launch].Tip, first.Index?.Content, first.Recipe, first.Candidate, first.Report!, [])
        { Capture = capture };
        Assert.Equal("OutcomeMismatch", Assert.IsType<RunDecision.Rejected>(f.Store.Record(f.Permit, f.Op(),
            new RunEvent.Planned(plan with { Capture = new(Id(9900)) }))).Reason.Problem.ToString());
        Assert.Empty(f.Read().Plans.Values.OfType<MaterializationPlan.Publication>());
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, OperationIds.Derive(operation, "plan"), new RunEvent.Planned(plan)));
        Assert.Single(f.Read().Plans.Values.OfType<MaterializationPlan.Publication>());
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), operation, ready.Execution.Launch.Attempt));
        Assert.Equal("done\n", f.Git.Git("show", Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex + ":result.txt"));
        Assert.Single(f.Read().Results);
    }

    [Fact]
    public async Task Publication_rejects_a_capture_report_mismatch_before_copying_result_artifacts()
    {
        using var source = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var sourceReady = Assert.IsType<Preparation.Ready>(await source.Prepare(T));
        source.Git.Write("result.txt", "done\n", sourceReady.Checkout);
        var outbox = Path.Combine(sourceReady.Checkout, sourceReady.Execution.OutboxPath);
        File.WriteAllText(Path.Combine(outbox, "manifest.json"), "{\"schema\":1,\"artifacts\":[{\"name\":\"payload\",\"path\":\"payload.bin\"}]}");
        File.WriteAllBytes(Path.Combine(outbox, "payload.bin"), [67, 0, 127]);
        await source.Close(sourceReady);
        var pair = Assert.Single(source.Read().Captures).Value;
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("result.txt", "done\n", ready.Checkout);
        var log = f.ObserveAndLog(ready);
        var storage = new RunStorage(f.Git.Folder, W, f.RunId);
        foreach (var observation in pair)
        {
            Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, f.Op(), new RunEvent.TurnCaptured(
                observation with { Launch = ready.Execution.Launch, Log = log, Report = "Different report.\n" })));
            var artifact = Assert.Single(observation.Artifacts);
            RunStorage.Publish(storage.Folder, artifact.StoredPath, [67, 0, 127], artifact.Content, artifact.ByteLength);
        }
        var capture = pair[0].Capture;
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, f.Op(), new RunEvent.CaptureDisposed(
            capture, ready.Execution.Launch, new CaptureDisposition.Matched())));
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseTurn(f.Permit, f.Op(), ready.Execution.Launch, log, capture));
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), ready.Execution.Launch.Attempt, TerminalAttemptOutcome.Succeeded, log));
        var operation = f.Op();
        var result = new ResultId(OperationIds.Derive(operation, "result").Value);
        var destination = RunStorage.SafePath(storage.Folder, RunStorage.ArtifactPath(result, "payload"));
        Assert.Equal("OutcomeMismatch", Assert.IsType<Publication.Rejected>(f.Materializer().Publish(
            f.Lease(T), operation, ready.Execution.Launch.Attempt)).Reason.Problem.ToString());
        Assert.False(File.Exists(destination));
        Assert.Empty(f.Read().Results);
        var accepted = Assert.IsType<Publication.Accepted>(source.Materializer().Publish(source.Lease(T), source.Op(), sourceReady.Execution.Launch.Attempt));
        var sourceStorage = new RunStorage(source.Git.Folder, W, source.RunId);
        Assert.Equal(new byte[] { 67, 0, 127 }, sourceStorage.ReadArtifact(accepted.Result.Id, Assert.Single(accepted.Result.Artifacts)));
        Assert.Equal("B ready.\n", accepted.Result.Report);
        Assert.Single(source.Read().Results);
    }

    [Fact]
    public async Task Publication_rechecks_attempt_base_ancestry_when_the_root_tip_becomes_shallow()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await ChangedWriter(f);
        var shallow = Path.Combine(f.Git.Open().CommonDirectory, "shallow");
        File.WriteAllText(shallow, "2f1d113f78fb3fe0c4c6d9ad1d7dc2788acecf67\n");
        var moves = f.Read().GitIntents.Count;
        var operation = f.Op();
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(f.Lease(T), operation, ready.Execution.Launch.Attempt));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Equal("The writer branch tip 2f1d113f78fb3fe0c4c6d9ad1d7dc2788acecf67 does not contain the attempt base adfe40b30c176fb407933286f51d15ea9b54cdc3.", blocked.Block.Detail);
        Assert.Equal(0, f.Read().GitIntents.Count - moves);
        Assert.Empty(f.Read().Results);
        File.Delete(shallow);
        Recheck(f, Assert.Single(f.Read().Blocks).Key);
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), operation, ready.Execution.Launch.Attempt));
        Assert.Equal("new\n", f.Git.Git("show", Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex + ":new.txt"));
        Assert.Equal(3, f.Read().GitIntents.Count - moves);
        Assert.Single(f.Read().Results);
    }

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
        var sequence = f.Read().Sequence;
        Assert.Equal(blocked, f.Materializer().Publish(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal(blocked, f.Materializer().Publish(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt));
        Assert.Equal(sequence, f.Read().Sequence);
        Assert.Empty(f.Read().Results);
        Recheck(f, Assert.Single(f.Read().Blocks).Key);
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("done\n", f.Git.Git("show", Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex + ":result.txt"));
        Assert.Empty(f.Materializer().Inspect(W, f.RunId, T)!.Blocks);
        Assert.Equal("Rechecked by the person.", Assert.Single(f.Read().Receipts.Values.Select(e => e.Event).OfType<RunEvent.BlockResolved>()).Reason);
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
        var sequence = f.Read().Sequence;
        Assert.Equal(blocked, f.Materializer().Publish(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal(blocked, f.Materializer().Publish(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt));
        Assert.Equal(sequence, f.Read().Sequence);
        Assert.Empty(f.Read().Results);
        Recheck(f, Assert.Single(f.Read().Blocks).Key);
        var published = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("81cae59086bf9597301026b65f1bb57380b74686", Assert.IsType<CodeOutput.Produced>(published.Result.Code).Code.Commit.Hex);
        Assert.True(Assert.Single(f.Read().Blocks).Value.Resolved);
        Assert.Equal("Rechecked by the person.", Assert.Single(f.Read().Receipts.Values.Select(e => e.Event).OfType<RunEvent.BlockResolved>()).Reason);
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
            if (step == "journal.index-blocked." + side) throw new Crash();
        }).Publish(f.Lease(ready.Execution.Location.Owner.Task), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("late\n", File.ReadAllText(late));
        Assert.Empty(f.Read().Results);
        File.Delete(late);
        if (side == "after")
        {
            var sequence = f.Read().Sequence;
            Assert.IsType<Publication.Blocked>(f.Materializer().Publish(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
            Assert.Equal(sequence, f.Read().Sequence);
            Recheck(f, Assert.Single(f.Read().Blocks).Key);
        }
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
        var ready = await ChangedWriter(f, artifact: true);
        Assert.Throws<Crash>(() => f.Materializer(probe: step =>
        {
            if (step == "journal.plan.after") throw new Crash();
        }).Publish(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
        var storage = new RunStorage(f.Git.Folder, W, f.RunId);
        var capture = Assert.Single(f.Read().Captures.Values).First();
        var path = RunStorage.SafePath(storage.Folder, Assert.Single(capture.Artifacts).StoredPath);
        File.WriteAllBytes(path, [88]);
        Assert.Equal("InputUnavailable", Assert.IsType<Publication.Blocked>(f.Materializer()
            .Publish(f.Lease(T), Operation, ready.Execution.Launch.Attempt)).Block.Problem.ToString());
        File.WriteAllBytes(path, [67, 0, 127]);
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

    [Fact]
    public async Task A_diverged_capture_returns_the_existing_block_for_a_new_operation_until_recheck()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("result.txt", "first\n", ready.Checkout);
        var log = f.ObserveAndLog(ready);
        var clock = new ManualTimeProvider();
        var settled = Assert.IsType<Settlement.Closed>(await f.Materializer(clock: clock, probe: step =>
        {
            if (step != "journal.capture-1.after") return;
            f.Git.Write("result.txt", "second\n", ready.Checkout);
            clock.Advance(TimeSpan.FromMilliseconds(250));
        }).Settle(f.Lease(T), f.Op(), ready.Execution.Launch, log));
        var diverged = Assert.IsType<CaptureDisposition.Diverged>(settled.Disposition);
        Assert.Equal("DirtyWorktree", diverged.Problem.ToString());
        Assert.Equal(new[] { "result.txt" }, diverged.Paths);
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), ready.Execution.Launch.Attempt, TerminalAttemptOutcome.Succeeded, log));
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
        Assert.Equal("The captures differ in recipe, candidate. Paths or refs: result.txt", blocked.Block.Detail);
        Assert.Equal(Operation, blocked.Block.Operation);
        var firstId = Assert.Single(f.Read().Blocks).Key;
        var sequence = f.Read().Sequence;
        var next = new OperationId(Id(2001));
        Assert.Equal(blocked, f.Materializer().Publish(f.Lease(T), next, ready.Execution.Launch.Attempt));
        Assert.Equal(sequence, f.Read().Sequence);
        Assert.Single(f.Read().Blocks);
        Assert.Empty(f.Read().Results);

        Recheck(f, firstId);
        var reblocked = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(f.Lease(T), next, ready.Execution.Launch.Attempt));
        Assert.Equal("DirtyWorktree", reblocked.Block.Problem.ToString());
        Assert.Equal("The captures differ in recipe, candidate. Paths or refs: result.txt", reblocked.Block.Detail);
        Assert.Equal(next, reblocked.Block.Operation);
        var record = f.Read();
        Assert.Equal(sequence + 2, record.Sequence);
        Assert.Equal(2, record.Blocks.Count);
        Assert.True(record.Blocks[firstId].Resolved);
        Assert.False(record.Blocks[OperationIds.Derive(next, "settlement-blocked")].Resolved);
        Assert.Equal(1, record.Blocks.Values.Count(block => !block.Resolved));
        Assert.Equal("Rechecked by the person.", Assert.Single(record.Receipts.Values.Select(entry => entry.Event).OfType<RunEvent.BlockResolved>()).Reason);
        Assert.Empty(record.Results);

        using var control = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var valid = Assert.IsType<Preparation.Ready>(await control.Prepare(T));
        control.Git.Write("result.txt", "done\n", valid.Checkout);
        await control.Close(valid);
        var accepted = Assert.IsType<Publication.Accepted>(control.Materializer().Publish(control.Lease(T), next, valid.Execution.Launch.Attempt));
        Assert.Equal("done\n", control.Git.Git("show", Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex + ":result.txt"));
        Assert.Single(control.Read().Results);
    }

    [Fact]
    public async Task Drift_recorded_before_the_publication_lock_blocks_moves_until_recheck()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("result.txt", "done\n", ready.Checkout);
        await f.Close(ready);
        var record = f.Read();
        var moves = record.GitIntents.Count;
        var sequence = record.Sequence;
        var branch = ready.Execution.Location.Owner.Branch;
        var drift = new MaterializationBlock(new(Id(2001)), T, ready.Execution.Launch.Attempt,
            MaterializationProblem.DirtyWorktree, ready.Execution.Inputs, [], "Concurrent salvage observed drift.");
        var blockId = new OperationId(Id(9000));
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer(probe: step =>
        {
            if (step == "publish.lock.before")
                Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, blockId, new RunEvent.Blocked(drift)));
        }).Publish(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal(drift, blocked.Block);
        Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
        Assert.Equal("Concurrent salvage observed drift.", blocked.Block.Detail);
        record = f.Read();
        Assert.Equal(sequence + 1, record.Sequence);
        Assert.Equal(0, record.GitIntents.Count - moves);
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", GitFixture.Read(f.Git.Open().ReadRef(branch))?.Hex);
        Assert.Single(record.Blocks);
        Assert.False(record.Blocks[blockId].Resolved);
        Assert.Empty(record.Results);

        Recheck(f, blockId);
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
        var code = Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code;
        Assert.Equal("done\n", f.Git.Git("show", code.Commit.Hex + ":result.txt"));
        Assert.Equal(code.Commit, GitFixture.Read(f.Git.Open().ReadRef(branch)));
        record = f.Read();
        Assert.Equal(3, record.GitIntents.Count - moves);
        Assert.Single(record.Results);
        Assert.True(record.Blocks[blockId].Resolved);
        Assert.Equal("Rechecked by the person.", Assert.Single(record.Receipts.Values.Select(entry => entry.Event).OfType<RunEvent.BlockResolved>()).Reason);
    }

    [Theory]
    [InlineData("DirtyWorktree")]
    [InlineData("UncertainOwnership")]
    public async Task Drift_from_other_operations_returns_the_earliest_block_until_each_is_rechecked(string problem)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("result.txt", "done\n", ready.Checkout);
        await f.Close(ready);
        var first = new MaterializationBlock(f.Op(), T, ready.Execution.Launch.Attempt,
            Enum.Parse<MaterializationProblem>(problem), ready.Execution.Inputs, [], "First observed drift.");
        var firstId = new OperationId(Id(9000));
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, firstId, new RunEvent.Blocked(first)));
        var second = first with { Operation = f.Op(), Detail = "Second observed drift." };
        var secondId = new OperationId(Id(8000));
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, secondId, new RunEvent.Blocked(second)));
        var sequence = f.Read().Sequence;
        var moves = f.Read().GitIntents.Count;
        Assert.Equal(new Publication.Blocked(first), f.Materializer().Publish(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal(new Publication.Blocked(first), f.Materializer().Publish(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt));
        Assert.Equal(sequence, f.Read().Sequence);
        Assert.Equal(0, f.Read().GitIntents.Count - moves);
        Assert.Empty(f.Read().Results);
        Recheck(f, firstId);
        Assert.Equal(new Publication.Blocked(second), f.Materializer().Publish(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
        Recheck(f, secondId);
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("done\n", f.Git.Git("show", Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex + ":result.txt"));
        Assert.Single(f.Read().Results);
        Assert.Equal(new[] { "Rechecked by the person.", "Rechecked by the person." },
            f.Read().Receipts.Values.OrderBy(e => e.Sequence).Select(e => e.Event).OfType<RunEvent.BlockResolved>().Select(e => e.Reason));
    }

    [Fact]
    public async Task Drift_that_recurs_after_recheck_records_a_new_unresolved_block()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("result.txt", "done\n", ready.Checkout);
        await f.Close(ready);
        f.Git.Write("result.txt", "late\n", ready.Checkout);
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
        var firstId = Assert.Single(f.Read().Blocks).Key;
        f.Git.Write("result.txt", "done\n", ready.Checkout);
        Recheck(f, firstId);
        f.Git.Write("result.txt", "late\n", ready.Checkout);
        Assert.Equal(blocked, f.Materializer().Publish(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal(2, f.Read().Blocks.Count);
        Assert.True(f.Read().Blocks[firstId].Resolved);
        var nextId = OperationIds.Derive(Operation, "capture-blocked-1");
        Assert.False(f.Read().Blocks[nextId].Resolved);
        Assert.Equal(blocked.Block, f.Read().Blocks[nextId].Block);
        Assert.Empty(f.Read().Results);
        f.Git.Write("result.txt", "done\n", ready.Checkout);
        var sequence = f.Read().Sequence;
        Assert.Equal(blocked, f.Materializer().Publish(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal(sequence, f.Read().Sequence);
        Recheck(f, nextId);
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("done\n", f.Git.Git("show", Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex + ":result.txt"));
        Assert.Single(f.Read().Results);
    }

    [Fact]
    public async Task Every_publication_crash_rechecks_late_writes_before_any_pending_move()
    {
        var points = new List<string>();
        using (var control = new PreparationFixture(FixtureWorkflow(Writer(T))))
        {
            var ready = Assert.IsType<Preparation.Ready>(await control.Prepare(T));
            control.Git.Write("result.txt", "done\n", ready.Checkout);
            await control.Close(ready);
            var accepted = Assert.IsType<Publication.Accepted>(control.Materializer(probe: points.Add)
                .Publish(control.Lease(T), Operation, ready.Execution.Launch.Attempt));
            Assert.Equal("done\n", control.Git.Git("show", Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex + ":result.txt"));
            Assert.Single(control.Read().Results);
        }
        Assert.Contains("journal.accepted.after", points);
        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            var occurrence = points.Take(index + 1).Count(p => p == point);
            using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
            var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
            f.Git.Write("result.txt", "done\n", ready.Checkout);
            await f.Close(ready);
            var seen = 0;
            Assert.Throws<Crash>(() => f.Materializer(probe: p =>
            {
                if (p == point && ++seen == occurrence) throw new Crash();
            }).Publish(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
            f.Git.Write("result.txt", "late\n", ready.Checkout);
            var moves = f.Read().GitIntents.Count;
            var branch = GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch));
            var outcome = f.Materializer().Publish(f.Lease(T), Operation, ready.Execution.Launch.Attempt);
            if (point == "journal.accepted.after")
            {
                var original = Assert.IsType<Publication.Accepted>(outcome);
                Assert.Equal("done\n", f.Git.Git("show", Assert.IsType<CodeOutput.Produced>(original.Result.Code).Code.Commit.Hex + ":result.txt"));
                Assert.Single(f.Read().Results);
                Assert.Equal(0, f.Read().GitIntents.Count - moves);
                Assert.Equal(branch, GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch)));
                continue;
            }
            var blocked = Assert.IsType<Publication.Blocked>(outcome);
            Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
            Assert.Equal(0, f.Read().GitIntents.Count - moves);
            Assert.Equal(branch, GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch)));
            Assert.Empty(f.Read().Results);
            Assert.Equal("late\n", File.ReadAllText(Path.Combine(ready.Checkout, "result.txt")));
            f.Git.Write("result.txt", "done\n", ready.Checkout);
            Recheck(f, Assert.Single(f.Read().Blocks).Key);
            var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
            Assert.Equal("done\n", f.Git.Git("show", Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex + ":result.txt"));
            Assert.Single(f.Read().Results);
        }
    }

    [Theory]
    [InlineData("capture-corrupt")]
    [InlineData("capture-delete")]
    [InlineData("result-corrupt")]
    public async Task Replay_validates_capture_and_existing_result_artifact_bytes(string damage)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await ChangedWriter(f, artifact: true);
        var branch = GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch));
        Assert.Throws<Crash>(() => f.Materializer(probe: step =>
        {
            if (step == "journal.plan.after") throw new Crash();
        }).Publish(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
        var storage = new RunStorage(f.Git.Folder, W, f.RunId);
        var capture = Assert.Single(f.Read().Captures.Values).First();
        var plan = Assert.Single(f.Read().Plans.Values.OfType<MaterializationPlan.Publication>());
        var captureArtifact = Assert.Single(capture.Artifacts);
        var resultArtifact = Assert.Single(plan.Artifacts);
        Assert.Equal(new byte[] { 67, 0, 127 }, storage.ReadArtifact(plan.Result, resultArtifact));
        var path = RunStorage.SafePath(storage.Folder, damage == "result-corrupt" ? resultArtifact.StoredPath : captureArtifact.StoredPath);
        if (damage == "capture-delete") File.Delete(path);
        else File.WriteAllBytes(path, [88]);
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("InputUnavailable", blocked.Block.Problem.ToString());
        Assert.Empty(f.Read().Results);
        Assert.Equal(branch, GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch)));
        Assert.StartsWith($"Attempt {ready.Execution.Launch.Attempt.Value:D}, capture path {captureArtifact.StoredPath}, result path {resultArtifact.StoredPath}: ", blocked.Block.Detail);
        File.WriteAllBytes(path, [67, 0, 127]);
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal(new byte[] { 67, 0, 127 }, storage.ReadArtifact(accepted.Result.Id, Assert.Single(accepted.Result.Artifacts)));
        Assert.Equal("new\n", f.Git.Git("show", Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex + ":new.txt"));
        Assert.Single(f.Read().Results);
        Assert.Equal("Publication verified.", Assert.Single(f.Read().Receipts.Values.Select(e => e.Event).OfType<RunEvent.BlockResolved>()).Reason);
    }

    [Fact]
    public async Task Publication_acceptance_leaves_drift_recorded_after_the_last_check_unresolved()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("result.txt", "done\n", ready.Checkout);
        await f.Close(ready);
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer(probe: step =>
        {
            if (step != "journal.accepted.before") return;
            f.Git.Write("result.txt", "late\n", ready.Checkout);
            var plan = Assert.Single(f.Read().Plans.Values.OfType<MaterializationPlan.Publication>());
            Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, f.Op(), new RunEvent.Blocked(new(
                OperationIds.Derive(Operation, "plan"), T, plan.Attempt, MaterializationProblem.DirtyWorktree,
                ready.Execution.Inputs, [], "Later writer drift."))));
        }).Publish(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("done\n", f.Git.Git("show", Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex + ":result.txt"));
        Assert.Equal("late\n", File.ReadAllText(Path.Combine(ready.Checkout, "result.txt")));
        Assert.False(Assert.Single(f.Read().Blocks).Value.Resolved);
        Assert.Equal(0, f.Read().Receipts.Values.Count(e => e.Event is RunEvent.BlockResolved));
        Assert.Single(f.Read().Results);
        var sequence = f.Read().Sequence;
        var replay = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal(accepted, replay);
        Assert.Equal(sequence, f.Read().Sequence);
        Assert.False(Assert.Single(f.Read().Blocks).Value.Resolved);
    }

    [Fact]
    public async Task An_index_lock_at_settlement_is_retained_with_evidence_and_blocks_publication_until_recheck()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("result.txt", "done\n", ready.Checkout);
        var log = f.ObserveAndLog(ready);
        var repository = f.Git.Open();
        var lockPath = GitFixture.Read(repository.IndexPath(ready.Checkout)) + ".lock";
        File.WriteAllText(lockPath, "lock\n");
        var settled = Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(T), f.Op(), ready.Execution.Launch, log));
        Assert.IsType<CaptureDisposition.Matched>(settled.Disposition);
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), ready.Execution.Launch.Attempt, TerminalAttemptOutcome.Succeeded, log));
        var branch = GitFixture.Read(repository.ReadRef(ready.Execution.Location.Owner.Branch));
        var moves = f.Read().GitIntents.Count;
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
        Assert.Equal("An index.lock exists in the writer checkout; it is retained and was not removed.", blocked.Block.Detail);
        var evidence = Assert.Single(blocked.Block.Evidence);
        var storage = new RunStorage(f.Git.Folder, W, f.RunId);
        Assert.Equal(new byte[] { 108, 111, 99, 107, 10 }, RunStorage.Read(storage.Folder, evidence.RelativePath, evidence.Content, evidence.ByteLength));
        Assert.Equal(0, f.Read().GitIntents.Count - moves);
        Assert.Equal(branch, GitFixture.Read(repository.ReadRef(ready.Execution.Location.Owner.Branch)));
        Assert.Equal("lock\n", File.ReadAllText(lockPath));
        Assert.Empty(f.Read().Results);
        File.Delete(lockPath);
        var sequence = f.Read().Sequence;
        var repeated = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("DirtyWorktree", repeated.Block.Problem.ToString());
        Assert.Equal(evidence, Assert.Single(repeated.Block.Evidence));
        Assert.Equal(blocked.Block.Operation, repeated.Block.Operation);
        Assert.Equal("An index.lock exists in the writer checkout; it is retained and was not removed.", repeated.Block.Detail);
        Assert.Equal(sequence, f.Read().Sequence);
        Recheck(f, Assert.Single(f.Read().Blocks).Key);
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("done\n", f.Git.Git("show", Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex + ":result.txt"));
        Assert.Equal("", f.Git.Run(ready.Checkout, "status", "--porcelain").Text);
        Assert.Single(f.Read().Results);
    }

    private static void Recheck(PreparationFixture f, OperationId block) =>
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, f.Op(), new RunEvent.BlockResolved(block, "Rechecked by the person.")));

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

    [Fact]
    public async Task Changed_index_lock_bytes_after_recheck_remain_drift_until_the_second_block_is_rechecked()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var writer = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("result.txt", "done\n", writer.Checkout);
        await f.Close(writer);
        var lockPath = GitFixture.Read(f.Git.Open().IndexPath(writer.Checkout)) + ".lock";
        File.WriteAllText(lockPath, "first\n");
        var operation = f.Op();
        Publication Run() => f.Materializer().Publish(f.Lease(T), operation, writer.Execution.Launch.Attempt);
        var first = Assert.IsType<Publication.Blocked>(Run()).Block;
        Assert.Equal(MaterializationProblem.DirtyWorktree, first.Problem);
        Assert.Equal("first\n", LockEvidence(first));
        var key = Assert.Single(f.Read().Blocks, pair => !pair.Value.Resolved).Key;
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, f.Op(), new RunEvent.BlockResolved(key, "Rechecked.")));
        File.WriteAllText(lockPath, "second\n");
        var second = Assert.IsType<Publication.Blocked>(Run()).Block;
        Assert.Equal(MaterializationProblem.DirtyWorktree, second.Problem);
        Assert.Equal("second\n", LockEvidence(second));
        File.Delete(lockPath);
        var third = Assert.IsType<Publication.Blocked>(Run()).Block;
        Assert.Equal(second with { Evidence = [] }, third with { Evidence = [] });
        Assert.Equal(second.Evidence.Select(file => (file.RelativePath, file.Content.Sha256)), third.Evidence.Select(file => (file.RelativePath, file.Content.Sha256)));
        Assert.Equal(new[] { (MaterializationProblem.DirtyWorktree, true), (MaterializationProblem.DirtyWorktree, false) },
            f.Read().Blocks.OrderBy(pair => f.Read().Receipts[pair.Key].Sequence).Select(pair => (pair.Value.Block.Problem, pair.Value.Resolved)));
        Assert.Empty(f.Read().Results);
        key = Assert.Single(f.Read().Blocks, pair => !pair.Value.Resolved).Key;
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, f.Op(), new RunEvent.BlockResolved(key, "Rechecked second lock.")));
        Assert.Equal(T, Assert.IsType<Publication.Accepted>(Run()).Result.Task);
        Assert.Single(f.Read().Results);

        string LockEvidence(MaterializationBlock block)
        {
            var file = Assert.Single(block.Evidence);
            return System.Text.Encoding.UTF8.GetString(RunStorage.Read(new RunStorage(f.Git.Folder, W, f.RunId).Folder,
                file.RelativePath, file.Content, file.ByteLength));
        }
    }

    [Fact]
    public async Task An_index_lock_appearing_at_alignment_blocks_as_drift_with_bytes_until_recheck()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var writer = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("result.txt", "done\n", writer.Checkout);
        await f.Close(writer);
        var lockPath = GitFixture.Read(f.Git.Open().IndexPath(writer.Checkout)) + ".lock";
        var operation = f.Op();
        var block = Assert.IsType<Publication.Blocked>(f.Materializer(probe: point =>
        {
            if (point == "git.align-index.before") File.WriteAllText(lockPath, "agent\n");
        }).Publish(f.Lease(T), operation, writer.Execution.Launch.Attempt)).Block;
        Assert.Equal(MaterializationProblem.DirtyWorktree, block.Problem);
        var file = Assert.Single(block.Evidence);
        Assert.Equal("agent\n", System.Text.Encoding.UTF8.GetString(RunStorage.Read(new RunStorage(f.Git.Folder, W, f.RunId).Folder,
            file.RelativePath, file.Content, file.ByteLength)));
        File.Delete(lockPath);
        var repeated = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(f.Lease(T), operation, writer.Execution.Launch.Attempt)).Block;
        Assert.Equal(block with { Evidence = [] }, repeated with { Evidence = [] });
        Assert.Equal(block.Evidence.Select(file => (file.RelativePath, file.Content.Sha256)), repeated.Evidence.Select(file => (file.RelativePath, file.Content.Sha256)));
        var state = Assert.Single(f.Read().Blocks);
        Assert.Equal((MaterializationProblem.DirtyWorktree, false), (state.Value.Block.Problem, state.Value.Resolved));
        Assert.Empty(f.Read().Results);
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, f.Op(), new RunEvent.BlockResolved(state.Key, "Rechecked.")));
        Assert.Equal(T, Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), operation, writer.Execution.Launch.Attempt)).Result.Task);
        Assert.Single(f.Read().Results);
    }

    [Fact]
    public async Task A_staged_only_change_after_alignment_blocks_before_the_result_ref_moves()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var writer = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("result.txt", "done\n", writer.Checkout);
        await f.Close(writer);
        var operation = f.Op();
        var block = Assert.IsType<Publication.Blocked>(f.Materializer(probe: point =>
        {
            if (point != "journal.index-observed.after") return;
            f.Git.Write("staged.txt", "staged\n", writer.Checkout);
            Assert.Equal(0, f.Git.Run(writer.Checkout, "add", "staged.txt").ExitCode);
            File.Delete(Path.Combine(writer.Checkout, "staged.txt"));
        }).Publish(f.Lease(T), operation, writer.Execution.Launch.Attempt)).Block;
        Assert.Equal(MaterializationProblem.DirtyWorktree, block.Problem);
        const string reference = "refs/idp/93f23689/result/90d5b0a2/00000000-0000-0000-0000-000000000102";
        Assert.Null(GitFixture.Read(f.Git.Open().ReadRef(reference)));
        Assert.Equal("Writer files or index changed after the turn-end capture.", block.Detail);
        Assert.Empty(f.Read().Results);
        Assert.Equal(0, f.Git.Run(writer.Checkout, "reset", "HEAD", "--", "staged.txt").ExitCode);
        var key = Assert.Single(f.Read().Blocks, pair => !pair.Value.Resolved).Key;
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, f.Op(), new RunEvent.BlockResolved(key, "Rechecked.")));
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), operation, writer.Execution.Launch.Attempt));
        Assert.Equal(T, accepted.Result.Task);
        Assert.Equal("e07fb83f0b331a370aed095309ed5ded0e916a16", GitFixture.Read(f.Git.Open().ReadRef(reference))?.Hex);
        Assert.Single(f.Read().Results);
    }
}
