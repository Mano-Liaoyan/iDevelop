using IDevelop.Core.Tests.Git;
using IDevelop.Core.Tests.Runs;
using IDevelop.Execution;
using IDevelop.TestSupport;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Materialization;

public sealed class CaptureTests
{
    [Fact]
    public async System.Threading.Tasks.Task A_capture_with_unexplained_refs_diverges_and_one_without_matches()
    {
        var template = await ObservationTemplate();
        foreach (var unexplained in new[] { true, false })
        {
            using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
            var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
            var log = f.ObserveAndLog(ready);
            var capture = template with { Launch = ready.Execution.Launch, Log = log, UnexplainedRefs = unexplained ? ["refs/stash"] : [] };
            RecordObservation(f, capture);
            var divergence = RunReducer.Divergence(f.Read(), f.Read().Captures[ready.Execution.Launch]);
            if (unexplained) Assert.Equal(new[] { "refs/stash" }, Assert.IsType<CaptureDivergence>(divergence).Refs);
            else Assert.Null(divergence);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task A_turn_takes_one_capture()
    {
        var template = await ObservationTemplate();
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        var log = f.ObserveAndLog(ready);
        var capture = template with { Launch = ready.Execution.Launch, Log = log };
        RecordObservation(f, capture);
        Assert.Equal("InvalidClaim", Assert.IsType<RunDecision.Rejected>(f.Store.Record(f.Permit, f.Op(),
            new RunEvent.TurnCaptured(capture))).Reason.Problem.ToString());
        Assert.Equal(capture.Capture, Assert.Single(f.Read().Captures).Value.Capture);
    }

    [Fact]
    public async System.Threading.Tasks.Task A_capture_block_needs_the_root_exit_and_stands_for_the_capture()
    {
        var template = await ObservationTemplate();
        foreach (var captured in new[] { false, true })
        {
            using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
            var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
            var launch = ready.Execution.Launch;
            RootExitTests.Claim(f, ready);
            var block = new MaterializationBlock(RunReducer.CaptureBlock(launch), T, launch.Attempt, MaterializationProblem.InputUnavailable,
                ready.Execution.Inputs, [], "Missing evidence.") { Scope = new BlockScope.Operation() };
            Assert.Equal("InvalidClaim", Assert.IsType<RunDecision.Rejected>(f.Store.Record(f.Permit, f.Op(),
                new RunEvent.Blocked(block))).Reason.Problem.ToString());
            var log = f.ObserveAndLog(ready);
            Assert.Equal(RunProblem.SettlementPending, Assert.IsType<RunDecision.Rejected>(f.Store.CloseTurn(f.Permit, f.Op(), launch, log, settles: true)).Reason.Problem);
            if (captured)
            {
                RecordObservation(f, template with { Launch = launch, Log = log });
                Assert.Equal("InvalidClaim", Assert.IsType<RunDecision.Rejected>(f.Store.Record(f.Permit, f.Op(),
                    new RunEvent.Blocked(block))).Reason.Problem.ToString());
                Assert.Empty(f.Read().Blocks);
            }
            else
            {
                Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, f.Op(), new RunEvent.Blocked(block)));
                Assert.Equal("InputUnavailable", RunReducer.CaptureFailure(f.Read(), launch)!.Problem.ToString());
                Assert.Empty(f.Read().Captures);
            }
            Assert.IsType<RunDecision.Recorded>(f.Store.CloseTurn(f.Permit, f.Op(), launch, log, settles: true));
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task A_settled_turn_refuses_later_capture_events()
    {
        var template = await ObservationTemplate();
        foreach (var closed in new[] { true, false })
        {
            using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
            var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
            var log = f.ObserveAndLog(ready);
            var first = template with { Launch = ready.Execution.Launch, Log = log };
            if (closed) Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(T), f.Op(), first.Launch, log));
            var outcome = f.Store.Record(f.Permit, f.Op(), new RunEvent.TurnCaptured(first));
            if (closed)
            {
                Assert.Equal("InvalidClaim", Assert.IsType<RunDecision.Rejected>(outcome).Reason.Problem.ToString());
                Assert.Single(f.Read().Captures);
                Assert.Single(f.Read().TurnClosures);
            }
            else
            {
                Assert.IsType<RunDecision.Recorded>(outcome);
                Assert.Equal("B ready.\n", Assert.Single(f.Read().Captures).Value.Report);
            }
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Settlement_replaces_stale_unjournaled_capture_bytes_and_removes_extra_files()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        WriteArtifact(ready, [67, 0, 127]);
        var log = f.ObserveAndLog(ready);
        var operation = f.Op();
        var capture = new CaptureId(OperationIds.Derive(operation, "capture").Value);
        var storage = new RunStorage(f.Git.Folder, W, f.RunId);
        var folder = RunStorage.SafePath(storage.Folder, $"captures/{capture.Value:D}");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "index"), [88]);
        File.WriteAllText(Path.Combine(folder, "extra.txt"), "stale\n");
        var closed = Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(T), operation,
            ready.Execution.Launch, log));
        Assert.Equal(capture, SettlementAssertions.Matched(closed).Capture);
        Assert.False(File.Exists(Path.Combine(folder, "extra.txt")));
        Assert.Equal("B ready.\n", f.Read().Captures[ready.Execution.Launch].Report);
        var artifact = Assert.Single(f.Read().Captures[ready.Execution.Launch].Artifacts);
        Assert.Equal(new byte[] { 67, 0, 127 }, RunStorage.Read(storage.Folder, artifact.StoredPath,
            artifact.Content, artifact.ByteLength));
        Assert.Equal(new byte[] { 67, 0, 127 }, File.ReadAllBytes(Path.Combine(folder, "artifacts", "payload")));
        SettlementAssertions.Equal(closed, await f.Materializer().Settle(f.Lease(T), operation, ready.Execution.Launch, log));
    }

    [Fact]
    public async System.Threading.Tasks.Task Captures_record_the_live_rewound_tip_instead_of_the_root_tip()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        var log = f.ObserveAndLog(ready);
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", f.Read().RootExits[ready.Execution.Launch].Tip.Hex);
        var closed = Assert.IsType<Settlement.Closed>(await f.Materializer(probe: point =>
        {
            if (point == "git.capture.before")
                Assert.Equal(0, f.Git.Run(ready.Checkout, "reset", "--hard", "81ddb7c330112c7f16700ed002803a04b0bce693").ExitCode);
        }).Settle(f.Lease(T), f.Op(), ready.Execution.Launch, log));
        Assert.Equal("81ddb7c330112c7f16700ed002803a04b0bce693", f.Read().Captures[ready.Execution.Launch].Tip!.Value.Hex);
        Assert.Equal(new[] { "refs/heads/idp/93f23689/task/90d5b0a2" }, SettlementAssertions.Diverged(closed).Refs);
        using var control = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var valid = Assert.IsType<Preparation.Ready>(await control.Prepare(T));
        var checkpoint = control.ObserveAndLog(valid);
        var matched = Assert.IsType<Settlement.Closed>(await control.Materializer().Settle(control.Lease(T), control.Op(), valid.Execution.Launch, checkpoint));
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", SettlementAssertions.Matched(matched).Tip!.Value.Hex);
    }

    [Fact]
    public async System.Threading.Tasks.Task A_matching_capture_freezes_code_index_report_and_binary_artifacts_without_changing_the_checkout()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("result.txt", "done\n", ready.Checkout);
        WriteArtifact(ready, [67, 0, 127]);
        var log = f.ObserveAndLog(ready, "Done.\n");
        var repository = f.Git.Open();
        var index = GitFixture.Read(repository.IndexDigest(ready.Checkout));
        var indexBytes = File.ReadAllBytes(GitFixture.Read(repository.IndexPath(ready.Checkout)));
        var tip = GitFixture.Read(repository.ReadRef(ready.Execution.Location.Owner.Branch!));
        using var mutation = repository.TakeMutationLock();
        Assert.NotNull(mutation);
        var closed = Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(T), f.Op(), ready.Execution.Launch, log));
        SettlementAssertions.Matched(closed);
        var record = f.Read();
        var observation = record.Captures[ready.Execution.Launch];
        Assert.Equal("e07fb83f0b331a370aed095309ed5ded0e916a16", observation.Candidate!.Value.Hex);
        Assert.Equal("Done.\n", observation.Report);
        Assert.Equal("done\n", f.Git.Git("show", observation.Recipe.Tree.Hex + ":result.txt"));
        Assert.Equal(new[] { "adfe40b30c176fb407933286f51d15ea9b54cdc3" },
            GitFixture.Read(repository.ReadCommit(observation.Candidate!.Value)).Parents.Select(parent => parent.Hex));
        Assert.Equal("iDevelop <idevelop@localhost>", observation.Recipe.Author);
        Assert.Equal("iDevelop <idevelop@localhost>", observation.Recipe.Committer);
        Assert.Equal(At, observation.Recipe.Timestamp);
        var storage = new RunStorage(f.Git.Folder, W, f.RunId);
        var artifact = Assert.Single(observation.Artifacts);
        Assert.Equal(new byte[] { 67, 0, 127 }, RunStorage.Read(storage.Folder, artifact.StoredPath, artifact.Content, artifact.ByteLength));
        Assert.Equal(RunStorage.CapturePath(observation.Capture, "artifacts/payload"), artifact.StoredPath);
        var stored = Assert.IsType<EvidenceFile>(observation.Index);
        Assert.Equal("cf3a398a67ea241355d092c55c321d9463d19d14cc11595a01cd02a70c0f3f74", stored.Content.Sha256);
        var bytes = RunStorage.Read(storage.Folder, stored.RelativePath, stored.Content, stored.ByteLength);
        Assert.Equal(index, Revision.Hash(bytes));
        Assert.Equal(GitFixture.Read(repository.IndexBytes(ready.Checkout)), bytes);
        Assert.Equal("done\n", File.ReadAllText(Path.Combine(ready.Checkout, "result.txt")));
        Assert.Equal(indexBytes, File.ReadAllBytes(GitFixture.Read(repository.IndexPath(ready.Checkout))));
        Assert.Equal(index, GitFixture.Read(repository.IndexDigest(ready.Checkout)));
        Assert.Equal(tip, GitFixture.Read(repository.ReadRef(ready.Execution.Location.Owner.Branch!)));
        Assert.Equal(RunJournal.Canonical(observation), RunJournal.Canonical(closed.Capture));
        Assert.Equal(log, record.TurnClosures[ready.Execution.Launch]);
    }

    [Fact]
    public async System.Threading.Tasks.Task A_late_commit_is_UncertainOwnership()
    {
        foreach (var stray in new[] { false, true })
        {
            using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
            var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
            f.Git.Write("result.txt", "writer\n", ready.Checkout);
            var log = f.ObserveAndLog(ready);
            if (stray)
            {
                Assert.Equal(0, f.Git.Run(ready.Checkout, "add", "result.txt").ExitCode);
                Assert.Equal(0, f.Git.Run(ready.Checkout, "commit", "-q", "-m", "late").ExitCode);
            }
            var closed = Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(T), f.Op(), ready.Execution.Launch, log));
            if (stray) Assert.Equal(new[] { "refs/heads/idp/93f23689/task/90d5b0a2" }, SettlementAssertions.Diverged(closed).Refs);
            else SettlementAssertions.Matched(closed);
            var observation = f.Read().Captures[ready.Execution.Launch];
            Assert.Equal("writer\n", f.Git.Git("show", observation.Recipe.Tree.Hex + ":result.txt"));
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task A_HEAD_detached_after_root_observation_diverges_as_UncertainOwnership_and_blocks_publication()
    {
        foreach (var detach in new[] { false, true })
        {
            using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
            var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
            f.Git.Write("result.txt", "writer\n", ready.Checkout);
            var log = f.ObserveAndLog(ready);
            if (detach) Assert.Equal(0, f.Git.Run(ready.Checkout, "checkout", "-q", "--detach").ExitCode);
            var closed = Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(T), f.Op(), ready.Execution.Launch, log));
            Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), ready.Execution.Launch.Attempt,
                TerminalAttemptOutcome.Succeeded, log));
            var publication = f.Materializer().Accept(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt);
            if (detach)
            {
                Assert.Equal(new[] { "HEAD" }, SettlementAssertions.Diverged(closed).Refs);
                Assert.Equal(MaterializationProblem.UncertainOwnership, Assert.IsType<Acceptance.Blocked>(publication).Block.Problem);
                Assert.Empty(f.Read().Results);
            }
            else
            {
                SettlementAssertions.Matched(closed);
                Assert.Equal("writer\n", f.Git.Git("show", Assert.IsType<CodeOutput.Produced>(
                    Assert.IsType<Acceptance.Accepted>(publication).Result.Code).Code.Commit.Hex + ":result.txt"));
            }
        }
    }

    [Theory]
    [InlineData("git.capture.before", false)]
    [InlineData("git.capture.after", false)]
    [InlineData("journal.capture.before", false)]
    [InlineData("journal.capture.after", true)]
    [InlineData("journal.close-turn.before", true)]
    [InlineData("journal.close-turn.after", true)]
    public async System.Threading.Tasks.Task Crash_recovery_uses_recorded_observations_and_never_replaces_a_partial_capture(string point, bool partial)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(U)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        Assert.IsType<RunDecision.Granted>(f.Store.Claim(f.Lease(T), f.Op(), ready.Execution.Launch,
            f.Read().Inputs[ready.Execution.Inputs], ready.Execution.PromptHash));
        f.Git.Write("result.txt", "done\n", ready.Checkout);
        var log = f.ObserveAndLog(ready);
        var operation = f.Op();
        var crash = f.Materializer(probe: probe => { if (probe == point) throw new CaptureCrash(); });
        await Assert.ThrowsAsync<CaptureCrash>(() => crash.Settle(f.Lease(T), operation, ready.Execution.Launch, log).AsTask());
        var recorded = f.Read().Captures.Values.ToArray();
        Assert.Equal(partial, recorded.Length == 1);
        var retried = f.Materializer(probe: probe =>
        {
            if (recorded.Length == 1 && probe == "git.capture.before") throw new CaptureCrash();
        }).Settle(f.Lease(T), operation, ready.Execution.Launch, log);
        var closed = Assert.IsType<Settlement.Closed>(await retried);
        var capture = SettlementAssertions.Matched(closed);
        foreach (var observation in recorded) Assert.Equal(RunJournal.Canonical(observation), RunJournal.Canonical(capture));
        var sequence = f.Read().Sequence;
        SettlementAssertions.Equal(closed, await f.Materializer(probe: _ => throw new CaptureCrash()).Settle(f.Lease(T), operation, ready.Execution.Launch, log));
        Assert.Equal(sequence, f.Read().Sequence);
        if (partial)
        {
            Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, operation, ready.Execution.Launch.Attempt,
                TerminalAttemptOutcome.Succeeded, log));
            var accepted = Assert.IsType<Acceptance.Accepted>(f.Materializer().Accept(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt));
            Assert.Equal("done\n", f.Git.Git("show", Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex + ":result.txt"));
            Assert.Equal((1, 1), (f.Read().Results.Count, f.Read().Claims.Count));
            Assert.Single(f.Read().Captures);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task The_reducer_closes_a_captured_turn_only_at_the_captured_checkpoint()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        var log = f.ObserveAndLog(ready);
        await Assert.ThrowsAsync<CaptureCrash>(() => f.Materializer(probe: point =>
        {
            if (point == "journal.close-turn.before") throw new CaptureCrash();
        }).Settle(f.Lease(T), f.Op(), ready.Execution.Launch, log).AsTask());
        var record = f.Read();
        Assert.Single(record.Captures);
        RunRead Apply(RunRecord current, RunEvent e) => RunReducer.Apply(W, f.RunId, current,
            new(RunJournal.Schema, current.Sequence + 1, f.Op(), Prompt, At, e));
        Assert.Equal(RunProblem.EvidenceMismatch, Assert.IsType<RunRead.Rejected>(Apply(record,
            new RunEvent.TurnClosed(ready.Execution.Launch, log with { ByteLength = log.ByteLength + 1 }))).Reason.Problem);
        var uncaptured = record with { Captures = record.Captures.Clear() };
        Assert.Equal(RunProblem.EvidenceMismatch, Assert.IsType<RunRead.Rejected>(Apply(uncaptured,
            new RunEvent.TurnClosed(ready.Execution.Launch, log))).Reason.Problem);
        var closed = Assert.IsType<RunRead.Loaded>(Apply(record, new RunEvent.TurnClosed(ready.Execution.Launch, log))).Record;
        Assert.Equal(log, closed.TurnClosures[ready.Execution.Launch]);
        Assert.NotNull(RunReducer.ResultCapture(closed, ready.Execution.Launch));
    }

    [Fact]
    public async System.Threading.Tasks.Task An_observation_failure_is_durable_with_its_evidence_and_closes_the_turn()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        WriteArtifact(ready, [67, 0, 127]);
        var manifest = Path.Combine(ready.Checkout, ready.Execution.OutboxPath, "manifest.json");
        var log = f.ObserveAndLog(ready);
        File.WriteAllText(manifest, "{broken");
        var operation = f.Op();
        var closed = Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(T), operation, ready.Execution.Launch, log));
        var failed = SettlementAssertions.Failed(closed);
        Assert.Equal(MaterializationProblem.InputUnavailable, failed.Problem);
        Assert.Empty(f.Read().Captures);
        var evidence = Assert.Single(failed.Evidence);
        Assert.Contains("manifest.json", System.Text.Encoding.UTF8.GetString(RunStorage.Read(
            new RunStorage(f.Git.Folder, W, f.RunId).Folder, evidence.RelativePath, evidence.Content, evidence.ByteLength)));
        Assert.Equal(RunJournal.Canonical(failed), RunJournal.Canonical(RunReducer.CaptureFailure(f.Read(), ready.Execution.Launch)));
        Assert.Equal(log, f.Read().TurnClosures[ready.Execution.Launch]);
        WriteArtifact(ready, [67, 0, 127]);
        var sequence = f.Read().Sequence;
        var repeated = Assert.IsType<Settlement.Closed>(await f.Materializer(probe: _ => throw new CaptureCrash())
            .Settle(f.Lease(T), operation, ready.Execution.Launch, log));
        SettlementAssertions.Equal(closed, repeated);
        Assert.Equal(sequence, f.Read().Sequence);
        using var control = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var clean = Assert.IsType<Preparation.Ready>(await control.Prepare(T));
        WriteArtifact(clean, [67, 0, 127]);
        var checkpoint = control.ObserveAndLog(clean);
        SettlementAssertions.Matched(Assert.IsType<Settlement.Closed>(await control.Materializer().Settle(
            control.Lease(T), control.Op(), clean.Execution.Launch, checkpoint)));
    }

    [Fact]
    public async System.Threading.Tasks.Task Settlement_requires_the_root_and_task_lease_and_preserves_capture_identity()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(U)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        RootExitTests.Claim(f, ready);
        var operation = f.Op();
        Assert.Equal(RunProblem.UnresolvedOwnership, Assert.IsType<Settlement.Rejected>(await f.Materializer().Settle(
            f.Lease(T), operation, ready.Execution.Launch, new(0, Prompt))).Reason.Problem);
        var log = f.ObserveAndLog(ready);
        var sequence = f.Read().Sequence;
        Assert.Equal(RunProblem.IdentityMismatch, Assert.IsType<Settlement.Rejected>(await f.Materializer().Settle(
            f.Lease(U), operation, ready.Execution.Launch, log)).Reason.Problem);
        Assert.Equal(sequence, f.Read().Sequence);
        await Assert.ThrowsAsync<CaptureCrash>(() => f.Materializer(probe: point =>
        {
            if (point == "journal.capture.after") throw new CaptureCrash();
        }).Settle(f.Lease(T), operation, ready.Execution.Launch, log).AsTask());
        sequence = f.Read().Sequence;
        Assert.Equal(RunProblem.OperationConflict, Assert.IsType<Settlement.Rejected>(await f.Materializer().Settle(
            f.Lease(T), f.Op(), ready.Execution.Launch, log)).Reason.Problem);
        Assert.Equal(sequence, f.Read().Sequence);
        var closed = Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(T), operation, ready.Execution.Launch, log));
        SettlementAssertions.Matched(closed);
        Assert.Equal(RunProblem.EvidenceMismatch, Assert.IsType<Settlement.Rejected>(await f.Materializer().Settle(
            f.Lease(T), f.Op(), ready.Execution.Launch, log)).Reason.Problem);
        SettlementAssertions.Equal(closed, await f.Materializer().Settle(f.Lease(T), operation, ready.Execution.Launch,
            log with { ByteLength = log.ByteLength + 1 }));
        SettlementAssertions.Equal(closed, await f.Materializer().Settle(f.Lease(T), operation, ready.Execution.Launch, log));
    }

    [Fact]
    public async System.Threading.Tasks.Task Unreadable_log_evidence_records_nothing_and_leaves_the_turn_unclosed()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(U)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        var checkpoint = f.ObserveAndLog(ready);
        var path = Path.Combine(f.Store.AttemptFolder(W, f.RunId, T, ready.Execution.Launch.Attempt), "events.jsonl");
        File.AppendAllText(path, "not-json\n");
        var operation = f.Op();
        var rejected = Assert.IsType<Settlement.Rejected>(await f.Materializer().Settle(f.Lease(T), operation, ready.Execution.Launch, checkpoint));
        Assert.Equal(RunProblem.EvidenceMismatch, rejected.Reason.Problem);
        Assert.Empty(f.Read().Captures);
        Assert.Empty(f.Read().Blocks);
        Assert.False(f.Read().TurnClosures.ContainsKey(ready.Execution.Launch));
        Assert.Equal(new[] { ready.Execution.Launch }, f.Read().UnresolvedClaims);
        var control = Assert.IsType<Preparation.Ready>(await f.Prepare(U));
        var log = f.ObserveAndLog(control);
        SettlementAssertions.Matched(Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(
            f.Lease(U), f.Op(), control.Execution.Launch, log)));
        Assert.Equal(log, f.Read().TurnClosures[control.Execution.Launch]);
    }

    private static void WriteArtifact(Preparation.Ready ready, byte[] bytes)
    {
        var outbox = Path.Combine(ready.Checkout, ready.Execution.OutboxPath);
        File.WriteAllBytes(Path.Combine(outbox, "payload.bin"), bytes);
        File.WriteAllText(Path.Combine(outbox, "manifest.json"), "{\"schema\":1,\"artifacts\":[{\"name\":\"payload\",\"path\":\"payload.bin\"}]}");
    }

    [Fact]
    public async System.Threading.Tasks.Task Journaled_sibling_moves_during_a_ref_observation_match_and_foreign_moves_block()
    {
        foreach (var (sibling, point) in new[]
        {
            ("prepare", "git.capture.before"),
            ("publish", "git.capture.before"),
            ("publish", "refs.snapshot.after"),
        })
        {
            foreach (var foreign in new[] { false, true })
            {
                using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(U)));
                Preparation.Ready? other = null;
                if (sibling == "publish" || foreign)
                {
                    other = Assert.IsType<Preparation.Ready>(await f.Prepare(U));
                    f.Git.Write("u.txt", "U\n", other.Checkout);
                    await f.Close(other);
                }
                var writer = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
                f.Git.Write("result.txt", "done\n", writer.Checkout);
                var log = f.ObserveAndLog(writer);
                var snapshots = 0;
                var fired = 0;
                // The sibling's publication runs inside a snapshot try. A held clock keeps its Git time, which grows
                // under load, out of the one-second budget for unstable snapshots.
                var settled = Assert.IsType<Settlement.Closed>(await f.Materializer(clock: new Clock(held: true), probe: probe =>
                {
                    if (probe == "refs.snapshot.after") snapshots++;
                    if (fired != 0 || probe != point || point == "refs.snapshot.after" && snapshots != 1) return;
                    fired++;
                    if (foreign)
                        Assert.Equal(0, f.Git.Run(other!.Checkout, "update-ref", other.Execution.Location.Owner.Branch!,
                            "81ddb7c330112c7f16700ed002803a04b0bce693").ExitCode);
                    else if (sibling == "prepare")
                        Assert.IsType<Preparation.Ready>(f.Prepare(U).AsTask().GetAwaiter().GetResult());
                    else
                        Assert.Equal(U, Assert.IsType<Acceptance.Accepted>(f.Materializer().Accept(f.Lease(U), f.Op(),
                            other!.Execution.Launch.Attempt)).Result.Task);
                }).Settle(f.Lease(T), f.Op(), writer.Execution.Launch, log));
                Assert.Equal(1, fired);
                Assert.True(f.Read().Captures.ContainsKey(writer.Execution.Launch));
                Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), writer.Execution.Launch.Attempt,
                    TerminalAttemptOutcome.Succeeded, log));
                if (foreign)
                {
                    // A foreign move after the capture's only snapshot leaves the writer's own branch and capture alone.
                    if (point == "refs.snapshot.after")
                    {
                        SettlementAssertions.Matched(settled);
                        Assert.Equal(T, Assert.IsType<Acceptance.Accepted>(f.Materializer().Accept(
                            f.Lease(T), f.Op(), writer.Execution.Launch.Attempt)).Result.Task);
                        continue;
                    }
                    Assert.Equal(new[] { "refs/heads/idp/93f23689/task/c67f2fc3" }, SettlementAssertions.Diverged(settled).Refs);
                    Assert.Equal("UncertainOwnership", Assert.IsType<Acceptance.Blocked>(f.Materializer().Accept(
                        f.Lease(T), f.Op(), writer.Execution.Launch.Attempt)).Block.Problem.ToString());
                    Assert.Empty(f.Read().Results);
                }
                else
                {
                    Assert.Empty(SettlementAssertions.Matched(settled).UnexplainedRefs);
                    var accepted = Assert.IsType<Acceptance.Accepted>(f.Materializer().Accept(f.Lease(T), f.Op(), writer.Execution.Launch.Attempt));
                    Assert.Equal("done\n", f.Git.Git("show", Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex + ":result.txt"));
                    Assert.Equal(1, f.Read().Results.Count(result => result.Task == T));
                }
            }
        }
    }

    private static async System.Threading.Tasks.Task VerifyUnreadableLogAfterMatch()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("result.txt", "done\n", ready.Checkout);
        var log = f.ObserveAndLog(ready);
        var path = Path.Combine(f.Store.AttemptFolder(W, f.RunId, T, ready.Execution.Launch.Attempt), "events.jsonl");
        var bytes = File.ReadAllBytes(path);
        var operation = f.Op();
        var rejected = Assert.IsType<Settlement.Rejected>(await f.Materializer(probe: point =>
        {
            if (point == "journal.close-turn.before") File.AppendAllText(path, "not-json\n");
        }).Settle(f.Lease(T), operation, ready.Execution.Launch, log));
        Assert.Equal("EvidenceMismatch", rejected.Reason.Problem.ToString());
        Assert.Null(RunReducer.Divergence(f.Read(), f.Read().Captures[ready.Execution.Launch]));
        Assert.Empty(f.Read().TurnClosures);
        Assert.Equal(new[] { ready.Execution.Launch }, f.Read().UnresolvedClaims);
        File.WriteAllBytes(path, bytes);
        var closed = Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(T), operation, ready.Execution.Launch, log));
        SettlementAssertions.Matched(closed);
        Assert.Single(f.Read().TurnClosures);
        Assert.Empty(f.Read().UnresolvedClaims);
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), ready.Execution.Launch.Attempt,
            TerminalAttemptOutcome.Succeeded, log));
        var accepted = Assert.IsType<Acceptance.Accepted>(f.Materializer().Accept(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt));
        Assert.Equal("done\n", f.Git.Git("show", Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex + ":result.txt"));
    }

    [Fact]
    public async System.Threading.Tasks.Task A_running_checkpoint_records_nothing_and_the_same_operation_can_settle_the_finished_turn()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("result.txt", "done\n", ready.Checkout);
        var full = f.ObserveAndLog(ready);
        var path = Path.Combine(f.Store.AttemptFolder(W, f.RunId, T, ready.Execution.Launch.Attempt), "events.jsonl");
        var bytes = File.ReadAllBytes(path);
        var lines = System.Text.Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var early = System.Text.Encoding.UTF8.GetBytes(string.Join("\n", lines.Take(2)) + "\n");
        File.WriteAllBytes(path, early);
        var operation = f.Op();
        var sequence = f.Read().Sequence;
        var rejected = Assert.IsType<Settlement.Rejected>(await f.Materializer().Settle(f.Lease(T), operation,
            ready.Execution.Launch, new(early.LongLength, Revision.Hash(early))));
        Assert.Equal("OutcomeMismatch", rejected.Reason.Problem.ToString());
        Assert.Empty(f.Read().Captures);
        Assert.Equal(sequence, f.Read().Sequence);
        File.WriteAllBytes(path, bytes);
        var closed = Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(T), operation, ready.Execution.Launch, full));
        SettlementAssertions.Matched(closed);
        Assert.Single(f.Read().Captures);
        Assert.Single(f.Read().TurnClosures);
        Assert.Equal(full, f.Read().TurnClosures[ready.Execution.Launch]);
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), ready.Execution.Launch.Attempt,
            TerminalAttemptOutcome.Succeeded, full));
        var accepted = Assert.IsType<Acceptance.Accepted>(f.Materializer().Accept(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt));
        Assert.Equal("done\n", f.Git.Git("show", Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex + ":result.txt"));
        await VerifyUnreadableLogAfterMatch();
    }

    private static async System.Threading.Tasks.Task<CaptureObservation> ObservationTemplate()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        var log = f.ObserveAndLog(ready);
        var closed = Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(T), f.Op(), ready.Execution.Launch, log));
        return SettlementAssertions.Matched(closed);
    }

    private static void RecordObservation(PreparationFixture f, CaptureObservation observation) =>
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, f.Op(), new RunEvent.TurnCaptured(observation)));

    private sealed class CaptureCrash : Exception;

    [Fact]
    public async System.Threading.Tasks.Task A_sibling_lease_ending_before_or_inside_capture_does_not_explain_a_later_foreign_commit()
    {
        foreach (var when in new[] { "inside", "before" })
        foreach (var foreign in new[] { true, false })
        {
            using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(U)));
            var sibling = Assert.IsType<Preparation.Ready>(await f.Prepare(U));
            RootExitTests.Claim(f, sibling);
            var writer = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
            f.Git.Write("result.txt", "done\n", writer.Checkout);
            var log = f.ObserveAndLog(writer);
            void EndLease()
            {
                Assert.Equal(TipOwnership.Explained, Assert.IsType<RootObservation.Observed>(f.Materializer().ObserveRootExit(
                    f.Lease(U), f.Op(), sibling.Execution.Launch, new RootExit.Exited(0))).Observation.Ownership);
                if (!foreign) return;
                f.Git.Write("foreign.txt", "foreign\n", sibling.Checkout);
                Assert.Equal(0, f.Git.Run(sibling.Checkout, "add", "foreign.txt").ExitCode);
                Assert.Equal(0, f.Git.Run(sibling.Checkout, "-c", "commit.gpgSign=false", "commit", "-qm", "foreign").ExitCode);
            }
            if (when == "before") EndLease();
            var fired = false;
            var settled = Assert.IsType<Settlement.Closed>(await f.Materializer(probe: point =>
            {
                if (when != "inside" || point != "git.capture.before" || fired) return;
                fired = true;
                EndLease();
            }).Settle(f.Lease(T), f.Op(), writer.Execution.Launch, log));
            var observation = f.Read().Captures[writer.Execution.Launch];
            Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), writer.Execution.Launch.Attempt,
                TerminalAttemptOutcome.Succeeded, log));
            if (foreign)
            {
                Assert.Equal(new[] { "refs/heads/idp/93f23689/task/c67f2fc3" }, observation.UnexplainedRefs);
                Assert.Equal(new[] { "refs/heads/idp/93f23689/task/c67f2fc3" }, SettlementAssertions.Diverged(settled).Refs);
                Assert.Equal(MaterializationProblem.UncertainOwnership, Assert.IsType<Acceptance.Blocked>(f.Materializer().Accept(
                    f.Lease(T), f.Op(), writer.Execution.Launch.Attempt)).Block.Problem);
                Assert.Empty(f.Read().Results);
            }
            else
            {
                Assert.Empty(observation.UnexplainedRefs);
                SettlementAssertions.Matched(settled);
                Assert.Equal(T, Assert.IsType<Acceptance.Accepted>(f.Materializer().Accept(f.Lease(T), f.Op(),
                    writer.Execution.Launch.Attempt)).Result.Task);
                Assert.Single(f.Read().Results);
            }
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task A_foreign_rewind_to_a_siblings_obsolete_journaled_tip_diverges()
    {
        foreach (var rewind in new[] { true, false })
        {
            using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(U)));
            var sibling = Assert.IsType<Preparation.Ready>(await f.Prepare(U));
            f.Git.Write("u.txt", "U\n", sibling.Checkout);
            await f.Close(sibling);
            var writer = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
            f.Git.Write("result.txt", "done\n", writer.Checkout);
            var log = f.ObserveAndLog(writer);
            var settled = Assert.IsType<Settlement.Closed>(await f.Materializer(probe: point =>
            {
                if (point != "git.capture.before") return;
                Assert.Equal(U, Assert.IsType<Acceptance.Accepted>(f.Materializer().Accept(f.Lease(U), f.Op(),
                    sibling.Execution.Launch.Attempt)).Result.Task);
                if (rewind)
                    Assert.Equal(0, f.Git.Run(f.Git.Folder, "update-ref", "refs/heads/idp/93f23689/task/c67f2fc3",
                        "adfe40b30c176fb407933286f51d15ea9b54cdc3").ExitCode);
            }).Settle(f.Lease(T), f.Op(), writer.Execution.Launch, log));
            Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), writer.Execution.Launch.Attempt,
                TerminalAttemptOutcome.Succeeded, log));
            if (rewind)
            {
                Assert.Equal(new[] { "refs/heads/idp/93f23689/task/c67f2fc3" }, SettlementAssertions.Diverged(settled).Refs);
                Assert.Equal(new[] { "refs/heads/idp/93f23689/task/c67f2fc3" }, f.Read().Captures[writer.Execution.Launch].UnexplainedRefs);
                Assert.Equal(MaterializationProblem.UncertainOwnership, Assert.IsType<Acceptance.Blocked>(f.Materializer().Accept(
                    f.Lease(T), f.Op(), writer.Execution.Launch.Attempt)).Block.Problem);
                Assert.Equal(0, f.Read().Results.Count(result => result.Task == T));
            }
            else
            {
                Assert.Empty(SettlementAssertions.Matched(settled).UnexplainedRefs);
                Assert.Equal(T, Assert.IsType<Acceptance.Accepted>(f.Materializer().Accept(f.Lease(T), f.Op(),
                    writer.Execution.Launch.Attempt)).Result.Task);
                Assert.Equal(1, f.Read().Results.Count(result => result.Task == T));
            }
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Irrelevant_sibling_journal_churn_does_not_retry_shared_ref_snapshots()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(U)));
        var sibling = Assert.IsType<Preparation.Ready>(await f.Prepare(U));
        var writer = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        var log = f.ObserveAndLog(writer);
        var snapshots = 0;
        var settled = Assert.IsType<Settlement.Closed>(await f.Materializer(probe: point =>
        {
            if (point != "refs.snapshot.after") return;
            snapshots++;
            var operation = f.Op();
            Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, operation, new RunEvent.Blocked(new(
                operation, U, sibling.Execution.Launch.Attempt, MaterializationProblem.InputUnavailable,
                sibling.Execution.Inputs, [], "Sibling input became unavailable.") { Scope = new BlockScope.Operation() })));
        }).Settle(f.Lease(T), f.Op(), writer.Execution.Launch, log));
        SettlementAssertions.Matched(settled);
        Assert.Equal(1, snapshots);
        Assert.Single(f.Read().Captures);
        Assert.Single(f.Read().Blocks);
    }

    [Fact]
    public async System.Threading.Tasks.Task Relevant_sibling_ref_churn_retries_beyond_three_snapshots_then_matches()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(U)));
        var sibling = Assert.IsType<Preparation.Ready>(await f.Prepare(U));
        var plan = f.Read().Plans.Single(pair => pair.Value is MaterializationPlan.Preparation p &&
            p.Attempt == sibling.Execution.Launch.Attempt).Key;
        var writer = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        var log = f.ObserveAndLog(writer);
        var snapshots = 0;
        var settled = Assert.IsType<Settlement.Closed>(await f.Materializer(clock: new Clock(held: true), probe: point =>
        {
            if (point != "refs.snapshot.after") return;
            if (++snapshots <= 5) RecordSiblingRefObservation(f, plan, sibling.Execution.Location.Owner.Branch!);
        }).Settle(f.Lease(T), f.Op(), writer.Execution.Launch, log));
        SettlementAssertions.Matched(settled);
        Assert.Equal(6, snapshots);
        Assert.Single(f.Read().Captures);
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3",
            GitFixture.Read(f.Git.Open().ReadRef("refs/heads/idp/93f23689/task/c67f2fc3"))!.Value.Hex);
    }

    [Fact]
    public async System.Threading.Tasks.Task Exhausted_shared_ref_snapshots_reject_settlement_and_the_same_operation_retries()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(U)));
        var sibling = Assert.IsType<Preparation.Ready>(await f.Prepare(U));
        var plan = f.Read().Plans.Single(pair => pair.Value is MaterializationPlan.Preparation p &&
            p.Attempt == sibling.Execution.Launch.Attempt).Key;
        var writer = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("result.txt", "done\n", writer.Checkout);
        var log = f.ObserveAndLog(writer);
        var operation = f.Op();
        var capture = new CaptureId(OperationIds.Derive(operation, "capture").Value);
        var snapshots = 0;
        // Each try takes 300 ms on the held clock, so the fourth try ends the one-second budget however long Git takes.
        var clock = new Clock(held: true);
        var churn = f.Materializer(clock: clock, probe: point =>
        {
            if (point != "refs.snapshot.after") return;
            snapshots++;
            clock.Advance(TimeSpan.FromMilliseconds(300));
            RecordSiblingRefObservation(f, plan, sibling.Execution.Location.Owner.Branch!);
        });
        var rejected = Assert.IsType<Settlement.Rejected>(await churn.Settle(f.Lease(T), operation, writer.Execution.Launch, log));
        Assert.Equal("JournalBusy", rejected.Reason.Problem.ToString());
        Assert.Equal(4, snapshots);
        Assert.Empty(f.Read().Captures);
        Assert.Empty(f.Read().Blocks);
        var settled = Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(T), operation, writer.Execution.Launch, log));
        Assert.Equal(capture, SettlementAssertions.Matched(settled).Capture);
        Assert.Single(f.Read().Captures);
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), writer.Execution.Launch.Attempt,
            TerminalAttemptOutcome.Succeeded, log));
        operation = f.Op();
        var accepted = Assert.IsType<Acceptance.Accepted>(f.Materializer().Accept(f.Lease(T), operation, writer.Execution.Launch.Attempt));
        Assert.Equal(T, accepted.Result.Task);
        Assert.Equal(T, Assert.Single(f.Read().Results).Task);
        Assert.Equal("done\n", f.Git.Git("show", Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex + ":result.txt"));
    }

    private static void RecordSiblingRefObservation(PreparationFixture f, OperationId plan, string branch)
    {
        var intent = f.Op();
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, intent,
            new RunEvent.GitIntended(plan, new GitMutation.MoveRef(new(branch, f.A, f.A)))));
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, f.Op(),
            new RunEvent.GitObserved(intent, new(true, f.A.Hex))));
    }
}
