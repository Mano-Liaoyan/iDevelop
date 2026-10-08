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
    public async System.Threading.Tasks.Task The_second_observation_starts_exactly_250_ms_after_the_first()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        var log = f.ObserveAndLog(ready);
        var clock = new ManualTimeProvider();
        var materializer = Materializer.Open(f.Git.Folder, f.Store, null, clock, f.Git.Environment);
        var settling = materializer.Settle(f.Lease(T), f.Op(), ready.Execution.Launch, log).AsTask();
        Assert.Single(Assert.Single(f.Read().Captures).Value);
        clock.Advance(TimeSpan.FromMilliseconds(249));
        Assert.Single(Assert.Single(f.Read().Captures).Value);
        Assert.False(settling.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var closed = Assert.IsType<Settlement.Closed>(await settling);
        Assert.IsType<CaptureDisposition.Matched>(closed.Disposition);
        var pair = f.Read().Captures[closed.Capture];
        Assert.Equal(2, pair.Count);
        Assert.Equal(pair[0].Completed + TimeSpan.FromMilliseconds(250), pair[1].Started);
    }

    [Fact]
    public async System.Threading.Tasks.Task Matching_captures_freeze_code_index_report_and_binary_artifacts_without_changing_the_checkout()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("result.txt", "done\n", ready.Checkout);
        WriteArtifact(ready, [67, 0, 127]);
        var log = f.ObserveAndLog(ready, "Done.\n");
        var repository = f.Git.Open();
        var index = GitFixture.Read(repository.IndexDigest(ready.Checkout));
        var indexBytes = File.ReadAllBytes(GitFixture.Read(repository.IndexPath(ready.Checkout)));
        var tip = GitFixture.Read(repository.ReadRef(ready.Execution.Location.Owner.Branch));
        using var mutation = repository.TakeMutationLock();
        Assert.NotNull(mutation);
        var closed = Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(T), f.Op(), ready.Execution.Launch, log));
        Assert.IsType<CaptureDisposition.Matched>(closed.Disposition);
        var record = f.Read();
        var pair = record.Captures[closed.Capture];
        Assert.Equal(2, pair.Count);
        Assert.Equal("e07fb83f0b331a370aed095309ed5ded0e916a16", pair[0].Candidate.Hex);
        Assert.Equal(pair[0].Candidate, pair[1].Candidate);
        foreach (var observation in pair)
        {
            Assert.Equal("Done.\n", observation.Report);
            Assert.Equal("done\n", f.Git.Git("show", observation.Recipe.Tree.Hex + ":result.txt"));
            Assert.Equal(new[] { "adfe40b30c176fb407933286f51d15ea9b54cdc3" },
                GitFixture.Read(repository.ReadCommit(observation.Candidate)).Parents.Select(parent => parent.Hex));
            Assert.Equal("iDevelop <idevelop@localhost>", observation.Recipe.Author);
            Assert.Equal("iDevelop <idevelop@localhost>", observation.Recipe.Committer);
            Assert.Equal(At, observation.Recipe.Timestamp);
            var storage = new RunStorage(f.Git.Folder, W, f.RunId);
            var artifact = Assert.Single(observation.Artifacts);
            Assert.Equal(new byte[] { 67, 0, 127 }, RunStorage.Read(storage.Folder, artifact.StoredPath, artifact.Content, artifact.ByteLength));
            Assert.Equal(RunStorage.CapturePath(closed.Capture, observation.Ordinal, "artifacts/payload"), artifact.StoredPath);
            var stored = Assert.IsType<EvidenceFile>(observation.Index);
            Assert.Equal("cf3a398a67ea241355d092c55c321d9463d19d14cc11595a01cd02a70c0f3f74", stored.Content.Sha256);
            var bytes = RunStorage.Read(storage.Folder, stored.RelativePath, stored.Content, stored.ByteLength);
            Assert.Equal(index, Revision.Hash(bytes));
            Assert.Equal(GitFixture.Read(repository.IndexBytes(ready.Checkout)), bytes);
        }
        Assert.Equal("done\n", File.ReadAllText(Path.Combine(ready.Checkout, "result.txt")));
        Assert.Equal(indexBytes, File.ReadAllBytes(GitFixture.Read(repository.IndexPath(ready.Checkout))));
        Assert.Equal(index, GitFixture.Read(repository.IndexDigest(ready.Checkout)));
        Assert.Equal(tip, GitFixture.Read(repository.ReadRef(ready.Execution.Location.Owner.Branch)));
        Assert.Equal(closed.Capture, record.Settlements[ready.Execution.Launch]);
        Assert.Equal(log, record.TurnClosures[ready.Execution.Launch]);
    }

    [Fact]
    public async System.Threading.Tasks.Task A_write_between_observations_retains_both_trees_and_closes_with_DirtyWorktree()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("result.txt", "first\n", ready.Checkout);
        var log = f.ObserveAndLog(ready);
        var closed = Assert.IsType<Settlement.Closed>(await f.Materializer(probe: point =>
        {
            if (point == "journal.capture-1.after") f.Git.Write("result.txt", "second\n", ready.Checkout);
        }).Settle(f.Lease(T), f.Op(), ready.Execution.Launch, log));
        var diverged = Assert.IsType<CaptureDisposition.Diverged>(closed.Disposition);
        Assert.Equal(MaterializationProblem.DirtyWorktree, diverged.Problem);
        Assert.Equal(new[] { "result.txt" }, diverged.Paths);
        var pair = f.Read().Captures[closed.Capture];
        Assert.Equal("first\n", f.Git.Git("show", pair[0].Recipe.Tree.Hex + ":result.txt"));
        Assert.Equal("second\n", f.Git.Git("show", pair[1].Recipe.Tree.Hex + ":result.txt"));
        Assert.Equal(closed.Capture, f.Read().Settlements[ready.Execution.Launch]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async System.Threading.Tasks.Task A_late_commit_and_a_stray_stash_are_UncertainOwnership_even_when_both_captures_agree(bool stash)
    {
        foreach (var stray in new[] { false, true })
        {
            using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
            var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
            f.Git.Write("result.txt", "writer\n", ready.Checkout);
            var log = f.ObserveAndLog(ready);
            if (stray)
            {
                if (stash) Assert.Equal(0, f.Git.Run(ready.Checkout, "update-ref", "refs/stash", f.A.Hex).ExitCode);
                else
                {
                    Assert.Equal(0, f.Git.Run(ready.Checkout, "add", "result.txt").ExitCode);
                    Assert.Equal(0, f.Git.Run(ready.Checkout, "commit", "-q", "-m", "late").ExitCode);
                }
            }
            var closed = Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(T), f.Op(), ready.Execution.Launch, log));
            if (stray)
            {
                var diverged = Assert.IsType<CaptureDisposition.Diverged>(closed.Disposition);
                Assert.Equal(MaterializationProblem.UncertainOwnership, diverged.Problem);
                Assert.Equal(new[] { stash ? "refs/stash" : "refs/heads/idp/93f23689/task/90d5b0a2" }, diverged.Refs);
            }
            else Assert.IsType<CaptureDisposition.Matched>(closed.Disposition);
            var pair = f.Read().Captures[closed.Capture];
            Assert.Equal(2, pair.Count);
            Assert.Equal(pair[0].Tip, pair[1].Tip);
            Assert.Equal("writer\n", f.Git.Git("show", pair[0].Recipe.Tree.Hex + ":result.txt"));
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
            var publication = f.Materializer().Publish(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt);
            if (detach)
            {
                var diverged = Assert.IsType<CaptureDisposition.Diverged>(closed.Disposition);
                Assert.Equal(MaterializationProblem.UncertainOwnership, diverged.Problem);
                Assert.Equal(new[] { "HEAD" }, diverged.Refs);
                Assert.Empty(diverged.Paths);
                Assert.Equal(MaterializationProblem.UncertainOwnership, Assert.IsType<Publication.Blocked>(publication).Block.Problem);
                Assert.Empty(f.Read().Results);
            }
            else
            {
                Assert.IsType<CaptureDisposition.Matched>(closed.Disposition);
                Assert.Equal("writer\n", f.Git.Git("show", Assert.IsType<CodeOutput.Produced>(
                    Assert.IsType<Publication.Accepted>(publication).Result.Code).Code.Commit.Hex + ":result.txt"));
            }
        }
    }

    [Theory]
    [InlineData("git.capture-1.before", false)]
    [InlineData("git.capture-1.after", false)]
    [InlineData("journal.capture-1.before", false)]
    [InlineData("journal.capture-1.after", true)]
    [InlineData("git.capture-2.before", true)]
    [InlineData("git.capture-2.after", true)]
    [InlineData("journal.capture-2.before", true)]
    [InlineData("journal.capture-2.after", false)]
    [InlineData("journal.capture-disposition.before", false)]
    [InlineData("journal.capture-disposition.after", false)]
    [InlineData("journal.close-turn.before", false)]
    [InlineData("journal.close-turn.after", false)]
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
        var journal = Path.Combine(new RunStorage(f.Git.Folder, W, f.RunId).Folder, "events.jsonl");
        var before = File.ReadAllBytes(journal);
        var recorded = f.Read().Captures.Values.SelectMany(observations => observations).ToArray();
        var retried = f.Materializer(probe: probe =>
        {
            if (recorded.Length != 0 && probe.StartsWith("git.capture-", StringComparison.Ordinal)) throw new CaptureCrash();
        }).Settle(f.Lease(T), operation, ready.Execution.Launch, log);
        if (partial)
        {
            Assert.Equal(RunProblem.RecoveryEvidenceInsufficient, Assert.IsType<Settlement.Rejected>(await retried).Reason.Problem);
            Assert.Equal(before, File.ReadAllBytes(journal));
            Assert.Single(Assert.Single(f.Read().Captures).Value);
            var control = Assert.IsType<Preparation.Ready>(await f.Prepare(U));
            var checkpoint = f.ObserveAndLog(control);
            Assert.IsType<CaptureDisposition.Matched>(Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(
                f.Lease(U), f.Op(), control.Execution.Launch, checkpoint)).Disposition);
            return;
        }
        var closed = Assert.IsType<Settlement.Closed>(await retried);
        Assert.IsType<CaptureDisposition.Matched>(closed.Disposition);
        Assert.Equal(2, f.Read().Captures[closed.Capture].Count);
        foreach (var observation in recorded) Assert.Equal(RunJournal.Canonical(observation),
            RunJournal.Canonical(f.Read().Captures[closed.Capture][observation.Ordinal - 1]));
        var sequence = f.Read().Sequence;
        Assert.Equal(closed, await f.Materializer(probe: _ => throw new CaptureCrash()).Settle(f.Lease(T), operation, ready.Execution.Launch, log));
        Assert.Equal(sequence, f.Read().Sequence);
        if (point is "journal.capture-disposition.after" or "journal.close-turn.after")
        {
            Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, operation, ready.Execution.Launch.Attempt,
                TerminalAttemptOutcome.Succeeded, log));
            var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt));
            Assert.Equal("done\n", f.Git.Git("show", Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex + ":result.txt"));
            Assert.Single(f.Read().Results);
            Assert.Equal(2, f.Read().Captures[closed.Capture].Count);
            Assert.Single(f.Read().Claims);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task The_reducer_rejects_false_matches_third_ordinals_and_wrong_closure_checkpoints()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        var log = f.ObserveAndLog(ready);
        await Assert.ThrowsAsync<CaptureCrash>(() => f.Materializer(probe: point =>
        {
            if (point == "journal.capture-disposition.before") throw new CaptureCrash();
        }).Settle(f.Lease(T), f.Op(), ready.Execution.Launch, log).AsTask());
        var record = f.Read();
        var pair = Assert.Single(record.Captures);
        var matched = new RunEvent.CaptureDisposed(pair.Key, ready.Execution.Launch, new CaptureDisposition.Matched());
        RunRead Apply(RunRecord current, RunEvent e) => RunReducer.Apply(W, f.RunId, current,
            new(3, current.Sequence + 1, f.Op(), Prompt, At, e));
        var falseMatch = record with { Captures = record.Captures.SetItem(pair.Key, pair.Value.SetItem(1, pair.Value[1] with { Report = "Different." })) };
        Assert.Equal(RunProblem.EvidenceMismatch, Assert.IsType<RunRead.Rejected>(Apply(falseMatch, matched)).Reason.Problem);
        var third = pair.Value[1] with { Ordinal = 3, Index = pair.Value[1].Index! with { RelativePath = RunStorage.CapturePath(pair.Key, 3, "index") } };
        Assert.Equal(RunProblem.InvalidClaim, Assert.IsType<RunRead.Rejected>(Apply(record, new RunEvent.TurnCaptured(third))).Reason.Problem);
        var disposed = Assert.IsType<RunRead.Loaded>(Apply(record, matched)).Record;
        Assert.Equal(RunProblem.EvidenceMismatch, Assert.IsType<RunRead.Rejected>(Apply(disposed,
            new RunEvent.TurnClosed(ready.Execution.Launch, log with { ByteLength = log.ByteLength + 1 }) { Capture = pair.Key })).Reason.Problem);
        var closed = Assert.IsType<RunRead.Loaded>(Apply(disposed, new RunEvent.TurnClosed(ready.Execution.Launch, log) { Capture = pair.Key })).Record;
        Assert.Equal(pair.Key, closed.Settlements[ready.Execution.Launch]);
    }

    [Fact]
    public void Schema_one_and_two_refuse_capture_events_and_linkage_while_the_legacy_fixture_replays_to_27()
    {
        using var f = new RunFixtures();
        var bytes = File.ReadAllBytes(Fixture.Path("e2-run/events.jsonl"));
        File.WriteAllBytes(f.Journal(W, Run), bytes);
        Assert.Equal((2, 27L, 1), (f.Read().Schema, f.Read().Sequence, f.Read().Results.Count));
        var capture = new CaptureId(Id(600));
        var launch = new LaunchKey(A1, 1);
        var log = new LogCheckpoint(10, Prompt);
        var observation = new CaptureObservation(capture, 1, launch, log, At, At,
            new(new(Base.Hex), [Base], "capture", "iDevelop <idevelop@localhost>", "iDevelop <idevelop@localhost>", At),
            Base, Base, "refs/heads/idp/task", null, null, [], []);
        RunEvent[] events = [new RunEvent.TurnCaptured(observation),
            new RunEvent.CaptureDisposed(capture, launch, new CaptureDisposition.Failed(MaterializationProblem.InputUnavailable, "Missing.", [])),
            new RunEvent.TurnClosed(launch, log) { Capture = capture }];
        foreach (var e in events)
        {
            File.WriteAllBytes(f.Journal(W, Run), bytes);
            File.AppendAllText(f.Journal(W, Run), RunJournal.Encode(new(2, 28, f.Op(), Prompt, At, e)));
            Assert.Equal(new RunRejection(RunProblem.UnsupportedSchema, 28), Assert.IsType<RunRead.Rejected>(f.Store.Read(W, Run)).Reason);
            Assert.Equal(new RunRejection(RunProblem.UnsupportedSchema, 1), RunJournal.Decode(RunJournal.Encode(new(1, 1, f.Op(), Prompt, At, e))).Rejection);
        }
        File.WriteAllBytes(f.Journal(W, Run), bytes);
        Assert.Equal(27L, f.Read().Sequence);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async System.Threading.Tasks.Task An_observation_failure_is_durable_with_its_evidence_and_closes_the_turn(int ordinal)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        WriteArtifact(ready, [67, 0, 127]);
        var manifest = Path.Combine(ready.Checkout, ready.Execution.OutboxPath, "manifest.json");
        var log = f.ObserveAndLog(ready);
        if (ordinal == 1) File.WriteAllText(manifest, "{broken");
        var operation = f.Op();
        var materializer = f.Materializer(probe: point =>
        {
            if (ordinal == 2 && point == "journal.capture-1.after") File.WriteAllText(manifest, "{broken");
        });
        var closed = Assert.IsType<Settlement.Closed>(await materializer.Settle(f.Lease(T), operation, ready.Execution.Launch, log));
        var failed = Assert.IsType<CaptureDisposition.Failed>(closed.Disposition);
        Assert.Equal(MaterializationProblem.InputUnavailable, failed.Problem);
        Assert.Equal(ordinal - 1, f.Read().Captures[closed.Capture].Count);
        var evidence = Assert.Single(failed.Evidence);
        Assert.Contains("manifest.json", System.Text.Encoding.UTF8.GetString(RunStorage.Read(
            new RunStorage(f.Git.Folder, W, f.RunId).Folder, evidence.RelativePath, evidence.Content, evidence.ByteLength)));
        Assert.Equal(closed.Capture, f.Read().Settlements[ready.Execution.Launch]);
        WriteArtifact(ready, [67, 0, 127]);
        var sequence = f.Read().Sequence;
        var repeated = Assert.IsType<Settlement.Closed>(await f.Materializer(probe: _ => throw new CaptureCrash())
            .Settle(f.Lease(T), operation, ready.Execution.Launch, log));
        Assert.Equal(RunJournal.Canonical(closed), RunJournal.Canonical(repeated));
        Assert.Equal(sequence, f.Read().Sequence);
        using var control = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var clean = Assert.IsType<Preparation.Ready>(await control.Prepare(T));
        WriteArtifact(clean, [67, 0, 127]);
        var checkpoint = control.ObserveAndLog(clean);
        Assert.IsType<CaptureDisposition.Matched>(Assert.IsType<Settlement.Closed>(await control.Materializer().Settle(
            control.Lease(T), control.Op(), clean.Execution.Launch, checkpoint)).Disposition);
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
            if (point == "journal.capture-2.after") throw new CaptureCrash();
        }).Settle(f.Lease(T), operation, ready.Execution.Launch, log).AsTask());
        sequence = f.Read().Sequence;
        Assert.Equal(RunProblem.OperationConflict, Assert.IsType<Settlement.Rejected>(await f.Materializer().Settle(
            f.Lease(T), f.Op(), ready.Execution.Launch, log)).Reason.Problem);
        Assert.Equal(sequence, f.Read().Sequence);
        var closed = Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(T), operation, ready.Execution.Launch, log));
        Assert.IsType<CaptureDisposition.Matched>(closed.Disposition);
        Assert.Equal(RunProblem.EvidenceMismatch, Assert.IsType<Settlement.Rejected>(await f.Materializer().Settle(
            f.Lease(T), f.Op(), ready.Execution.Launch, log)).Reason.Problem);
        Assert.Equal(closed, await f.Materializer().Settle(f.Lease(T), operation, ready.Execution.Launch,
            log with { ByteLength = log.ByteLength + 1 }));
        Assert.Equal(closed, await f.Materializer().Settle(f.Lease(T), operation, ready.Execution.Launch, log));
    }

    [Fact]
    public async System.Threading.Tasks.Task A_journaled_sibling_publication_between_observations_remains_explained()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(U)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        var sibling = Assert.IsType<Preparation.Ready>(await f.Prepare(U));
        await f.Close(sibling);
        var log = f.ObserveAndLog(ready);
        var closed = Assert.IsType<Settlement.Closed>(await f.Materializer(probe: point =>
        {
            if (point == "journal.capture-1.after")
                Assert.Equal(U, Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(U), f.Op(),
                    sibling.Execution.Launch.Attempt)).Result.Task);
        }).Settle(f.Lease(T), f.Op(), ready.Execution.Launch, log));
        Assert.IsType<CaptureDisposition.Matched>(closed.Disposition);
        Assert.Equal(2, f.Read().Captures[closed.Capture].Count);
        Assert.All(f.Read().Captures[closed.Capture], observation => Assert.Empty(observation.UnexplainedRefs));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async System.Threading.Tasks.Task Unreadable_log_evidence_records_a_failed_capture_and_leaves_the_turn_unclosed(int ordinal)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(U)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        var checkpoint = f.ObserveAndLog(ready);
        var path = Path.Combine(f.Store.AttemptFolder(W, f.RunId, T, ready.Execution.Launch.Attempt), "events.jsonl");
        if (ordinal == 1) File.AppendAllText(path, "not-json\n");
        var operation = f.Op();
        var rejected = Assert.IsType<Settlement.Rejected>(await f.Materializer(probe: point =>
        {
            if (ordinal == 2 && point == "journal.capture-1.after") File.AppendAllText(path, "not-json\n");
        }).Settle(f.Lease(T), operation, ready.Execution.Launch, checkpoint));
        Assert.Equal(RunProblem.EvidenceMismatch, rejected.Reason.Problem);
        var capture = new CaptureId(OperationIds.Derive(operation, "capture").Value);
        var failure = Assert.IsType<CaptureDisposition.Failed>(f.Read().Dispositions[capture].Disposition);
        Assert.Equal(MaterializationProblem.InputUnavailable, failure.Problem);
        Assert.Equal("EvidenceMismatch", failure.Detail);
        Assert.Equal(ordinal - 1, f.Read().Captures.GetValueOrDefault(capture, []).Count);
        Assert.False(f.Read().TurnClosures.ContainsKey(ready.Execution.Launch));
        Assert.Equal(new[] { ready.Execution.Launch }, f.Read().UnresolvedClaims);
        var control = Assert.IsType<Preparation.Ready>(await f.Prepare(U));
        var log = f.ObserveAndLog(control);
        Assert.IsType<CaptureDisposition.Matched>(Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(
            f.Lease(U), f.Op(), control.Execution.Launch, log)).Disposition);
        Assert.Equal(log, f.Read().TurnClosures[control.Execution.Launch]);
    }

    private static void WriteArtifact(Preparation.Ready ready, byte[] bytes)
    {
        var outbox = Path.Combine(ready.Checkout, ready.Execution.OutboxPath);
        File.WriteAllBytes(Path.Combine(outbox, "payload.bin"), bytes);
        File.WriteAllText(Path.Combine(outbox, "manifest.json"), "{\"schema\":1,\"artifacts\":[{\"name\":\"payload\",\"path\":\"payload.bin\"}]}");
    }

    private sealed class CaptureCrash : Exception;
}
