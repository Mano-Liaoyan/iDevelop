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
    public async AsyncTask A_crash_after_the_capture_closes_once_after_takeover()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await ProcessCrash(f, ready, "journal.close-turn.before");
        var closed = Assert.IsType<Settlement.Closed>(await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), ready.Execution.Launch));
        SettlementAssertions.Matched(closed);
        CloseAndAccept(f, ready, f.Read().Captures[ready.Execution.Launch].Log);
        var sequence = f.Read().Sequence;
        SettlementAssertions.Equal(closed, await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), ready.Execution.Launch));
        Assert.Equal(sequence, f.Read().Sequence);
        Assert.Single(f.Read().TurnClosures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async AsyncTask Recovery_closes_a_recorded_capture_with_or_without_an_index_tree(bool retained)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (ready, log) = await Ready(f);
        var capture = new CaptureId(f.Op().Value);
        CaptureObservation? first = null;
        await Assert.ThrowsAsync<SettlementCrash>(() => f.Materializer(probe: point =>
        {
            if (point != "journal.close-turn.before") return;
            first = Assert.Single(f.Read().Captures).Value;
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
        SettlementAssertions.Matched(closed);
        Assert.Equal(canonical, RunJournal.Canonical(recovery.Read().Captures[other.Execution.Launch]));
        Assert.Single(recovery.Read().TurnClosures);
    }

    [Fact]
    public async AsyncTask A_missing_first_capture_after_takeover_closes_with_failure_evidence()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("keep.txt", "keep\n", ready.Checkout);
        await ProcessCrash(f, ready, "journal.capture.before");
        var closed = Assert.IsType<Settlement.Closed>(await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), ready.Execution.Launch));
        var failure = SettlementAssertions.Failed(closed);
        Assert.Equal("InputUnavailable", failure.Problem.ToString());
        Assert.Equal("Missing turn-end capture evidence.", failure.Detail);
        Assert.Empty(f.Read().Captures);
        Assert.Single(f.Read().TurnClosures);
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), ready.Execution.Launch.Attempt,
            TerminalAttemptOutcome.Succeeded, f.Read().TurnClosures[ready.Execution.Launch]));
        Assert.Equal("Missing turn-end capture evidence.", Assert.IsType<Acceptance.Blocked>(f.Materializer().Accept(
            f.Lease(T), f.Op(), ready.Execution.Launch.Attempt)).Block.Detail);
        Assert.Equal(0, f.Read().Results.Count(result => result.Task == T));
        Assert.Equal("keep\n", File.ReadAllText(Path.Combine(ready.Checkout, "keep.txt")));
        var sequence = f.Read().Sequence;
        SettlementAssertions.Equal(closed, await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), ready.Execution.Launch));
        Assert.Equal(sequence, f.Read().Sequence);
        using var control = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (valid, checkpoint) = await Ready(control);
        var matched = Assert.IsType<Settlement.Closed>(await control.Materializer().Settle(control.Lease(T), control.Op(), valid.Execution.Launch, checkpoint));
        SettlementAssertions.Matched(matched);
        CloseAndAccept(control, valid, checkpoint);
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
        Assert.Equal("Missing turn-end log evidence.", RunReducer.CaptureFailure(f.Read(), launch)!.Detail);
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
        Assert.Equal("OutcomeMismatch", Assert.IsType<Acceptance.Rejected>(f.Materializer().Accept(f.Lease(T), f.Op(), launch.Attempt)).Reason.Problem.ToString());
        using var control = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (valid, log) = await Ready(control);
        Assert.IsType<Settlement.Closed>(await control.Materializer().Settle(control.Lease(T), control.Op(), valid.Execution.Launch, log));
        Assert.Single(control.Read().TurnClosures);
        CloseAndAccept(control, valid, log);
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
        Assert.Equal("Missing turn-end log evidence.", SettlementAssertions.Failed(closed).Detail);
        Assert.Single(f.Read().TurnClosures);
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), ready.Execution.Launch.Attempt, TerminalAttemptOutcome.Succeeded, log));
        Assert.Equal("Missing turn-end log evidence.", Assert.IsType<Acceptance.Blocked>(f.Materializer().Accept(
            f.Lease(T), f.Op(), ready.Execution.Launch.Attempt)).Block.Detail);
        Assert.Equal(0, f.Read().Results.Count(result => result.Task == T));
        sequence = f.Read().Sequence;
        SettlementAssertions.Equal(closed, await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), ready.Execution.Launch));
        Assert.Equal(sequence, f.Read().Sequence);
        using var control = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (valid, checkpoint) = await Ready(control);
        Assert.IsType<Settlement.Closed>(await control.Materializer().Settle(control.Lease(T), control.Op(), valid.Execution.Launch, checkpoint));
        CloseAndAccept(control, valid, checkpoint);
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
        CloseAndAccept(control, valid, log);
    }

    [Theory]
    [InlineData("journal.capture.before", false)]
    [InlineData("journal.capture.after", false)]
    [InlineData("journal.close-turn.before", false)]
    [InlineData("journal.capture.before", true)]
    [InlineData("journal.capture.after", true)]
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
        if (point == "journal.capture.before") SettlementAssertions.Failed(settlement);
        else
        {
            SettlementAssertions.Matched(settlement);
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
    [InlineData("journal.close-turn.before", false)]
    [InlineData("journal.close-turn.before", true)]
    public async AsyncTask A_captured_settlement_whose_log_moved_on_closes_by_confirmed_recovery(string point, bool takeover)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (ready, log) = await Ready(f);
        var launch = ready.Execution.Launch;
        await Crash(f, ready, log, f.Op(), point);
        using (var attempt = AttemptLog.Open(f.Store.AttemptFolder(W, f.RunId, T, launch.Attempt)))
            attempt.Append(new AttemptEvent.HandedToTerminal(At, ready.Checkout, "codex resume fixture"));
        if (takeover) f.ReleaseControl();
        Assert.Equal("EvidenceMismatch", Rejection(await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), launch)));
        Assert.Null(RunReducer.Divergence(f.Read(), Assert.Single(f.Read().Captures).Value));
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
        Assert.Equal("OutcomeMismatch", Assert.IsType<Acceptance.Rejected>(f.Materializer().Accept(f.Lease(T), f.Op(), launch.Attempt)).Reason.Problem.ToString());
        Assert.Equal(0, f.Read().Results.Count(result => result.Task == T));
        using var control = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (valid, checkpoint) = await Ready(control);
        await Crash(control, valid, checkpoint, control.Op(), point);
        if (takeover) control.ReleaseControl();
        Assert.Equal("SettlementPending", ProblemName(control.Store.Recover(control.Lease(T), control.Op(), valid.Execution.Launch.Attempt)));
        SettlementAssertions.Matched(Assert.IsType<Settlement.Closed>(await control.Materializer().RecoverSettlement(
            control.Lease(T), control.Op(), valid.Execution.Launch)));
        CloseAndAccept(control, valid, checkpoint);
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("rewritten")]
    public async AsyncTask A_captured_settlement_stays_pending_unless_its_log_reads_as_moved_on(string state)
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
    public async AsyncTask A_log_that_moved_on_while_its_writer_still_holds_it_closes_by_confirmed_recovery()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (ready, log) = await Ready(f);
        var launch = ready.Execution.Launch;
        await Crash(f, ready, log, f.Op(), "journal.close-turn.before");
        using var attempt = AttemptLog.Open(f.Store.AttemptFolder(W, f.RunId, T, launch.Attempt));
        attempt.Append(new AttemptEvent.HandedToTerminal(At, ready.Checkout, "codex resume fixture"));

        var recovered = Assert.IsType<RunDecision.Recorded>(f.Store.Recover(f.Lease(T), f.Op(), launch.Attempt,
            RecoveryOutcome.Stopped, f.Op(), "The person stopped the turn after its log moved on."));

        Assert.Equal("Stopped", Assert.IsType<AttemptEnd.Recovered>(recovered.Record.Closures[launch.Attempt]).Outcome.ToString());
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
        RunRead Apply(RunRecord current, RunEvent e) => RunReducer.Apply(W, f.RunId, current, new(RunJournal.Schema, current.Sequence + 1, f.Op(), Prompt, At, e));
        Assert.Equal(new RunRejection(RunProblem.InvalidClaim, record.Sequence + 1),
            Assert.IsType<RunRead.Rejected>(Apply(record with { Captures = record.Captures.Clear() }, new RunEvent.TurnClosed(launch, log))).Reason);
        Assert.Equal(log, Assert.IsType<RunRead.Loaded>(Apply(record, new RunEvent.TurnClosed(launch, log))).Record.TurnClosures[launch]);
    }

    [Fact]
    public async AsyncTask A_late_close_turn_gets_the_closed_turns_answer()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (ready, log) = await Ready(f);
        var launch = ready.Execution.Launch;
        var closed = Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(T), f.Op(), launch, log));
        var sequence = f.Read().Sequence;
        SettlementAssertions.Matched(closed);
        var repeated = Assert.IsType<RunDecision.Existing>(f.Store.CloseTurn(f.Permit, f.Op(), launch, log));
        Assert.Equal(log, Assert.IsType<RunEvent.TurnClosed>(repeated.Event).Evidence);
        Assert.Equal("EvidenceMismatch", ProblemName(f.Store.CloseTurn(f.Permit, f.Op(), launch, log with { ByteLength = log.ByteLength + 1 })));
        Assert.Equal(sequence, f.Read().Sequence);
        CloseAndAccept(f, ready, log);
    }

    [Fact]
    public async AsyncTask Fenced_launches_accept_only_settlement_evidence()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (ready, log) = await Ready(f);
        await Crash(f, ready, log, f.Op(), "journal.close-turn.before");
        var template = Assert.Single(f.Read().Captures).Value;
        using var target = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (valid, checkpoint) = await Ready(target);
        var first = template with { Launch = valid.Execution.Launch, Log = checkpoint };
        Assert.IsType<RunDecision.Recorded>(target.Store.Record(target.Permit, target.Op(), new RunEvent.TurnCaptured(first)));
        target.ReleaseControl();
        var lease = target.Lease(T);
        Assert.Equal("InvalidClaim", ProblemName(target.Store.Record(target.Permit, target.Op(), new RunEvent.TurnCaptured(first))));
        Assert.Equal("InvalidClaim", ProblemName(target.Store.Record(target.Permit, target.Op(), target.Read().RootExits[first.Launch])));
        Assert.Equal("UnresolvedOwnership", ProblemName(target.Store.CloseTurn(target.Permit, target.Op(), first.Launch, checkpoint)));
        Assert.Empty(target.Read().TurnClosures);
        Assert.Equal(RunJournal.Canonical(first), RunJournal.Canonical(SettlementAssertions.Matched(Assert.IsType<Settlement.Closed>(
            await target.Materializer().RecoverSettlement(lease, target.Op(), first.Launch)))));
        Assert.Single(target.Read().TurnClosures);
        using var fresh = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (freshReady, freshLog) = await Ready(fresh);
        fresh.ReleaseControl();
        _ = fresh.Lease(T);
        Assert.Equal("InvalidClaim", ProblemName(fresh.Store.Record(fresh.Permit, fresh.Op(), new RunEvent.TurnCaptured(
            first with { Launch = freshReady.Execution.Launch, Log = freshLog }))));
        Assert.Equal("UnresolvedOwnership", ProblemName(fresh.Store.CloseTurn(fresh.Permit, fresh.Op(), freshReady.Execution.Launch, freshLog)));
        Assert.Empty(fresh.Read().Captures);
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
        await Crash(f, ready, log, operation, "journal.capture.after");
        await Crash(f, ready, log, operation, "journal.close-turn.before");
        var capture = Assert.Single(f.Read().Captures).Value.Capture;
        var root = new OperationId(capture.Value);
        var closed = Assert.IsType<Settlement.Closed>(await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), ready.Execution.Launch));
        Assert.Equal(capture, SettlementAssertions.Matched(closed).Capture);
        Assert.Equal(OperationIds.Derive(operation, "capture-1"), Assert.Single(f.Read().Receipts.Values, e =>
            e.Event is RunEvent.TurnCaptured).Operation);
        Assert.True(f.Read().Receipts.ContainsKey(OperationIds.Derive(root, "close-turn")));
        Assert.Single(f.Read().TurnClosures);
        var sequence = f.Read().Sequence;
        SettlementAssertions.Equal(closed, await f.Materializer().Settle(f.Lease(T), operation, ready.Execution.Launch, log));
        Assert.Equal(sequence, f.Read().Sequence);
        CloseAndAccept(f, ready, log);
    }

    [Fact]
    public async AsyncTask Recovery_uses_the_first_observations_log_instead_of_a_later_checkpoint()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (ready, log) = await Ready(f);
        await Crash(f, ready, log, f.Op(), "journal.capture.after");
        var first = Assert.Single(f.Read().Captures).Value;
        var canonical = RunJournal.Canonical(first);
        using (var attempt = AttemptLog.Open(f.Store.AttemptFolder(W, f.RunId, T, ready.Execution.Launch.Attempt)))
            attempt.Append(new AttemptEvent.HandedToTerminal(At, ready.Checkout, "codex resume fixture"));
        f.ReleaseControl();
        Assert.Equal("EvidenceMismatch", Rejection(await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), ready.Execution.Launch)));
        Assert.Single(f.Read().Captures);
        Assert.Equal(canonical, RunJournal.Canonical(f.Read().Captures[first.Launch]));
        Assert.Empty(f.Read().TurnClosures);
        using var control = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (valid, checkpoint) = await Ready(control);
        await Crash(control, valid, checkpoint, control.Op(), "journal.capture.after");
        control.ReleaseControl();
        var closed = Assert.IsType<Settlement.Closed>(await control.Materializer().RecoverSettlement(control.Lease(T), control.Op(), valid.Execution.Launch));
        SettlementAssertions.Matched(closed);
        Assert.Single(control.Read().Captures);
        Assert.Single(control.Read().TurnClosures);
    }

    [Fact]
    public async AsyncTask A_failed_settlement_requires_recovered_even_with_a_terminal_log()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var (ready, log) = await Ready(f);
        var launch = ready.Execution.Launch;
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, f.Op(), new RunEvent.Blocked(new(RunReducer.CaptureBlock(launch), T,
            launch.Attempt, MaterializationProblem.InputUnavailable, ready.Execution.Inputs, [], "Missing turn-end log evidence.")
        { Scope = new BlockScope.Operation() })));
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
        CloseAndAccept(published, valid, checkpoint);
    }

    public static IEnumerable<object[]> CrashPoints()
    {
        foreach (var point in new[] { "journal.root-exit.before", "journal.root-exit.after", "git.capture.before", "git.capture.after",
            "journal.capture.before", "journal.capture.after", "git.pin.before", "git.pin.after", "refs.snapshot.after",
            "outbox.before", "outbox.after", "artifact.payload.before", "artifact.payload.after",
            "journal.close-turn.before", "journal.close-turn.after" })
            foreach (var takeover in new[] { false, true }) yield return [point, takeover, false, false];
        // Recovery of a recorded capture closes its turn without observing the worktree again.
        foreach (var point in new[] { "journal.close-turn.before", "journal.close-turn.after" })
            foreach (var takeover in new[] { false, true }) yield return [point, takeover, true, false];
        // Recovery of a turn without a capture records why and closes it.
        foreach (var point in new[] { "journal.capture-blocked.before", "journal.capture-blocked.after", "journal.close-turn.before", "journal.close-turn.after" })
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
                CloseAndAccept(control, valid, checkpoint);
                return;
            }
            log = f.ObserveAndLog(ready);
        }
        else
        {
            f.Git.Write("result.txt", "done\n", ready.Checkout);
            log = f.ObserveAndLog(ready);
            if (recovering && !missingCapture) await Crash(f, ready, log, operation, "journal.capture.after");
            var crashing = f.Materializer(probe: p => { if (p == point) throw new SettlementCrash(); });
            await Assert.ThrowsAsync<SettlementCrash>(() => (recovering
                ? crashing.RecoverSettlement(f.Lease(T), operation, launch)
                : crashing.Settle(f.Lease(T), operation, launch, log)).AsTask());
        }
        var first = f.Read().Captures.Values.FirstOrDefault();
        var firstBytes = first is null ? null : RunJournal.Canonical(first);
        if (takeover) f.ReleaseControl();
        var missing = missingCapture || takeover && first is null;
        var closed = Assert.IsType<Settlement.Closed>(await (takeover || recovering
            ? f.Materializer().RecoverSettlement(f.Lease(T), operation, launch)
            : f.Materializer().Settle(f.Lease(T), operation, launch, log)));
        if (missing)
        {
            Assert.Equal("Missing turn-end capture evidence.", SettlementAssertions.Failed(closed).Detail);
            Assert.Empty(f.Read().Captures);
        }
        else
        {
            var observation = SettlementAssertions.Matched(closed);
            Assert.Equal(new byte[] { 67, 0, 127 }, RunStorage.Read(new RunStorage(f.Git.Folder, W, f.RunId).Folder,
                observation.Artifacts[0].StoredPath, observation.Artifacts[0].Content, 3));
            if (firstBytes is not null) Assert.Equal(firstBytes, RunJournal.Canonical(f.Read().Captures[launch]));
        }
        var retainedPins = IDevelop.Core.Tests.Git.GitFixture.Read(f.Git.Open().RefSnapshot(RunLayout.PinPrefix(f.Read().RunKey!)));
        Assert.Contains(RunLayout.RootPin(f.Read().RunKey!, f.Read().TaskKeys[T], launch), retainedPins.Keys);
        if (!missing) Assert.Contains(RunLayout.CapturePin(f.Read().RunKey!, f.Read().TaskKeys[T], launch), retainedPins.Keys);
        var sequence = f.Read().Sequence;
        SettlementAssertions.Equal(closed, await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), launch));
        Assert.Equal(sequence, f.Read().Sequence);
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), launch.Attempt, TerminalAttemptOutcome.Succeeded, log));
        SettlementAssertions.Equal(closed, await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), launch));
        if (missing)
        {
            Assert.Equal("Missing turn-end capture evidence.", Assert.IsType<Acceptance.Blocked>(f.Materializer().Accept(
                f.Lease(T), f.Op(), launch.Attempt)).Block.Detail);
            Assert.Equal((0, 1), (f.Read().Results.Count, f.Read().Claims.Count));
            using var control = new PreparationFixture(FixtureWorkflow(Writer(T)));
            var (valid, checkpoint) = await Ready(control);
            Assert.IsType<Settlement.Closed>(await control.Materializer().Settle(control.Lease(T), control.Op(), valid.Execution.Launch, checkpoint));
            CloseAndAccept(control, valid, checkpoint);
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
            CloseAndAccept(control, valid, checkpoint);
        }
        else
        {
            var log = f.ObserveAndLog(ready);
            Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(T), f.Op(), ready.Execution.Launch, log));
            CloseAndAccept(f, ready, log);
        }
        Assert.Single(f.Read().Claims);
        Assert.Equal("done\n", File.ReadAllText(Path.Combine(ready.Checkout, "result.txt")));
        var sequence = f.Read().Sequence;
        if (takeover) Assert.Equal("UnresolvedOwnership", Rejection(await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), ready.Execution.Launch)));
        else Assert.IsType<Settlement.Closed>(await f.Materializer().RecoverSettlement(f.Lease(T), f.Op(), ready.Execution.Launch));
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

    private static void CloseAndAccept(PreparationFixture f, Preparation.Ready ready, LogCheckpoint log)
    {
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), ready.Execution.Launch.Attempt,
            TerminalAttemptOutcome.Succeeded, log));
        AssertPublished(f, ready);
        Assert.Equal((1, 1), (f.Read().Results.Count, f.Read().Claims.Count));
    }

    private static void AssertPublished(PreparationFixture f, Preparation.Ready ready)
    {
        var result = Assert.IsType<Acceptance.Accepted>(f.Materializer().Accept(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt)).Result;
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

    /// <summary>The turn closed with its one capture, which matched its turn's root observation.</summary>
    internal static CaptureObservation Matched(Settlement.Closed closed)
    {
        Assert.Null(closed.Failure);
        Assert.Null(closed.Divergence);
        return Assert.IsType<CaptureObservation>(closed.Capture);
    }

    /// <summary>The turn closed with its one capture, which differs from its turn's root observation.</summary>
    internal static CaptureDivergence Diverged(Settlement.Closed closed)
    {
        Assert.Null(closed.Failure);
        Assert.NotNull(closed.Capture);
        return Assert.IsType<CaptureDivergence>(closed.Divergence);
    }

    /// <summary>The turn closed without a capture, with the block that says why none could be taken.</summary>
    internal static MaterializationBlock Failed(Settlement.Closed closed)
    {
        Assert.Null(closed.Capture);
        Assert.Null(closed.Divergence);
        return Assert.IsType<MaterializationBlock>(closed.Failure);
    }
}
