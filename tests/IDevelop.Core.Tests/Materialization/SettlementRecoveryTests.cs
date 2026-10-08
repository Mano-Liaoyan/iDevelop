using IDevelop.Core.Tests.Runs;
using IDevelop.Execution;
using IDevelop.TestSupport;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;
using static IDevelop.Core.Tests.Runs.RunFixtures;
using AsyncTask = System.Threading.Tasks.Task;

namespace IDevelop.Core.Tests.Materialization;

public sealed class SettlementRecoveryTests
{
    [Fact]
    public async AsyncTask A_crash_after_matching_captures_closes_once_after_takeover()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await ProcessCrash(f, ready, "journal.capture-disposition.before");
        var closed = Assert.IsType<Settlement.Closed>(await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), ready.Execution.Launch));
        Assert.IsType<CaptureDisposition.Matched>(closed.Disposition);
        Assert.Equal(new[] { false, false }, f.Read().Captures[closed.Capture].Select(o => o.Recovery));
        Assert.Equal(2, f.Read().Captures[closed.Capture].Count);
        CloseAndPublish(f, ready, f.Read().Captures[closed.Capture][0].Log);
        var sequence = f.Read().Sequence;
        SettlementAssertions.Equal(closed, await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), ready.Execution.Launch));
        Assert.Equal(sequence, f.Read().Sequence);
        Assert.Single(f.Read().TurnClosures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async AsyncTask Recovery_retains_index_trees_only_when_the_first_observation_has_one(bool retained)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (ready, log) = await Ready(f);
        var capture = new CaptureId(f.Op().Value);
        CaptureObservation? first = null;
        await Assert.ThrowsAsync<SettlementCrash>(() => f.Materializer(probe: point =>
        {
            if (point != "journal.capture-2.before") return;
            first = Assert.Single(Assert.Single(f.Read().Captures).Value);
            throw new SettlementCrash();
        }).Settle(f.Lease(T), new OperationId(capture.Value), ready.Execution.Launch, log).AsTask());
        var template = Assert.IsType<CaptureObservation>(first);
        using var recovery = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (other, checkpoint) = await Ready(recovery);
        var observation = template with { Launch = other.Execution.Launch, Log = checkpoint,
            IndexTree = retained ? template.IndexTree : null };
        Assert.NotNull(template.IndexTree);
        Assert.IsType<RunDecision.Recorded>(recovery.Store.Record(recovery.Permit, recovery.Op(), new RunEvent.TurnCaptured(observation)));
        var canonical = RunJournal.Canonical(observation);
        if (!retained) Assert.DoesNotContain("indexTree", canonical);
        recovery.ReleaseControl();
        var closed = Assert.IsType<Settlement.Closed>(await recovery.Materializer().RecoverSettlement(recovery.Lease(T), recovery.Op(), other.Execution.Launch));
        Assert.IsType<CaptureDisposition.Matched>(closed.Disposition);
        var pair = recovery.Read().Captures[closed.Capture];
        Assert.Equal(2, pair.Count);
        Assert.True(pair[1].Recovery);
        Assert.Equal(observation.IndexTree, pair[1].IndexTree);
        Assert.Equal(canonical, RunJournal.Canonical(pair[0]));
        var pins = IDevelop.Core.Tests.Git.GitFixture.Read(recovery.Git.Open().RefSnapshot(RunLayout.PinPrefix(recovery.Read().RunKey!)));
        Assert.Equal(retained, pins.ContainsKey(RunLayout.CaptureIndexPin(recovery.Read().RunKey!, recovery.Read().TaskKeys[T], other.Execution.Launch, 2)));
    }

    [Fact]
    public async AsyncTask A_changed_recovery_capture_keeps_both_observations_and_blocks()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (ready, log) = await Ready(f);
        await Crash(f, ready, log, f.Op(), "journal.capture-1.after");
        var first = Assert.Single(Assert.Single(f.Read().Captures).Value);
        var bytes = RunJournal.Canonical(first);
        f.ReleaseControl();
        f.Git.Write("result.txt", "late\n", ready.Checkout);
        var closed = Assert.IsType<Settlement.Closed>(await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), ready.Execution.Launch));
        var diverged = Assert.IsType<CaptureDisposition.Diverged>(closed.Disposition);
        Assert.Equal("DirtyWorktree", diverged.Problem.ToString());
        Assert.Equal(new[] { "result.txt" }, diverged.Paths);
        var pair = f.Read().Captures[closed.Capture];
        Assert.Equal(2, pair.Count);
        Assert.True(pair[1].Recovery);
        Assert.Equal(bytes, RunJournal.Canonical(pair[0]));
        Assert.Equal(new[] { "done\n", "late\n" }, pair.Select(o => f.Git.Git("show", o.Candidate.Hex + ":result.txt")));
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), ready.Execution.Launch.Attempt,
            TerminalAttemptOutcome.Succeeded, log));
        Assert.Equal("DirtyWorktree", Assert.IsType<Publication.Blocked>(f.Materializer().Publish(f.Lease(T), f.Op(),
            ready.Execution.Launch.Attempt)).Block.Problem.ToString());
        Assert.Equal(0, f.Read().Results.Count(result => result.Task == T));
        var sequence = f.Read().Sequence;
        SettlementAssertions.Equal(closed, await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), ready.Execution.Launch));
        Assert.Equal(sequence, f.Read().Sequence);
        Assert.Single(f.Read().Claims);
        using var control = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (valid, checkpoint) = await Ready(control);
        Assert.IsType<CaptureDisposition.Matched>(Assert.IsType<Settlement.Closed>(await control.Materializer().Settle(
            control.Lease(T), control.Op(), valid.Execution.Launch, checkpoint)).Disposition);
        CloseAndPublish(control, valid, checkpoint);
    }

    [Fact]
    public async AsyncTask A_recovery_capture_waits_for_the_interval_from_the_first_capture()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (ready, log) = await Ready(f);
        var clock = new ManualTimeProvider();
        var operation = f.Op();
        var firstTry = f.Materializer(clock: clock, probe: point =>
        {
            if (point == "journal.capture-2.before") throw new SettlementCrash();
        }).Settle(f.Lease(T), operation, ready.Execution.Launch, log).AsTask();
        var first = Assert.Single(Assert.Single(f.Read().Captures).Value);
        Assert.False(firstTry.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAsync<SettlementCrash>(() => firstTry);
        var bytes = RunJournal.Canonical(first);
        var retryClock = new ManualTimeProvider();
        retryClock.Advance(first.Completed + TimeSpan.FromMilliseconds(249) - retryClock.GetUtcNow());
        var retry = f.Materializer(clock: retryClock).Settle(f.Lease(T), operation, ready.Execution.Launch, log).AsTask();
        Assert.False(retry.IsCompleted);
        Assert.Single(f.Read().Captures[first.Capture]);
        retryClock.Advance(TimeSpan.FromMilliseconds(1));
        var closed = Assert.IsType<Settlement.Closed>(await retry.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsType<CaptureDisposition.Matched>(closed.Disposition);
        Assert.Equal(2, f.Read().Captures[first.Capture].Count);
        Assert.True(f.Read().Captures[first.Capture][1].Recovery);
        Assert.Equal(first.Completed + TimeSpan.FromMilliseconds(250), f.Read().Captures[first.Capture][1].Started);
        Assert.Equal(bytes, RunJournal.Canonical(f.Read().Captures[first.Capture][0]));
    }

    [Fact]
    public async AsyncTask A_missing_first_capture_after_takeover_closes_with_failure_evidence()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("keep.txt", "keep\n", ready.Checkout);
        await ProcessCrash(f, ready, "journal.capture-1.before");
        var closed = Assert.IsType<Settlement.Closed>(await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), ready.Execution.Launch));
        var failure = Assert.IsType<CaptureDisposition.Failed>(closed.Disposition);
        Assert.Equal("InputUnavailable", failure.Problem.ToString());
        Assert.Equal("Missing turn-end capture evidence.", failure.Detail);
        Assert.Empty(f.Read().Captures[closed.Capture]);
        Assert.Single(f.Read().TurnClosures);
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), ready.Execution.Launch.Attempt,
            TerminalAttemptOutcome.Succeeded, f.Read().TurnClosures[ready.Execution.Launch]));
        Assert.Equal("Missing turn-end capture evidence.", Assert.IsType<Publication.Blocked>(f.Materializer().Publish(
            f.Lease(T), f.Op(), ready.Execution.Launch.Attempt)).Block.Detail);
        Assert.Equal(0, f.Read().Results.Count(result => result.Task == T));
        Assert.Equal("keep\n", File.ReadAllText(Path.Combine(ready.Checkout, "keep.txt")));
        var sequence = f.Read().Sequence;
        SettlementAssertions.Equal(closed, await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), ready.Execution.Launch));
        Assert.Equal(sequence, f.Read().Sequence);
        using var control = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (valid, checkpoint) = await Ready(control);
        var matched = Assert.IsType<Settlement.Closed>(await control.Materializer().Settle(control.Lease(T), control.Op(), valid.Execution.Launch, checkpoint));
        Assert.Equal(2, control.Read().Captures[matched.Capture].Count);
        CloseAndPublish(control, valid, checkpoint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async AsyncTask A_missing_turn_end_log_records_failure_and_allows_only_confirmed_recovery(bool missingFile)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (ready, checkpoint) = await Ready(f);
        var launch = ready.Execution.Launch;
        var folder = f.Store.AttemptFolder(W, f.RunId, T, launch.Attempt);
        if (missingFile) File.Delete(Path.Combine(folder, "events.jsonl"));
        else KeepRunningLog(folder);
        f.ReleaseControl();
        Assert.Equal("RecoveryEvidenceInsufficient", Rejection(await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), launch)));
        var disposed = Assert.Single(f.Read().Dispositions).Value;
        Assert.Equal("Missing turn-end log evidence.", Assert.IsType<CaptureDisposition.Failed>(disposed.Disposition).Detail);
        Assert.Empty(f.Read().TurnClosures);
        Assert.Equal("UnresolvedOwnership", ProblemName(f.Store.CloseAttempt(f.Permit, f.Op(), launch.Attempt, TerminalAttemptOutcome.Succeeded, checkpoint)));
        Assert.Equal("SettlementPending", ProblemName(f.Store.Recover(f.Lease(T), f.Op(), launch.Attempt)));
        Assert.Equal("ConfirmationRequired", ProblemName(f.Store.Recover(f.Lease(T), f.Op(), launch.Attempt, RecoveryOutcome.Stopped)));
        var closed = Assert.IsType<RunDecision.Recorded>(f.Store.Recover(f.Lease(T), f.Op(), launch.Attempt,
            RecoveryOutcome.Stopped, f.Op(), "The person stopped the lost turn."));
        Assert.Equal("Stopped", Assert.IsType<AttemptEnd.Recovered>(closed.Record.Closures[launch.Attempt]).Outcome.ToString());
        Assert.Equal(0, f.Read().Results.Count(result => result.Task == T));
        Assert.Empty(f.Read().UnresolvedClaims);
        Assert.Equal("Closed", Assert.Single(Assert.IsType<RecoveryRead.Loaded>(f.Store.InspectRecovery(W, f.RunId)).Attempts).State.ToString());
        Assert.Equal("OutcomeMismatch", Assert.IsType<Publication.Rejected>(f.Materializer().Publish(f.Lease(T), f.Op(), launch.Attempt)).Reason.Problem.ToString());
        using var control = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (valid, log) = await Ready(control);
        Assert.IsType<Settlement.Closed>(await control.Materializer().Settle(control.Lease(T), control.Op(), valid.Execution.Launch, log));
        Assert.Single(control.Read().TurnClosures);
        CloseAndPublish(control, valid, log);
    }

    [Fact]
    public async AsyncTask A_recorded_failure_closes_when_strict_log_is_restored()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (ready, log) = await Ready(f);
        var folder = f.Store.AttemptFolder(W, f.RunId, T, ready.Execution.Launch.Attempt);
        var path = Path.Combine(folder, "events.jsonl");
        var bytes = File.ReadAllBytes(path);
        KeepRunningLog(folder);
        f.ReleaseControl();
        Assert.Equal("RecoveryEvidenceInsufficient", Rejection(await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), ready.Execution.Launch)));
        var sequence = f.Read().Sequence;
        Assert.Equal("RecoveryEvidenceInsufficient", Rejection(await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), ready.Execution.Launch)));
        Assert.Equal(sequence, f.Read().Sequence);
        Assert.Empty(f.Read().TurnClosures);
        File.WriteAllBytes(path, bytes);
        var closed = Assert.IsType<Settlement.Closed>(await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), ready.Execution.Launch));
        Assert.Equal("Missing turn-end log evidence.", Assert.IsType<CaptureDisposition.Failed>(closed.Disposition).Detail);
        Assert.Single(f.Read().TurnClosures);
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), ready.Execution.Launch.Attempt, TerminalAttemptOutcome.Succeeded, log));
        Assert.Equal("Missing turn-end log evidence.", Assert.IsType<Publication.Blocked>(f.Materializer().Publish(
            f.Lease(T), f.Op(), ready.Execution.Launch.Attempt)).Block.Detail);
        Assert.Equal(0, f.Read().Results.Count(result => result.Task == T));
        sequence = f.Read().Sequence;
        SettlementAssertions.Equal(closed, await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), ready.Execution.Launch));
        Assert.Equal(sequence, f.Read().Sequence);
        using var control = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (valid, checkpoint) = await Ready(control);
        Assert.IsType<Settlement.Closed>(await control.Materializer().Settle(control.Lease(T), control.Op(), valid.Execution.Launch, checkpoint));
        CloseAndPublish(control, valid, checkpoint);
    }

    [Fact]
    public async AsyncTask A_missing_root_observation_stays_uncertain()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("keep.txt", "keep\n", ready.Checkout);
        await ProcessCrash(f, ready, "journal.root-exit.before");
        Assert.Equal("Uncertain", Assert.Single(Assert.IsType<RecoveryRead.Loaded>(f.Store.InspectRecovery(W, f.RunId)).Attempts).State.ToString());
        Assert.Equal("UnresolvedOwnership", Assert.IsType<RootObservation.Rejected>(f.Materializer().ObserveRootExit(
            f.Lease(T), f.Op(), ready.Execution.Launch, new RootExit.Exited(0))).Reason.Problem.ToString());
        Assert.Equal("UnresolvedOwnership", Rejection(await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), ready.Execution.Launch)));
        Assert.Equal(0, f.Read().Results.Count(result => result.Task == T));
        Assert.Empty(f.Read().RootExits);
        Assert.Equal("keep\n", File.ReadAllText(Path.Combine(ready.Checkout, "keep.txt")));
        Assert.IsType<RunDecision.Recorded>(f.Store.Recover(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt,
            RecoveryOutcome.Stopped, f.Op(), "The person stopped the lost root."));
        Assert.Empty(f.Read().UnresolvedClaims);
        using var control = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (valid, log) = await Ready(control);
        Assert.Single(control.Read().RootExits);
        Assert.IsType<Settlement.Closed>(await control.Materializer().Settle(control.Lease(T), control.Op(), valid.Execution.Launch, log));
        CloseAndPublish(control, valid, log);
    }

    [Theory]
    [InlineData("journal.capture-1.before", false)]
    [InlineData("journal.capture-1.after", false)]
    [InlineData("journal.close-turn.before", false)]
    [InlineData("journal.capture-1.before", true)]
    [InlineData("journal.capture-1.after", true)]
    [InlineData("journal.close-turn.before", true)]
    public async AsyncTask Closure_waits_for_a_settling_launch(string point, bool takeover)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (ready, checkpointAtExit) = await Ready(f);
        if (takeover)
        {
            await Crash(f, ready, checkpointAtExit, f.Op(), point);
            f.ReleaseControl();
        }
        else await Crash(f, ready, checkpointAtExit, f.Op(), point);
        var launch = ready.Execution.Launch;
        var log = Checkpoint(f.Store.AttemptFolder(W, f.RunId, T, launch.Attempt));
        _ = f.Lease(T);
        var sequence = f.Read().Sequence;
        Assert.Equal("SettlementPending", ProblemName(f.Store.Recover(f.Lease(T), f.Op(), launch.Attempt)));
        Assert.Equal("SettlementPending", ProblemName(f.Store.Recover(f.Lease(T), f.Op(), launch.Attempt,
            RecoveryOutcome.Stopped, f.Op(), "Confirmed.")));
        Assert.Equal(takeover ? "UnresolvedOwnership" : "SettlementPending", ProblemName(f.Store.CloseAttempt(f.Permit, f.Op(), launch.Attempt, TerminalAttemptOutcome.Succeeded, log)));
        Assert.Equal(takeover ? "UnresolvedOwnership" : "SettlementPending", ProblemName(f.Store.CloseTurn(f.Permit, f.Op(), launch, log)));
        Assert.Equal(sequence, f.Read().Sequence);
        var settlement = Assert.IsType<Settlement.Closed>(await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), launch));
        Assert.Single(f.Read().TurnClosures);
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), launch.Attempt, TerminalAttemptOutcome.Succeeded, log));
        if (point == "journal.capture-1.before") Assert.IsType<CaptureDisposition.Failed>(settlement.Disposition);
        else
        {
            Assert.IsType<CaptureDisposition.Matched>(settlement.Disposition);
            AssertPublished(f, ready);
        }
        using var control = new RunFixtures();
        control.Approve();
        var reserved = control.Reserve();
        control.Claim(reserved);
        var checkpoint = control.WriteLog(reserved);
        Assert.IsType<RunDecision.Recorded>(control.Store.CloseTurn(control.Permit, control.Op(), new(A1, 1), checkpoint));
        Assert.IsType<RunDecision.Recorded>(control.Store.CloseAttempt(control.Permit, control.Op(), A1, TerminalAttemptOutcome.Succeeded, checkpoint));
    }

    [Theory]
    [InlineData("journal.capture-disposition.before", false)]
    [InlineData("journal.capture-disposition.before", true)]
    [InlineData("journal.close-turn.before", false)]
    [InlineData("journal.close-turn.before", true)]
    public async AsyncTask A_decided_settlement_whose_log_moved_on_closes_by_confirmed_recovery(string point, bool takeover)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (ready, log) = await Ready(f);
        var launch = ready.Execution.Launch;
        await Crash(f, ready, log, f.Op(), point);
        using (var attempt = AttemptLog.Open(f.Store.AttemptFolder(W, f.RunId, T, launch.Attempt)))
            attempt.Append(new AttemptEvent.HandedToTerminal(At, ready.Checkout, "codex resume fixture"));
        if (takeover) f.ReleaseControl();
        Assert.Equal("EvidenceMismatch", Rejection(await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), launch)));
        Assert.IsType<CaptureDisposition.Matched>(Assert.Single(f.Read().Dispositions).Value.Disposition);
        var sequence = f.Read().Sequence;
        Assert.Equal("EvidenceMismatch", Rejection(await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), launch)));
        Assert.Equal(sequence, f.Read().Sequence);
        Assert.Equal("SettlementPending", ProblemName(f.Store.Recover(f.Lease(T), f.Op(), launch.Attempt)));
        Assert.Equal("ConfirmationRequired", ProblemName(f.Store.Recover(f.Lease(T), f.Op(), launch.Attempt, RecoveryOutcome.Stopped)));
        Assert.Equal(sequence, f.Read().Sequence);
        var recovered = Assert.IsType<RunDecision.Recorded>(f.Store.Recover(f.Lease(T), f.Op(), launch.Attempt,
            RecoveryOutcome.Stopped, f.Op(), "The person stopped the turn after its log moved on."));
        Assert.Equal("Stopped", Assert.IsType<AttemptEnd.Recovered>(recovered.Record.Closures[launch.Attempt]).Outcome.ToString());
        Assert.Empty(f.Read().TurnClosures);
        Assert.Empty(f.Read().UnresolvedClaims);
        Assert.Equal("OutcomeMismatch", Assert.IsType<Publication.Rejected>(f.Materializer().Publish(f.Lease(T), f.Op(), launch.Attempt)).Reason.Problem.ToString());
        Assert.Equal(0, f.Read().Results.Count(result => result.Task == T));
        using var control = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (valid, checkpoint) = await Ready(control);
        await Crash(control, valid, checkpoint, control.Op(), point);
        if (takeover) control.ReleaseControl();
        Assert.Equal("SettlementPending", ProblemName(control.Store.Recover(control.Lease(T), control.Op(), valid.Execution.Launch.Attempt)));
        Assert.IsType<CaptureDisposition.Matched>(Assert.IsType<Settlement.Closed>(await control.Materializer().RecoverSettlement(
            control.Lease(T), control.Op(), valid.Execution.Launch)).Disposition);
        CloseAndPublish(control, valid, checkpoint);
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("rewritten")]
    public async AsyncTask A_decided_settlement_stays_pending_unless_its_log_reads_as_moved_on(string state)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (ready, log) = await Ready(f);
        var launch = ready.Execution.Launch;
        await Crash(f, ready, log, f.Op(), "journal.close-turn.before");
        var folder = f.Store.AttemptFolder(W, f.RunId, T, launch.Attempt);
        using (var attempt = AttemptLog.Open(folder))
            attempt.Append(new AttemptEvent.HandedToTerminal(At, ready.Checkout, "codex resume fixture"));
        var lease = f.Lease(T);
        var sequence = f.Read().Sequence;
        var operation = f.Op();
        var confirmation = f.Op();
        var path = Path.Combine(folder, "events.jsonl");
        var moved = File.ReadAllBytes(path);
        if (state == "rewritten")
        {
            var rewritten = moved.ToArray();
            rewritten[0] = (byte)' ';
            File.WriteAllBytes(path, rewritten);
        }
        using (state == "locked" ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None) : null)
            Assert.Equal("SettlementPending", ProblemName(f.Store.Recover(lease, operation, launch.Attempt,
                RecoveryOutcome.Stopped, confirmation, "The person stopped the turn after its log moved on.")));
        File.WriteAllBytes(path, moved);
        Assert.Equal(sequence, f.Read().Sequence);
        var recovered = Assert.IsType<RunDecision.Recorded>(f.Store.Recover(lease, operation, launch.Attempt,
            RecoveryOutcome.Stopped, confirmation, "The person stopped the turn after its log moved on."));
        Assert.Equal("Stopped", Assert.IsType<AttemptEnd.Recovered>(recovered.Record.Closures[launch.Attempt]).Outcome.ToString());
        Assert.Equal(sequence + 1, f.Read().Sequence);
    }

    [Fact]
    public async AsyncTask The_reducer_refuses_a_capture_less_turn_closure_on_a_fenced_launch()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (ready, log) = await Ready(f);
        var launch = ready.Execution.Launch;
        await Crash(f, ready, log, f.Op(), "journal.close-turn.before");
        f.ReleaseControl();
        _ = f.Lease(T);
        var record = f.Read();
        Assert.Equal(new[] { launch }, record.Fenced);
        var capture = Assert.Single(record.Dispositions).Key;
        RunRead Apply(RunEvent e) => RunReducer.Apply(W, f.RunId, record, new(3, record.Sequence + 1, f.Op(), Prompt, At, e));
        Assert.Equal(new RunRejection(RunProblem.InvalidClaim, record.Sequence + 1),
            Assert.IsType<RunRead.Rejected>(Apply(new RunEvent.TurnClosed(launch, log))).Reason);
        Assert.Equal(capture, Assert.IsType<RunRead.Loaded>(Apply(new RunEvent.TurnClosed(launch, log) { Capture = capture })).Record.Settlements[launch]);
    }

    [Fact]
    public async AsyncTask A_late_close_turn_gets_the_closed_turns_answer()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (ready, log) = await Ready(f);
        var launch = ready.Execution.Launch;
        var closed = Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(T), f.Op(), launch, log));
        var sequence = f.Read().Sequence;
        var repeated = Assert.IsType<RunDecision.Existing>(f.Store.CloseTurn(f.Permit, f.Op(), launch, log, closed.Capture));
        Assert.Equal(closed.Capture, Assert.IsType<RunEvent.TurnClosed>(repeated.Event).Capture);
        Assert.Equal("EvidenceMismatch", ProblemName(f.Store.CloseTurn(f.Permit, f.Op(), launch, log)));
        Assert.Equal(sequence, f.Read().Sequence);
        CloseAndPublish(f, ready, log);
    }

    [Fact]
    public async AsyncTask Fenced_launches_accept_only_settlement_evidence()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (ready, log) = await Ready(f);
        var operation = f.Op();
        await Crash(f, ready, log, operation, "journal.capture-disposition.before");
        var template = Assert.Single(f.Read().Captures).Value;
        using var target = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (valid, checkpoint) = await Ready(target);
        var first = template[0] with { Launch = valid.Execution.Launch, Log = checkpoint };
        var second = template[1] with { Launch = first.Launch, Log = checkpoint };
        Assert.Equal("InvalidData", ProblemName(target.Store.Record(target.Permit, target.Op(), new RunEvent.TurnCaptured(first with { Recovery = true }))));
        Assert.IsType<RunDecision.Recorded>(target.Store.Record(target.Permit, target.Op(), new RunEvent.TurnCaptured(first)));
        target.ReleaseControl();
        var lease = target.Lease(T);
        Assert.Equal("InvalidClaim", ProblemName(target.Store.Record(target.Permit, target.Op(), new RunEvent.TurnCaptured(first))));
        Assert.Equal("InvalidClaim", ProblemName(target.Store.Record(target.Permit, target.Op(), new RunEvent.TurnCaptured(second))));
        Assert.Equal("InvalidData", ProblemName(target.Store.Record(target.Permit, target.Op(), new RunEvent.TurnCaptured(first with { Recovery = true }))));
        Assert.Equal("InvalidClaim", ProblemName(target.Store.Record(target.Permit, target.Op(), target.Read().RootExits[first.Launch])));
        Assert.Equal("UnresolvedOwnership", ProblemName(target.Store.CloseTurn(target.Permit, target.Op(), first.Launch, checkpoint)));
        Assert.Single(target.Read().Captures[first.Capture]);
        Assert.Equal("EvidenceMismatch", ProblemName(target.Store.Record(target.Permit, target.Op(),
            new RunEvent.TurnCaptured(second with { Recovery = true, Log = checkpoint with { ByteLength = checkpoint.ByteLength + 1 } }))));
        Assert.IsType<RunDecision.Recorded>(target.Store.Record(target.Permit, target.Op(), new RunEvent.TurnCaptured(second with { Recovery = true })));
        Assert.IsType<RunDecision.Recorded>(target.Store.Record(target.Permit, target.Op(), new RunEvent.CaptureDisposed(first.Capture,
            first.Launch, new CaptureDisposition.Matched())));
        Assert.IsType<RunDecision.Recorded>(target.Store.CloseTurn(target.Permit, target.Op(), first.Launch, checkpoint, first.Capture));
        Assert.Equal(new[] { 1, 2 }, target.Read().Captures[first.Capture].Select(o => o.Ordinal));
        Assert.Single(target.Read().TurnClosures);
        Assert.IsType<Settlement.Closed>(await target.Materializer().RecoverSettlement(lease, target.Op(), first.Launch));
        using var fresh = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (freshReady, freshLog) = await Ready(fresh);
        fresh.ReleaseControl();
        _ = fresh.Lease(T);
        Assert.Equal("InvalidClaim", ProblemName(fresh.Store.Record(fresh.Permit, fresh.Op(), new RunEvent.TurnCaptured(
            first with { Launch = freshReady.Execution.Launch, Log = freshLog }))));
        Assert.Equal("InvalidClaim", ProblemName(fresh.Store.Record(fresh.Permit, fresh.Op(), new RunEvent.TurnCaptured(
            second with { Launch = freshReady.Execution.Launch, Log = freshLog, Recovery = true }))));
        Assert.Empty(fresh.Read().Captures);
        Assert.Equal(2, target.Read().Captures[first.Capture].Count);
    }

    [Fact]
    public async AsyncTask InspectRecovery_distinguishes_settling_from_uncertain()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(U), Writer(C)));
        var (settling, _) = await Ready(f);
        var uncertain = Assert.IsType<Preparation.Ready>(await f.Prepare(U));
        RootExitTests.Claim(f, uncertain);
        var closed = Assert.IsType<Preparation.Ready>(await f.Prepare(C));
        await f.Close(closed);
        Assert.Equal(new[] { "Settling", "Uncertain", "Closed" }, Assert.IsType<RecoveryRead.Loaded>(
            f.Store.InspectRecovery(W, f.RunId)).Attempts.Select(a => a.State.ToString()));
        Assert.Equal(settling.Execution.Launch, Assert.Single(f.Read().Claims.Keys, f.Read().Settling));
    }

    [Fact]
    public async AsyncTask Two_entry_points_converge_on_one_settlement()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (ready, log) = await Ready(f);
        var operation = f.Op();
        await Crash(f, ready, log, operation, "journal.capture-1.after");
        await Crash(f, ready, log, operation, "journal.capture-2.after");
        var capture = Assert.Single(f.Read().Captures).Key;
        var root = new OperationId(capture.Value);
        Assert.True(f.Read().Captures[capture][1].Recovery);
        var closed = Assert.IsType<Settlement.Closed>(await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), ready.Execution.Launch));
        Assert.IsType<CaptureDisposition.Matched>(closed.Disposition);
        Assert.Equal(OperationIds.Derive(operation, "capture-1"), Assert.Single(f.Read().Receipts.Values, e =>
            e.Event is RunEvent.TurnCaptured { Observation.Ordinal: 1 }).Operation);
        foreach (var step in new[] { "capture-2", "capture-disposition", "close-turn" })
            Assert.True(f.Read().Receipts.ContainsKey(OperationIds.Derive(root, step)));
        Assert.Single(f.Read().Dispositions);
        Assert.Single(f.Read().TurnClosures);
        var sequence = f.Read().Sequence;
        SettlementAssertions.Equal(closed, await f.Materializer().Settle(f.Lease(T), operation, ready.Execution.Launch, log));
        Assert.Equal(sequence, f.Read().Sequence);
        CloseAndPublish(f, ready, log);
    }

    [Fact]
    public async AsyncTask Recovery_uses_the_first_observations_log_instead_of_a_later_checkpoint()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (ready, log) = await Ready(f);
        await Crash(f, ready, log, f.Op(), "journal.capture-1.after");
        var first = Assert.Single(Assert.Single(f.Read().Captures).Value);
        var canonical = RunJournal.Canonical(first);
        using (var attempt = AttemptLog.Open(f.Store.AttemptFolder(W, f.RunId, T, ready.Execution.Launch.Attempt)))
            attempt.Append(new AttemptEvent.HandedToTerminal(At, ready.Checkout, "codex resume fixture"));
        f.ReleaseControl();
        Assert.Equal("EvidenceMismatch", Rejection(await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), ready.Execution.Launch)));
        var failure = Assert.IsType<CaptureDisposition.Failed>(Assert.Single(f.Read().Dispositions).Value.Disposition);
        Assert.Equal("EvidenceMismatch", failure.Detail);
        Assert.Single(f.Read().Captures[first.Capture]);
        Assert.Equal(canonical, RunJournal.Canonical(f.Read().Captures[first.Capture][0]));
        Assert.Empty(f.Read().TurnClosures);
        using var control = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (valid, checkpoint) = await Ready(control);
        await Crash(control, valid, checkpoint, control.Op(), "journal.capture-1.after");
        control.ReleaseControl();
        var closed = Assert.IsType<Settlement.Closed>(await control.Materializer().RecoverSettlement(control.Lease(T), control.Op(), valid.Execution.Launch));
        Assert.IsType<CaptureDisposition.Matched>(closed.Disposition);
        Assert.Equal(2, control.Read().Captures[closed.Capture].Count);
        Assert.Single(control.Read().TurnClosures);
    }

    [Fact]
    public async AsyncTask A_failed_settlement_requires_recovered_even_with_a_terminal_log()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (ready, log) = await Ready(f);
        var launch = ready.Execution.Launch;
        var capture = new CaptureId(Id(9000));
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, f.Op(), new RunEvent.CaptureDisposed(capture, launch,
            new CaptureDisposition.Failed(MaterializationProblem.InputUnavailable, "Missing turn-end log evidence.", []))));
        Assert.Equal("SettlementPending", ProblemName(f.Store.CloseAttempt(f.Permit, f.Op(), launch.Attempt,
            TerminalAttemptOutcome.Succeeded, log)));
        Assert.Equal("SettlementPending", ProblemName(f.Store.Recover(f.Lease(T), f.Op(), launch.Attempt)));
        Assert.Equal("ConfirmationRequired", ProblemName(f.Store.Recover(f.Lease(T), f.Op(), launch.Attempt, RecoveryOutcome.Stopped)));
        Assert.Equal("ConfirmationRequired", ProblemName(f.Store.Recover(f.Lease(T), f.Op(), launch.Attempt,
            RecoveryOutcome.Stopped, f.Op(), " ")));
        var recovered = Assert.IsType<RunDecision.Recorded>(f.Store.Recover(f.Lease(T), f.Op(), launch.Attempt,
            RecoveryOutcome.Stopped, f.Op(), "Confirmed."));
        Assert.Equal("Stopped", Assert.IsType<AttemptEnd.Recovered>(recovered.Record.Closures[launch.Attempt]).Outcome.ToString());
        Assert.Equal(0, f.Read().Results.Count(result => result.Task == T));
        using var control = new RunFixtures();
        control.Approve();
        var reserved = control.Reserve();
        control.Claim(reserved);
        control.WriteLog(reserved);
        var logged = Assert.IsType<RunDecision.Recorded>(control.Store.Recover(control.Lease(T), control.Op(), A1));
        Assert.Equal("Succeeded", Assert.IsType<AttemptEnd.Logged>(logged.Record.Closures[A1]).Outcome.ToString());
        using var published = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (valid, checkpoint) = await Ready(published);
        Assert.IsType<Settlement.Closed>(await published.Materializer().Settle(published.Lease(T), published.Op(), valid.Execution.Launch, checkpoint));
        CloseAndPublish(published, valid, checkpoint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async AsyncTask Recovery_outbox_failure_evidence_is_bound_to_the_capture(bool takeover)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (ready, log) = await Ready(f);
        WriteOutbox(ready);
        var operation = new OperationId(Id(9000));
        await Crash(f, ready, log, operation, "journal.capture-1.after");
        var first = Assert.Single(Assert.Single(f.Read().Captures).Value);
        File.WriteAllText(Path.Combine(ready.Checkout, ready.Execution.OutboxPath, "manifest.json"), "{\"schema\":0,\"artifacts\":[]}");
        if (takeover) f.ReleaseControl();
        var closed = Assert.IsType<Settlement.Closed>(await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), ready.Execution.Launch));
        var failure = Assert.IsType<CaptureDisposition.Failed>(closed.Disposition);
        Assert.Equal("InputUnavailable", failure.Problem.ToString());
        Assert.Equal("Attempt 00000000-0000-0000-0000-000000000102, manifest .idp/outbox/00000000-0000-0000-0000-000000000102/manifest.json: The outbox manifest must contain schema 1 and an artifacts array only.", failure.Detail);
        var evidence = Assert.Single(failure.Evidence);
        Assert.Equal("evidence/e047f4d7-3c7c-8b55-861e-aea8c9c4c997/outbox-rejection.txt", evidence.RelativePath);
        Assert.Equal(failure.Detail, System.Text.Encoding.UTF8.GetString(RunStorage.Read(new RunStorage(f.Git.Folder, W, f.RunId).Folder,
            evidence.RelativePath, evidence.Content, evidence.ByteLength)));
        Assert.Single(f.Read().Captures[first.Capture]);
        Assert.Equal(new byte[] { 67, 0, 127 }, RunStorage.Read(new RunStorage(f.Git.Folder, W, f.RunId).Folder,
            first.Artifacts[0].StoredPath, first.Artifacts[0].Content, 3));
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), ready.Execution.Launch.Attempt,
            TerminalAttemptOutcome.Succeeded, log));
        Assert.Equal(failure.Detail, Assert.IsType<Publication.Blocked>(f.Materializer().Publish(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt)).Block.Detail);
        Assert.Equal(0, f.Read().Results.Count(result => result.Task == T));
        var sequence = f.Read().Sequence;
        SettlementAssertions.Equal(closed, await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), ready.Execution.Launch));
        Assert.Equal(sequence, f.Read().Sequence);
        using var control = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (valid, checkpoint) = await Ready(control);
        WriteOutbox(valid);
        var match = Assert.IsType<Settlement.Closed>(await control.Materializer().Settle(control.Lease(T), control.Op(), valid.Execution.Launch, checkpoint));
        Assert.IsType<CaptureDisposition.Matched>(match.Disposition);
        Assert.Equal(2, control.Read().Captures[match.Capture].Count);
        CloseAndPublish(control, valid, checkpoint);
    }

    public static IEnumerable<object[]> CrashPoints()
    {
        foreach (var point in new[] { "journal.root-exit.before", "journal.root-exit.after", "git.capture-1.before", "git.capture-1.after",
            "journal.capture-1.before", "journal.capture-1.after", "git.pin.before", "git.pin.after", "refs.snapshot.after",
            "outbox.before", "outbox.after", "artifact.payload.before", "artifact.payload.after", "git.capture-2.before", "git.capture-2.after", "journal.capture-2.before",
            "journal.capture-2.after", "journal.capture-disposition.before", "journal.capture-disposition.after", "journal.close-turn.before", "journal.close-turn.after" })
            foreach (var takeover in new[] { false, true }) yield return [point, takeover, false, false];
        foreach (var point in new[] { "git.capture-2.before", "git.capture-2.after", "git.pin.before", "git.pin.after", "refs.snapshot.after",
            "outbox.before", "outbox.after", "artifact.payload.before", "artifact.payload.after", "journal.capture-2.before", "journal.capture-2.after",
            "journal.capture-disposition.before", "journal.capture-disposition.after", "journal.close-turn.before", "journal.close-turn.after" })
            foreach (var takeover in new[] { false, true }) yield return [point, takeover, true, false];
        foreach (var point in new[] { "journal.capture-disposition.before", "journal.capture-disposition.after", "journal.close-turn.before", "journal.close-turn.after" })
            foreach (var takeover in new[] { false, true }) yield return [point, takeover, true, true];
    }

    [Theory]
    [MemberData(nameof(CrashPoints))]
    public async AsyncTask Every_settlement_crash_converges_without_a_second_claim(string point, bool takeover, bool recovering, bool missingCapture = false)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        WriteOutbox(ready);
        var launch = ready.Execution.Launch;
        var operation = f.Op();
        LogCheckpoint log;
        if (point.StartsWith("journal.root-exit", StringComparison.Ordinal))
        {
            RootExitTests.Claim(f, ready);
            f.Git.Write("result.txt", "done\n", ready.Checkout);
            Assert.Throws<SettlementCrash>(() => f.Materializer(probe: p => { if (p == point) throw new SettlementCrash(); })
                .ObserveRootExit(f.Lease(T), f.Op(), launch, new RootExit.Exited(0)));
            if (takeover && !f.Read().RootExits.ContainsKey(launch))
            {
                f.ReleaseControl();
                Assert.Equal("UnresolvedOwnership", Rejection(await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), launch)));
                Assert.IsType<RunDecision.Recorded>(f.Store.Recover(f.Lease(T), f.Op(), launch.Attempt,
                    RecoveryOutcome.Stopped, f.Op(), "Confirmed."));
                Assert.Equal((0, 1), (f.Read().Results.Count, f.Read().Claims.Count));
                var endSequence = f.Read().Sequence;
                Assert.Equal("UnresolvedOwnership", Rejection(await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), launch)));
                Assert.Equal(endSequence, f.Read().Sequence);
                using var control = new PreparationFixture(FixtureWorkflow(Writer(T)));
                var (valid, checkpoint) = await Ready(control);
                Assert.IsType<Settlement.Closed>(await control.Materializer().Settle(control.Lease(T), control.Op(), valid.Execution.Launch, checkpoint));
                CloseAndPublish(control, valid, checkpoint);
                return;
            }
            log = f.ObserveAndLog(ready);
        }
        else
        {
            f.Git.Write("result.txt", "done\n", ready.Checkout);
            log = f.ObserveAndLog(ready);
            if (recovering && !missingCapture) await Crash(f, ready, log, operation, "journal.capture-1.after");
            var crashing = f.Materializer(probe: p => { if (p == point) throw new SettlementCrash(); });
            await Assert.ThrowsAsync<SettlementCrash>(() => (recovering
                ? crashing.RecoverSettlement(f.Lease(T), operation, launch)
                : crashing.Settle(f.Lease(T), operation, launch, log)).AsTask());
        }
        var first = f.Read().Captures.Values.SelectMany(o => o).FirstOrDefault();
        var firstBytes = first is null ? null : RunJournal.Canonical(first);
        if (takeover) f.ReleaseControl();
        var missing = missingCapture || takeover && first is null;
        var closed = Assert.IsType<Settlement.Closed>(await (takeover || recovering
            ? f.Materializer().RecoverSettlement(f.Lease(T), operation, launch)
            : f.Materializer().Settle(f.Lease(T), operation, launch, log)));
        if (missing)
        {
            Assert.Equal("Missing turn-end capture evidence.", Assert.IsType<CaptureDisposition.Failed>(closed.Disposition).Detail);
            Assert.Empty(f.Read().Captures[closed.Capture]);
        }
        else
        {
            Assert.IsType<CaptureDisposition.Matched>(closed.Disposition);
            Assert.Equal(2, f.Read().Captures[closed.Capture].Count);
            Assert.Equal(new byte[] { 67, 0, 127 }, RunStorage.Read(new RunStorage(f.Git.Folder, W, f.RunId).Folder,
                f.Read().Captures[closed.Capture][1].Artifacts[0].StoredPath, f.Read().Captures[closed.Capture][1].Artifacts[0].Content, 3));
            if (firstBytes is not null) Assert.Equal(firstBytes, RunJournal.Canonical(f.Read().Captures[closed.Capture][0]));
        }
        var retainedPins = IDevelop.Core.Tests.Git.GitFixture.Read(f.Git.Open().RefSnapshot(RunLayout.PinPrefix(f.Read().RunKey!)));
        Assert.Contains(RunLayout.RootPin(f.Read().RunKey!, f.Read().TaskKeys[T], launch), retainedPins.Keys);
        if (!missing)
        {
            Assert.Contains(RunLayout.CapturePin(f.Read().RunKey!, f.Read().TaskKeys[T], launch, 1), retainedPins.Keys);
            Assert.Contains(RunLayout.CapturePin(f.Read().RunKey!, f.Read().TaskKeys[T], launch, 2), retainedPins.Keys);
        }
        var sequence = f.Read().Sequence;
        SettlementAssertions.Equal(closed, await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), launch));
        Assert.Equal(sequence, f.Read().Sequence);
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), launch.Attempt, TerminalAttemptOutcome.Succeeded, log));
        SettlementAssertions.Equal(closed, await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), launch));
        if (missing)
        {
            Assert.Equal("Missing turn-end capture evidence.", Assert.IsType<Publication.Blocked>(f.Materializer().Publish(
                f.Lease(T), f.Op(), launch.Attempt)).Block.Detail);
            Assert.Equal((0, 1), (f.Read().Results.Count, f.Read().Claims.Count));
            using var control = new PreparationFixture(FixtureWorkflow(Writer(T)));
            var (valid, checkpoint) = await Ready(control);
            Assert.IsType<Settlement.Closed>(await control.Materializer().Settle(control.Lease(T), control.Op(), valid.Execution.Launch, checkpoint));
            CloseAndPublish(control, valid, checkpoint);
        }
        else
        {
            AssertPublished(f, ready);
            Assert.Equal((1, 1), (f.Read().Results.Count, f.Read().Claims.Count));
        }
        Assert.Single(f.Read().Claims);
        sequence = f.Read().Sequence;
        SettlementAssertions.Equal(closed, await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), launch));
        Assert.Equal(sequence, f.Read().Sequence);
    }

    [Theory]
    [InlineData("git.pin.before", false)]
    [InlineData("git.pin.after", false)]
    [InlineData("git.pin.before", true)]
    [InlineData("git.pin.after", true)]
    public async AsyncTask A_root_pin_without_its_observation_never_supplies_settlement_evidence(string point, bool takeover)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        RootExitTests.Claim(f, ready);
        f.Git.Write("result.txt", "done\n", ready.Checkout);
        Assert.Throws<SettlementCrash>(() => f.Materializer(probe: p => { if (p == point) throw new SettlementCrash(); })
            .ObserveRootExit(f.Lease(T), f.Op(), ready.Execution.Launch, new RootExit.Exited(0)));
        Assert.Empty(f.Read().RootExits);
        var pins = IDevelop.Core.Tests.Git.GitFixture.Read(f.Git.Open().RefSnapshot(RunLayout.PinPrefix(f.Read().RunKey!)));
        if (point == "git.pin.before") Assert.Empty(pins);
        else Assert.Equal(new[] { f.A }, pins.Values);
        if (takeover)
        {
            f.ReleaseControl();
            Assert.Equal("UnresolvedOwnership", Rejection(await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), ready.Execution.Launch)));
            Assert.IsType<RunDecision.Recorded>(f.Store.Recover(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt,
                RecoveryOutcome.Stopped, f.Op(), "Confirmed."));
            Assert.Equal(0, f.Read().Results.Count(result => result.Task == T));
            using var control = new PreparationFixture(FixtureWorkflow(Writer(T)));
            var (valid, checkpoint) = await Ready(control);
            Assert.IsType<Settlement.Closed>(await control.Materializer().Settle(control.Lease(T), control.Op(), valid.Execution.Launch, checkpoint));
            CloseAndPublish(control, valid, checkpoint);
        }
        else
        {
            var log = f.ObserveAndLog(ready);
            Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(T), f.Op(), ready.Execution.Launch, log));
            CloseAndPublish(f, ready, log);
        }
        Assert.Single(f.Read().Claims);
        Assert.Equal("done\n", File.ReadAllText(Path.Combine(ready.Checkout, "result.txt")));
        var sequence = f.Read().Sequence;
        if (takeover) Assert.Equal("UnresolvedOwnership", Rejection(await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), ready.Execution.Launch)));
        else Assert.IsType<Settlement.Closed>(await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), ready.Execution.Launch));
        Assert.Equal(sequence, f.Read().Sequence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async AsyncTask An_interrupted_recovery_wait_preserves_the_first_capture_and_waits_again(bool takeover)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (ready, log) = await Ready(f);
        var operation = f.Op();
        await Crash(f, ready, log, operation, "journal.capture-1.after");
        var first = Assert.Single(Assert.Single(f.Read().Captures).Value);
        var bytes = RunJournal.Canonical(first);
        var stale = RunStorage.SafePath(new RunStorage(f.Git.Folder, W, f.RunId).Folder,
            RunStorage.CapturePath(first.Capture, 2, "stale.txt"));
        Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
        File.WriteAllText(stale, "stale\n");
        var sequence = f.Read().Sequence;
        var clock = new ManualTimeProvider();
        clock.Advance(first.Completed - clock.GetUtcNow());
        using var cancel = new CancellationTokenSource();
        var waiting = f.Materializer(clock: clock).RecoverSettlement(f.Lease(T), f.Op(), ready.Execution.Launch, cancel.Token).AsTask();
        Assert.False(waiting.IsCompleted);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(sequence, f.Read().Sequence);
        Assert.Single(f.Read().Captures[first.Capture]);
        if (takeover) f.ReleaseControl();
        var rerun = f.Materializer(clock: clock).RecoverSettlement(f.Lease(T), f.Op(), ready.Execution.Launch).AsTask();
        clock.Advance(TimeSpan.FromMilliseconds(249));
        Assert.False(rerun.IsCompleted);
        Assert.Single(f.Read().Captures[first.Capture]);
        Assert.Equal("stale\n", File.ReadAllText(stale));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var closed = Assert.IsType<Settlement.Closed>(await rerun.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsType<CaptureDisposition.Matched>(closed.Disposition);
        Assert.Equal(2, f.Read().Captures[first.Capture].Count);
        Assert.False(File.Exists(stale));
        Assert.True(f.Read().Captures[first.Capture][1].Recovery);
        Assert.Equal(bytes, RunJournal.Canonical(f.Read().Captures[first.Capture][0]));
        CloseAndPublish(f, ready, log);
        sequence = f.Read().Sequence;
        SettlementAssertions.Equal(closed, await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), ready.Execution.Launch));
        Assert.Equal(sequence, f.Read().Sequence);
    }

    private static void WriteOutbox(Preparation.Ready ready)
    {
        var folder = Path.Combine(ready.Checkout, ready.Execution.OutboxPath);
        File.WriteAllBytes(Path.Combine(folder, "payload.bin"), [67, 0, 127]);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), "{\"schema\":1,\"artifacts\":[{\"name\":\"payload\",\"path\":\"payload.bin\"}]}");
    }

    private static async System.Threading.Tasks.Task<(Preparation.Ready Ready, LogCheckpoint Log)> Ready(PreparationFixture f)
    {
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("result.txt", "done\n", ready.Checkout);
        return (ready, f.ObserveAndLog(ready));
    }

    private static AsyncTask Crash(PreparationFixture f, Preparation.Ready ready, LogCheckpoint log, OperationId operation, string point) =>
        Assert.ThrowsAsync<SettlementCrash>(() => f.Materializer(probe: p => { if (p == point) throw new SettlementCrash(); })
            .Settle(f.Lease(T), operation, ready.Execution.Launch, log).AsTask());

    private static async AsyncTask ProcessCrash(PreparationFixture f, Preparation.Ready ready, string point)
    {
        var operation = f.Op();
        f.ReleaseControl();
        using var racer = new Racer("settle-crash", f.Git.Folder, W.Value.ToString("D"), f.RunId.Value.ToString("D"),
            T.Value.ToString("D"), ready.Execution.Launch.Attempt.Value.ToString("D"), operation.Value.ToString("D"), point);
        Assert.Equal("Owned:", await racer.Line());
        Assert.Equal(point, await racer.Line());
        await racer.Exit();
        Assert.Equal(73, racer.ExitCode);
        _ = f.Lease(T);
        Assert.Equal(new[] { ready.Execution.Launch }, f.Read().Fenced);
    }

    private static void KeepRunningLog(string folder)
    {
        var path = Path.Combine(folder, "events.jsonl");
        File.WriteAllText(path, File.ReadLines(path).First() + "\n");
    }

    private static void CloseAndPublish(PreparationFixture f, Preparation.Ready ready, LogCheckpoint log)
    {
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), ready.Execution.Launch.Attempt,
            TerminalAttemptOutcome.Succeeded, log));
        AssertPublished(f, ready);
        Assert.Equal((1, 1), (f.Read().Results.Count, f.Read().Claims.Count));
    }

    private static void AssertPublished(PreparationFixture f, Preparation.Ready ready)
    {
        var result = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt)).Result;
        Assert.Equal("done\n", f.Git.Git("show", Assert.IsType<CodeOutput.Produced>(result.Code).Code.Commit.Hex + ":result.txt"));
    }

    private static string ProblemName(RunDecision decision) => Assert.IsType<RunDecision.Rejected>(decision).Reason.Problem.ToString();
    private static string Rejection(Settlement settlement) => Assert.IsType<Settlement.Rejected>(settlement).Reason.Problem.ToString();
    private sealed class SettlementCrash : Exception;
}

internal static class SettlementAssertions
{
    internal static void Equal(Settlement.Closed expected, Settlement actual) =>
        Assert.Equal(RunJournal.Canonical(expected), RunJournal.Canonical(Assert.IsType<Settlement.Closed>(actual)));
}
