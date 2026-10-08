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
    public async System.Threading.Tasks.Task Matched_dispositions_refuse_unexplained_refs_and_accept_explained_observations()
    {
        var template = await ObservationTemplates();
        foreach (var unexplained in new[] { true, false })
        {
            using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
            var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
            var log = f.ObserveAndLog(ready);
            var first = template.First with { Launch = ready.Execution.Launch, Log = log,
                UnexplainedRefs = unexplained ? ["refs/stash"] : [] };
            RecordObservation(f, first);
            RecordObservation(f, template.Second with { Launch = first.Launch, Log = first.Log, UnexplainedRefs = first.UnexplainedRefs });
            var outcome = f.Store.Record(f.Permit, f.Op(), new RunEvent.CaptureDisposed(first.Capture,
                first.Launch, new CaptureDisposition.Matched()));
            if (unexplained)
            {
                Assert.Equal("EvidenceMismatch", Assert.IsType<RunDecision.Rejected>(outcome).Reason.Problem.ToString());
                Assert.Empty(f.Read().Dispositions);
                Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, f.Op(), new RunEvent.CaptureDisposed(
                    first.Capture, first.Launch, new CaptureDisposition.Diverged(MaterializationProblem.UncertainOwnership,
                        [], ["refs/stash"], "Unexplained stash."))));
                Assert.Equal("UncertainOwnership", Assert.IsType<CaptureDisposition.Diverged>(
                    f.Read().Dispositions[first.Capture].Disposition).Problem.ToString());
            }
            else
            {
                Assert.IsType<RunDecision.Recorded>(outcome);
                Assert.IsType<CaptureDisposition.Matched>(Assert.Single(f.Read().Dispositions).Value.Disposition);
            }
            Assert.Equal(2, f.Read().Captures[first.Capture].Count);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Matching_captures_require_equal_retained_index_trees()
    {
        var template = await ObservationTemplates();
        foreach (var matches in new[] { false, true })
        {
            using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
            var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
            var log = f.ObserveAndLog(ready);
            var first = template.First with { Launch = ready.Execution.Launch, Log = log };
            Assert.NotNull(first.IndexTree);
            RecordObservation(f, first);
            RecordObservation(f, template.Second with { Launch = first.Launch, Log = first.Log,
                IndexTree = matches ? first.IndexTree : null });
            var outcome = f.Store.Record(f.Permit, f.Op(), new RunEvent.CaptureDisposed(first.Capture,
                first.Launch, new CaptureDisposition.Matched()));
            if (matches) Assert.IsType<CaptureDisposition.Matched>(Assert.Single(Assert.IsType<RunDecision.Recorded>(outcome).Record.Dispositions).Value.Disposition);
            else
            {
                Assert.Equal("EvidenceMismatch", Assert.IsType<RunDecision.Rejected>(outcome).Reason.Problem.ToString());
                Assert.Empty(f.Read().Dispositions);
            }
            Assert.Equal(2, f.Read().Captures[first.Capture].Count);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Capture_ordinals_must_start_at_one_and_advance_once()
    {
        var template = await ObservationTemplates();
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        var log = f.ObserveAndLog(ready);
        var first = template.First with { Launch = ready.Execution.Launch, Log = log };
        var second = template.Second with { Launch = first.Launch, Log = first.Log };
        Assert.Equal("InvalidClaim", Assert.IsType<RunDecision.Rejected>(f.Store.Record(f.Permit, f.Op(),
            new RunEvent.TurnCaptured(second))).Reason.Problem.ToString());
        Assert.Empty(f.Read().Captures);
        RecordObservation(f, first);
        Assert.Equal("InvalidClaim", Assert.IsType<RunDecision.Rejected>(f.Store.Record(f.Permit, f.Op(),
            new RunEvent.TurnCaptured(first))).Reason.Problem.ToString());
        Assert.Equal(new[] { 1 }, f.Read().Captures[first.Capture].Select(o => o.Ordinal));
        RecordObservation(f, second);
        Assert.Equal(new[] { 1, 2 }, f.Read().Captures[first.Capture].Select(o => o.Ordinal));
    }

    [Fact]
    public async System.Threading.Tasks.Task A_diverged_disposition_requires_two_observations_while_a_failure_can_keep_one()
    {
        var template = await ObservationTemplates();
        foreach (var failed in new[] { false, true })
        {
            using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
            var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
            var log = f.ObserveAndLog(ready);
            var first = template.First with { Launch = ready.Execution.Launch, Log = log };
            RecordObservation(f, first);
            var diverged = new RunEvent.CaptureDisposed(first.Capture, first.Launch,
                new CaptureDisposition.Diverged(MaterializationProblem.DirtyWorktree, ["result.txt"], [], "Changed files."));
            Assert.Equal("InvalidClaim", Assert.IsType<RunDecision.Rejected>(f.Store.Record(f.Permit, f.Op(), diverged)).Reason.Problem.ToString());
            Assert.Empty(f.Read().Dispositions);
            if (failed)
            {
                Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, f.Op(), diverged with
                { Disposition = new CaptureDisposition.Failed(MaterializationProblem.InputUnavailable, "Missing evidence.", []) }));
                Assert.Equal("InputUnavailable", Assert.IsType<CaptureDisposition.Failed>(
                    Assert.Single(f.Read().Dispositions).Value.Disposition).Problem.ToString());
                Assert.Single(f.Read().Captures[first.Capture]);
            }
            else
            {
                RecordObservation(f, template.Second with { Launch = first.Launch, Log = first.Log, UnexplainedRefs = first.UnexplainedRefs });
                Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, f.Op(), diverged));
                Assert.Equal("DirtyWorktree", Assert.IsType<CaptureDisposition.Diverged>(
                    Assert.Single(f.Read().Dispositions).Value.Disposition).Problem.ToString());
                Assert.Equal(2, f.Read().Captures[first.Capture].Count);
            }
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task A_settled_turn_refuses_later_capture_events()
    {
        var template = await ObservationTemplates();
        foreach (var closed in new[] { true, false })
        {
            using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
            var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
            var log = f.ObserveAndLog(ready);
            var first = template.First with { Launch = ready.Execution.Launch, Log = log };
            if (closed) Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(T), f.Op(), first.Launch, log));
            var outcome = f.Store.Record(f.Permit, f.Op(), new RunEvent.TurnCaptured(first));
            if (closed)
            {
                Assert.Equal("InvalidClaim", Assert.IsType<RunDecision.Rejected>(outcome).Reason.Problem.ToString());
                Assert.Equal(2, Assert.Single(f.Read().Captures).Value.Count);
                Assert.Single(f.Read().TurnClosures);
            }
            else
            {
                Assert.IsType<RunDecision.Recorded>(outcome);
                Assert.Single(Assert.Single(f.Read().Captures).Value);
                Assert.Equal("B ready.\n", Assert.Single(Assert.Single(f.Read().Captures).Value).Report);
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
        var folder = RunStorage.SafePath(storage.Folder, $"captures/{capture.Value:D}/1");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "index"), [88]);
        File.WriteAllText(Path.Combine(folder, "extra.txt"), "stale\n");
        var closed = Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(T), operation,
            ready.Execution.Launch, log));
        Assert.IsType<CaptureDisposition.Matched>(closed.Disposition);
        Assert.Equal(2, f.Read().Captures[capture].Count);
        Assert.False(File.Exists(Path.Combine(folder, "extra.txt")));
        Assert.Equal("B ready.\n", f.Read().Captures[capture][0].Report);
        var artifact = Assert.Single(f.Read().Captures[capture][0].Artifacts);
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
        var closed = Assert.IsType<Settlement.Closed>(await f.Materializer(probe: point =>
        {
            if (point == "journal.capture-1.after")
                Assert.Equal(0, f.Git.Run(ready.Checkout, "reset", "--hard", "81ddb7c330112c7f16700ed002803a04b0bce693").ExitCode);
        }).Settle(f.Lease(T), f.Op(), ready.Execution.Launch, log));
        var pair = f.Read().Captures[closed.Capture];
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", pair[0].Tip!.Value.Hex);
        Assert.Equal("81ddb7c330112c7f16700ed002803a04b0bce693", pair[1].Tip!.Value.Hex);
        Assert.Equal("UncertainOwnership", Assert.IsType<CaptureDisposition.Diverged>(closed.Disposition).Problem.ToString());
        Assert.Equal(new[] { "refs/heads/idp/93f23689/task/90d5b0a2" }, Assert.IsType<CaptureDisposition.Diverged>(closed.Disposition).Refs);
        using var control = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var valid = Assert.IsType<Preparation.Ready>(await control.Prepare(T));
        var checkpoint = control.ObserveAndLog(valid);
        var matched = Assert.IsType<Settlement.Closed>(await control.Materializer().Settle(control.Lease(T), control.Op(), valid.Execution.Launch, checkpoint));
        Assert.IsType<CaptureDisposition.Matched>(matched.Disposition);
        Assert.Equal(new[] { "adfe40b30c176fb407933286f51d15ea9b54cdc3", "adfe40b30c176fb407933286f51d15ea9b54cdc3" },
            control.Read().Captures[matched.Capture].Select(o => o.Tip!.Value.Hex));
    }

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
        var recorded = f.Read().Captures.Values.SelectMany(observations => observations).ToArray();
        var retried = f.Materializer(probe: probe =>
        {
            if (recorded.Length == 2 && probe.StartsWith("git.capture-", StringComparison.Ordinal)) throw new CaptureCrash();
        }).Settle(f.Lease(T), operation, ready.Execution.Launch, log);
        var closed = Assert.IsType<Settlement.Closed>(await retried);
        Assert.IsType<CaptureDisposition.Matched>(closed.Disposition);
        Assert.Equal(2, f.Read().Captures[closed.Capture].Count);
        if (partial) Assert.True(f.Read().Captures[closed.Capture][1].Recovery);
        foreach (var observation in recorded) Assert.Equal(RunJournal.Canonical(observation),
            RunJournal.Canonical(f.Read().Captures[closed.Capture][observation.Ordinal - 1]));
        var sequence = f.Read().Sequence;
        SettlementAssertions.Equal(closed, await f.Materializer(probe: _ => throw new CaptureCrash()).Settle(f.Lease(T), operation, ready.Execution.Launch, log));
        Assert.Equal(sequence, f.Read().Sequence);
        if (partial || point is "journal.capture-disposition.after" or "journal.close-turn.after")
        {
            Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, operation, ready.Execution.Launch.Attempt,
                TerminalAttemptOutcome.Succeeded, log));
            var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt));
            Assert.Equal("done\n", f.Git.Git("show", Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex + ":result.txt"));
            Assert.Equal((1, 1), (f.Read().Results.Count, f.Read().Claims.Count));
            Assert.Equal(2, f.Read().Captures[closed.Capture].Count);
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
        var third = pair.Value[1] with { Ordinal = 3, Index = pair.Value[1].Index! with { RelativePath = RunStorage.CapturePath(pair.Key, 3, "index") },
            SharedRefs = pair.Value[1].SharedRefs with { RelativePath = RunStorage.CapturePath(pair.Key, 3, "refs.json") } };
        Assert.Equal(RunProblem.InvalidClaim, Assert.IsType<RunRead.Rejected>(Apply(record, new RunEvent.TurnCaptured(third))).Reason.Problem);
        var disposed = Assert.IsType<RunRead.Loaded>(Apply(record, matched)).Record;
        Assert.Equal(RunProblem.EvidenceMismatch, Assert.IsType<RunRead.Rejected>(Apply(disposed,
            new RunEvent.TurnClosed(ready.Execution.Launch, log with { ByteLength = log.ByteLength + 1 }) { Capture = pair.Key })).Reason.Problem);
        var closed = Assert.IsType<RunRead.Loaded>(Apply(disposed, new RunEvent.TurnClosed(ready.Execution.Launch, log) { Capture = pair.Key })).Record;
        Assert.Equal(pair.Key, closed.Settlements[ready.Execution.Launch]);
    }

    [Fact]
    public async System.Threading.Tasks.Task Schema_one_and_two_refuse_capture_events_and_linkage_while_the_legacy_fixture_replays_to_27()
    {
        using var f = new RunFixtures();
        var bytes = File.ReadAllBytes(Fixture.Path("e2-run/events.jsonl"));
        File.WriteAllBytes(f.Journal(W, Run), bytes);
        Assert.Equal((2, 27L, 1), (f.Read().Schema, f.Read().Sequence, f.Read().Results.Count));
        var capture = new CaptureId(Id(600));
        var launch = new LaunchKey(A1, 1);
        var log = new LogCheckpoint(10, Prompt);
        var observation = (await ObservationTemplates()).First;
        observation = observation with { Capture = capture, Launch = launch, Log = log,
            Index = observation.Index! with { RelativePath = RunStorage.CapturePath(capture, 1, "index") },
            SharedRefs = observation.SharedRefs with { RelativePath = RunStorage.CapturePath(capture, 1, "refs.json") } };
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
        SettlementAssertions.Equal(closed, repeated);
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
        SettlementAssertions.Equal(closed, await f.Materializer().Settle(f.Lease(T), operation, ready.Execution.Launch,
            log with { ByteLength = log.ByteLength + 1 }));
        SettlementAssertions.Equal(closed, await f.Materializer().Settle(f.Lease(T), operation, ready.Execution.Launch, log));
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
        if (ordinal == 1) Assert.False(f.Read().Dispositions.ContainsKey(capture));
        else
        {
            var failure = Assert.IsType<CaptureDisposition.Failed>(f.Read().Dispositions[capture].Disposition);
            Assert.Equal(MaterializationProblem.InputUnavailable, failure.Problem);
            Assert.Equal("EvidenceMismatch", failure.Detail);
        }
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

    [Fact]
    public async System.Threading.Tasks.Task Journaled_sibling_moves_during_a_ref_observation_match_and_foreign_moves_block()
    {
        foreach (var (sibling, point) in new[]
        {
            ("prepare", "git.capture-2.before"),
            ("publish", "git.capture-2.before"),
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
                var settled = Assert.IsType<Settlement.Closed>(await f.Materializer(probe: probe =>
                {
                    if (probe == "refs.snapshot.after") snapshots++;
                    if (fired != 0 || probe != point || point == "refs.snapshot.after" && snapshots != (foreign ? 1 : 2)) return;
                    fired++;
                    if (foreign)
                        Assert.Equal(0, f.Git.Run(other!.Checkout, "update-ref", other.Execution.Location.Owner.Branch,
                            "81ddb7c330112c7f16700ed002803a04b0bce693").ExitCode);
                    else if (sibling == "prepare")
                        Assert.IsType<Preparation.Ready>(f.Prepare(U).AsTask().GetAwaiter().GetResult());
                    else
                        Assert.Equal(U, Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(U), f.Op(),
                            other!.Execution.Launch.Attempt)).Result.Task);
                }).Settle(f.Lease(T), f.Op(), writer.Execution.Launch, log));
                Assert.Equal(1, fired);
                Assert.Equal(2, f.Read().Captures[settled.Capture].Count);
                Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), writer.Execution.Launch.Attempt,
                    TerminalAttemptOutcome.Succeeded, log));
                if (foreign)
                {
                    var diverged = Assert.IsType<CaptureDisposition.Diverged>(settled.Disposition);
                    Assert.Equal("UncertainOwnership", diverged.Problem.ToString());
                    Assert.Equal(new[] { "refs/heads/idp/93f23689/task/c67f2fc3" }, diverged.Refs);
                    Assert.Equal("UncertainOwnership", Assert.IsType<Publication.Blocked>(f.Materializer().Publish(
                        f.Lease(T), f.Op(), writer.Execution.Launch.Attempt)).Block.Problem.ToString());
                    Assert.Empty(f.Read().Results);
                }
                else
                {
                    Assert.IsType<CaptureDisposition.Matched>(settled.Disposition);
                    Assert.Equal(0, f.Read().Captures[settled.Capture].Sum(observation => observation.UnexplainedRefs.Length));
                    var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), f.Op(), writer.Execution.Launch.Attempt));
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
        var capture = new CaptureId(OperationIds.Derive(operation, "capture").Value);
        Assert.IsType<CaptureDisposition.Matched>(f.Read().Dispositions[capture].Disposition);
        Assert.Empty(f.Read().TurnClosures);
        Assert.Equal(new[] { ready.Execution.Launch }, f.Read().UnresolvedClaims);
        File.WriteAllBytes(path, bytes);
        var closed = Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(T), operation, ready.Execution.Launch, log));
        Assert.IsType<CaptureDisposition.Matched>(closed.Disposition);
        Assert.Single(f.Read().TurnClosures);
        Assert.Empty(f.Read().UnresolvedClaims);
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), ready.Execution.Launch.Attempt,
            TerminalAttemptOutcome.Succeeded, log));
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt));
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
        Assert.Equal(0, f.Read().Captures.Values.Sum(pair => pair.Count));
        Assert.Equal(sequence, f.Read().Sequence);
        File.WriteAllBytes(path, bytes);
        var closed = Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(T), operation, ready.Execution.Launch, full));
        Assert.IsType<CaptureDisposition.Matched>(closed.Disposition);
        Assert.Equal(2, f.Read().Captures[closed.Capture].Count);
        Assert.Single(f.Read().TurnClosures);
        Assert.Equal(full, f.Read().TurnClosures[ready.Execution.Launch]);
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), ready.Execution.Launch.Attempt,
            TerminalAttemptOutcome.Succeeded, full));
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt));
        Assert.Equal("done\n", f.Git.Git("show", Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex + ":result.txt"));
        await VerifyUnreadableLogAfterMatch();
    }

    private static async System.Threading.Tasks.Task<(CaptureObservation First, CaptureObservation Second)> ObservationTemplates()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        var log = f.ObserveAndLog(ready);
        var closed = Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(T), f.Op(), ready.Execution.Launch, log));
        Assert.IsType<CaptureDisposition.Matched>(closed.Disposition);
        var pair = f.Read().Captures[closed.Capture];
        return (pair[0], pair[1]);
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
                if (when != "inside" || point != "git.capture-1.before" || fired) return;
                fired = true;
                EndLease();
            }).Settle(f.Lease(T), f.Op(), writer.Execution.Launch, log));
            var observations = f.Read().Captures[settled.Capture];
            Assert.Equal(2, observations.Count);
            Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), writer.Execution.Launch.Attempt,
                TerminalAttemptOutcome.Succeeded, log));
            if (foreign)
            {
                Assert.Equal(new[] { "refs/heads/idp/93f23689/task/c67f2fc3" }, observations[0].UnexplainedRefs);
                var diverged = Assert.IsType<CaptureDisposition.Diverged>(settled.Disposition);
                Assert.Equal(MaterializationProblem.UncertainOwnership, diverged.Problem);
                Assert.Equal(new[] { "refs/heads/idp/93f23689/task/c67f2fc3" }, diverged.Refs);
                Assert.Equal(MaterializationProblem.UncertainOwnership, Assert.IsType<Publication.Blocked>(f.Materializer().Publish(
                    f.Lease(T), f.Op(), writer.Execution.Launch.Attempt)).Block.Problem);
                Assert.Empty(f.Read().Results);
            }
            else
            {
                Assert.Empty(observations[0].UnexplainedRefs);
                Assert.IsType<CaptureDisposition.Matched>(settled.Disposition);
                Assert.Equal(T, Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), f.Op(),
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
                if (point != "git.capture-2.before") return;
                Assert.Equal(U, Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(U), f.Op(),
                    sibling.Execution.Launch.Attempt)).Result.Task);
                if (rewind)
                    Assert.Equal(0, f.Git.Run(f.Git.Folder, "update-ref", "refs/heads/idp/93f23689/task/c67f2fc3",
                        "adfe40b30c176fb407933286f51d15ea9b54cdc3").ExitCode);
            }).Settle(f.Lease(T), f.Op(), writer.Execution.Launch, log));
            Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), writer.Execution.Launch.Attempt,
                TerminalAttemptOutcome.Succeeded, log));
            if (rewind)
            {
                var diverged = Assert.IsType<CaptureDisposition.Diverged>(settled.Disposition);
                Assert.Equal(MaterializationProblem.UncertainOwnership, diverged.Problem);
                Assert.Equal(new[] { "refs/heads/idp/93f23689/task/c67f2fc3" }, diverged.Refs);
                Assert.Equal(new[] { "refs/heads/idp/93f23689/task/c67f2fc3" }, f.Read().Captures[settled.Capture][1].UnexplainedRefs);
                Assert.Equal(MaterializationProblem.UncertainOwnership, Assert.IsType<Publication.Blocked>(f.Materializer().Publish(
                    f.Lease(T), f.Op(), writer.Execution.Launch.Attempt)).Block.Problem);
                Assert.Equal(0, f.Read().Results.Count(result => result.Task == T));
            }
            else
            {
                Assert.IsType<CaptureDisposition.Matched>(settled.Disposition);
                Assert.Empty(f.Read().Captures[settled.Capture][1].UnexplainedRefs);
                Assert.Equal(T, Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), f.Op(),
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
        Assert.Equal("Matched", settled.Disposition.GetType().Name);
        Assert.Equal(2, snapshots);
        Assert.Equal(2, f.Read().Captures[settled.Capture].Count);
        Assert.Equal(2, f.Read().Blocks.Count);
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
        var settled = Assert.IsType<Settlement.Closed>(await f.Materializer(probe: point =>
        {
            if (point != "refs.snapshot.after") return;
            if (++snapshots <= 5) RecordSiblingRefObservation(f, plan, sibling.Execution.Location.Owner.Branch);
        }).Settle(f.Lease(T), f.Op(), writer.Execution.Launch, log));
        Assert.Equal("Matched", settled.Disposition.GetType().Name);
        Assert.Equal(7, snapshots);
        Assert.Equal(2, f.Read().Captures[settled.Capture].Count);
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3",
            GitFixture.Read(f.Git.Open().ReadRef("refs/heads/idp/93f23689/task/c67f2fc3"))!.Value.Hex);
    }

    [Fact]
    public async System.Threading.Tasks.Task Exhausted_shared_ref_snapshots_reject_settlement_and_publication_and_same_operations_retry()
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
        var churn = f.Materializer(probe: point =>
        {
            if (point != "refs.snapshot.after") return;
            snapshots++;
            RecordSiblingRefObservation(f, plan, sibling.Execution.Location.Owner.Branch);
        });
        var rejected = Assert.IsType<Settlement.Rejected>(await churn.Settle(f.Lease(T), operation, writer.Execution.Launch, log));
        Assert.Equal("JournalBusy", rejected.Reason.Problem.ToString());
        Assert.True(snapshots > 3);
        Assert.Empty(f.Read().Captures.GetValueOrDefault(capture, []));
        Assert.False(f.Read().Dispositions.ContainsKey(capture));
        var settled = Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(T), operation, writer.Execution.Launch, log));
        Assert.Equal(capture, settled.Capture);
        Assert.Equal("Matched", settled.Disposition.GetType().Name);
        Assert.Equal(2, f.Read().Captures[capture].Count);
        Assert.Equal("Matched", f.Read().Dispositions[capture].Disposition.GetType().Name);
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), writer.Execution.Launch.Attempt,
            TerminalAttemptOutcome.Succeeded, log));
        operation = f.Op();
        snapshots = 0;
        var publication = Assert.IsType<Publication.Rejected>(churn.Publish(f.Lease(T), operation, writer.Execution.Launch.Attempt));
        Assert.Equal("JournalBusy", publication.Reason.Problem.ToString());
        Assert.True(snapshots > 3);
        Assert.Equal(0, f.Read().Blocks.Count(pair => !pair.Value.Resolved && pair.Value.Block.Attempt == writer.Execution.Launch.Attempt));
        Assert.Empty(f.Read().Results);
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), operation, writer.Execution.Launch.Attempt));
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
