using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;
using static IDevelop.Core.Tests.Turns.TurnFixture;

namespace IDevelop.Core.Tests.Turns;

public sealed class TurnCrashTests
{
    [Theory]
    [InlineData("runner.claim.after", 0, "Uncertain")]
    [InlineData("runner.launch.after", 1, "Uncertain")]
    [InlineData("runner.running", 1, "Uncertain")]
    [InlineData("journal.root-exit.before", 1, "Uncertain")]
    [InlineData("runner.cleanup.inside", 1, "Settling")]
    [InlineData("journal.capture-1.before", 1, "Reserved")]
    [InlineData("journal.capture-1.after", 1, "Reserved")]
    [InlineData("journal.capture-disposition.before", 1, "Reserved")]
    [InlineData("journal.close-turn.after", 1, "Reserved")]
    public async Task Reconcile_never_launches(string point, int launches, string recovery)
    {
        var waiting = point is "runner.launch.after" or "runner.running";
        await using var f = waiting ? new TurnFixture(client: ClientId.Pi, model: "deepseek/deepseek-flash") : new TurnFixture();
        using var processes = new Processes();
        await f.Open(waiting ? FakeAgents.Fresh(ClientId.Pi).WaitForFile(Path.Combine(f.Evidence, "gate")) : f.Success());
        var intent = f.First();
        var launch = await f.Crash(processes, intent.Operation, point, launches);
        if (point == "runner.running" && OperatingSystem.IsWindows())
        {
            var launched = Assert.Single(f.Log(launch).Events.OfType<AttemptEvent.Launched>());
            await WaitUntilAsync(() => ProcessCheck.Check(new(launched.ProcessId, launched.ProcessStarted)) == ProcessMatch.Gone);
        }
        await using var fresh = f.OpenRuns(await f.Fakes.DiscoverAsync());
        fresh.MaterializerClock = TimeProvider.System;
        var first = await fresh.Reconcile(f.Preparation.Permit, f.Preparation.Op(), launch).WaitAsync(Bound);
        AssertRecovery(f, launch, recovery);
        if (recovery == "Uncertain")
        {
            var turn = Assert.IsType<TurnSettlement.Unresolved>(first).Turn;
            Assert.Equal("Uncertain", turn.Reason.ToString());
            Assert.Null(turn.Rejection);
            Assert.Empty(f.Preparation.Read().RootExits);
            if (point is "runner.claim.after" or "runner.launch.after") Assert.Null(turn.Root);
            else Assert.Equal(point == "runner.running" && !OperatingSystem.IsWindows() ? "Same" : "Gone", turn.Root.ToString());
            Assert.Equal("NotSettled", Assert.IsType<Release.Held>(turn.Release()).Reason.Problem.ToString());
        }
        else if (recovery == "Settling")
        {
            var turn = Assert.IsType<TurnSettlement.Unresolved>(first).Turn;
            Assert.Equal("IncompleteEvidence", turn.Reason.ToString());
            Assert.Equal("RecoveryEvidenceInsufficient", turn.Rejection!.Problem.ToString());
            Assert.Null(turn.Root);
            var failure = Assert.IsType<CaptureDisposition.Failed>(Assert.Single(f.Preparation.Read().Dispositions).Value.Disposition);
            Assert.Equal("InputUnavailable", failure.Problem.ToString());
            Assert.Equal("Missing turn-end log evidence.", failure.Detail);
            Assert.Empty(failure.Evidence);
            Assert.Single(f.Preparation.Read().RootExits);
            Assert.Empty(f.Preparation.Read().TurnClosures);
        }
        else
        {
            var turn = Assert.IsType<TurnSettlement.Settled>(first).Turn;
            Assert.Equal("Succeeded", turn.Attempt.Status.ToString());
            Assert.Equal(0, Assert.IsType<RootExit.Exited>(turn.Exit.Exit).Code);
            Assert.Single(f.Preparation.Read().RootExits);
            Assert.Single(f.Preparation.Read().TurnClosures);
            if (point == "journal.capture-1.before")
            {
                var failure = Assert.IsType<CaptureDisposition.Failed>(turn.Capture.Disposition);
                Assert.Equal("InputUnavailable", failure.Problem.ToString());
                Assert.Equal("Missing turn-end capture evidence.", failure.Detail);
                Assert.Empty(failure.Evidence);
                Assert.Empty(f.Preparation.Read().Captures[turn.Capture.Capture]);
            }
            else
            {
                Assert.IsType<CaptureDisposition.Matched>(turn.Capture.Disposition);
                Assert.Equal(2, f.Preparation.Read().Captures[turn.Capture.Capture].Count);
                Assert.Equal(point == "journal.capture-1.after" ? new[] { false, true } : new[] { false, false },
                    f.Preparation.Read().Captures[turn.Capture.Capture].Select(capture => capture.Recovery));
            }
        }
        Assert.Equal(launches, f.Launches);
        var second = await fresh.Reconcile(f.Preparation.Permit, f.Preparation.Op(), launch).WaitAsync(Bound);
        if (recovery is "Uncertain" or "Settling")
            Assert.Equal(recovery == "Uncertain" ? "Uncertain" : "IncompleteEvidence", Assert.IsType<TurnSettlement.Unresolved>(second).Turn.Reason.ToString());
        else
        {
            var disposition = Assert.IsType<TurnSettlement.Settled>(second).Turn.Capture.Disposition;
            if (point == "journal.capture-1.before") Assert.Equal("Missing turn-end capture evidence.", Assert.IsType<CaptureDisposition.Failed>(disposition).Detail);
            else Assert.IsType<CaptureDisposition.Matched>(disposition);
        }
        var duplicate = Assert.IsType<TurnStart.Existing>(await fresh.StartTurn(f.Preparation.Permit, intent).WaitAsync(Bound));
        Assert.Equal(launch, duplicate.Launch);
        AssertRecovery(f, launch, recovery);
        Assert.Equal(launches, f.Launches);
        Assert.Single(f.Preparation.Read().Claims);
        Assert.Empty(f.Preparation.Read().Results);
        if (point is "journal.capture-1.after" or "journal.capture-disposition.before" or "journal.close-turn.after")
        {
            var result = f.Publish(Assert.IsType<TurnSettlement.Settled>(second).Turn);
            Assert.Single(f.Preparation.Read().Results);
            Assert.Equal("done\n", f.Preparation.Git.Git("show", Assert.IsType<CodeOutput.Produced>(result.Code).Code.Commit.Hex + ":result.txt"));
            AssertRecovery(f, launch, "Closed");
        }
        else if (point == "journal.capture-1.before")
        {
            var turn = Assert.IsType<TurnSettlement.Settled>(second).Turn;
            Assert.IsType<RunDecision.Recorded>(f.Preparation.Store.CloseAttempt(f.Preparation.Permit, f.Preparation.Op(),
                launch.Attempt, TerminalAttemptOutcome.Succeeded, turn.Log));
            Assert.Equal("Missing turn-end capture evidence.", Assert.IsType<Publication.Blocked>(f.Preparation.Materializer()
                .Publish(turn.Lease, f.Preparation.Op(), launch.Attempt)).Block.Detail);
            Assert.Empty(f.Preparation.Read().Results);
        }
        File.WriteAllText(Path.Combine(f.Evidence, "gate"), "release");
        processes.Dispose();
        Assert.Equal(launches, f.Launches);
        if (point is "runner.claim.after" or "runner.launch.after" or "runner.running" or "journal.root-exit.before" or "runner.cleanup.inside" or "journal.capture-1.before")
            await SuccessfulControl();
    }

    [Theory]
    [InlineData("runner.prepared")]
    [InlineData("runner.request.after")]
    public async Task A_crash_before_the_claim_reruns_once(string point)
    {
        await using var f = new TurnFixture();
        using var processes = new Processes();
        await f.Open();
        var intent = f.First();
        var launch = await f.Crash(processes, intent.Operation, point, 0);
        Assert.Equal(0, f.Launches);
        Assert.Empty(f.Preparation.Read().Claims);
        AssertRecovery(f, launch, point == "runner.prepared" ? "RequestMissing" : "Reserved");
        await using var fresh = f.OpenRuns(await f.Fakes.DiscoverAsync());
        var running = Assert.IsType<TurnStart.Started>(await fresh.StartTurn(f.Preparation.Permit, intent).WaitAsync(Bound)).Turn;
        var turn = await f.Settled(running);
        Assert.IsType<CaptureDisposition.Matched>(turn.Capture.Disposition);
        Assert.Equal(launch, turn.Address.Launch);
        Assert.Equal(1, f.Launches);
        Assert.Single(f.Preparation.Read().Claims);
        Assert.Single(f.Log(launch).Events.OfType<AttemptEvent.Requested>());
        Assert.Equal("done\n", File.ReadAllText(Path.Combine(f.Checkout, "result.txt")));
    }

    [Fact]
    public async Task A_crash_after_a_later_claim_is_recovered_without_a_launch()
    {
        await using var f = new TurnFixture(ConversationMode.Chat);
        using var processes = new Processes();
        await f.Open();
        var first = await f.Settled(await f.Start());
        Assert.Equal("WaitingForInput", first.Attempt.Status.ToString());
        Assert.IsType<TurnDisposition.Resting>(Assert.IsType<Release.Released>(first.Release()).Receipt);
        var next = new LaunchKey(first.Address.Launch.Attempt, 2);
        var operation = f.Preparation.Op();
        await f.Crash(processes, operation, "runner.claim.after", 1, next);
        await using var fresh = f.OpenRuns(await f.Fakes.DiscoverAsync());
        var turn = Assert.IsType<TurnSettlement.Unresolved>(await fresh.Reconcile(f.Preparation.Permit, f.Preparation.Op(), next).WaitAsync(Bound)).Turn;
        Assert.Equal("Uncertain", turn.Reason.ToString());
        Assert.Null(turn.Root);
        Assert.Equal(1, f.Launches);
        Assert.IsType<AttemptEvent.Exited>(f.Log(next).Events[^1]);
        Assert.Equal("WaitingForInput", f.Log(next).Record!.Status.ToString());
        Assert.Empty(f.Log(next).Events.OfType<AttemptEvent.TurnRequested>());
        var confirmation = f.Preparation.Op();
        var recovered = Assert.IsType<RunDecision.Recorded>(f.Preparation.Store.Recover(turn.Lease, f.Preparation.Op(),
            next.Attempt, RecoveryOutcome.NotStarted, confirmation, "Not started."));
        var closed = Assert.IsType<RunEvent.AttemptClosed>(recovered.Event);
        var end = Assert.IsType<AttemptEnd.Recovered>(closed.End);
        Assert.Equal("NotStarted", end.Outcome.ToString());
        Assert.Equal(confirmation, end.Confirmation);
        Assert.Equal("Not started.", end.Reason);
        var released = Assert.IsType<AttemptEnd.Recovered>(Assert.IsType<TurnDisposition.Ended>(Assert.IsType<Release.Released>(turn.Release()).Receipt).End);
        Assert.Equal("NotStarted", released.Outcome.ToString());
        AssertRecovery(f, next, "Closed");
        Assert.Empty(f.Preparation.Read().Results);
        Assert.Equal(1, f.Launches);
        await RestingNextControl();
    }

    [UnixFact]
    public Task Reconcile_never_signals_a_surviving_root() => SurvivingRoot("Same");

    [WindowsFact]
    public Task Reconcile_keeps_a_job_terminated_root_uncertain() => SurvivingRoot("Gone");

    private static async Task SurvivingRoot(string root)
    {
        await using var f = new TurnFixture(client: ClientId.Pi, model: "deepseek/deepseek-flash");
        using var processes = new Processes();
        var alive = Path.Combine(f.Evidence, "alive.txt");
        await f.Open(FakeAgents.Fresh(ClientId.Pi).WaitForFile(Path.Combine(f.Evidence, "gate")).Write(alive, "alive\n"));
        var launch = await f.Crash(processes, f.Preparation.Op(), "runner.running");
        var launched = Assert.Single(f.Log(launch).Events.OfType<AttemptEvent.Launched>());
        var identity = new ProcessIdentity(launched.ProcessId, launched.ProcessStarted);
        await WaitUntilAsync(() => ProcessCheck.Check(identity).ToString() == root);
        Assert.Equal(root, ProcessCheck.Check(identity).ToString());
        await using var fresh = f.OpenRuns(await f.Fakes.DiscoverAsync());
        var turn = Assert.IsType<TurnSettlement.Unresolved>(await fresh.Reconcile(f.Preparation.Permit, f.Preparation.Op(), launch).WaitAsync(Bound)).Turn;
        Assert.Equal("Uncertain", turn.Reason.ToString());
        Assert.Equal(root, turn.Root.ToString());
        Assert.Equal(root, ProcessCheck.Check(identity).ToString());
        Assert.Equal(1, f.Launches);
        File.WriteAllText(Path.Combine(f.Evidence, "gate"), "release");
        if (root == "Same") await WaitUntilAsync(() => File.Exists(alive) && File.ReadAllText(alive) == "alive\n");
        else Assert.False(File.Exists(alive));
        processes.Dispose();
        await WaitUntilAsync(() => ProcessCheck.Check(identity) == ProcessMatch.Gone);
        Assert.Equal("Gone", ProcessCheck.Check(identity).ToString());
    }

    private static void AssertRecovery(TurnFixture f, LaunchKey launch, string state)
    {
        var attempts = Assert.IsType<RecoveryRead.Loaded>(f.Preparation.Store.InspectRecovery(W, f.Preparation.RunId)).Attempts;
        Assert.Equal(new[] { state }, attempts.Select(attempt => attempt.State.ToString()));
        Assert.Equal(launch.Attempt, Assert.Single(attempts).Attempt);
    }
}
