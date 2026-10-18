using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;
using static IDevelop.Core.Tests.Turns.TurnFixture;

namespace IDevelop.Core.Tests.Turns;

/// <summary>Continue and Retry after an interrupted attempt, launched through the runner (design rows 395 to 397).</summary>
public sealed class RecoveryLaunchTests
{
    /// <summary>
    /// A turn whose client wrote keep.txt is interrupted by closing the project and closed as interrupted, and the window
    /// opens again.
    /// </summary>
    internal static async Task<SettledTurn> Interrupted(TurnFixture f)
    {
        await f.Open(FakeAgents.Fresh(f.Client)
            .Print(FakeAgents.SessionLine(f.Client, "session-1"))
            .Write("keep.txt", "keep\n")
            .WaitForFile(Path.Combine(f.Evidence, "gate")));
        var running = await f.Start();
        await WaitUntilAsync(() => f.Log(running.Address.Launch).Record?.SessionId == "session-1" && File.Exists(Path.Combine(f.Checkout, "keep.txt")));
        await f.Reopen();
        var turn = await f.Settled(running);
        Assert.Equal("Interrupted", turn.Attempt.Status.ToString());
        Assert.IsType<RunDecision.Recorded>(f.Preparation.Store.CloseAttempt(f.Preparation.Permit, f.Preparation.Op(),
            turn.Address.Launch.Attempt, TerminalAttemptOutcome.Interrupted, turn.Log));
        Assert.IsType<TurnDisposition.Ended>(Assert.IsType<Release.Released>(turn.Release()).Receipt);
        return turn;
    }

    [Fact]
    public async Task An_interrupted_attempt_continues_once_in_its_checkout_with_its_session()
    {
        await using var f = new TurnFixture();
        var previous = (await Interrupted(f)).Address.Launch.Attempt;
        var confirmation = f.Preparation.Op();
        var launches = f.Launches;
        await f.Reopen();
        var reconciled = Assert.IsType<Reconciliation.Found>(await f.Runs.Reconcile(f.Preparation.Permit, f.Preparation.Op(), new(previous, 1))
            .WaitAsync(Bound));
        var settled = Assert.IsType<TurnSettlement.Settled>(reconciled.Settlement).Turn;
        Assert.IsType<TurnDisposition.Ended>(Assert.IsType<Release.Released>(settled.Release()).Receipt);
        Assert.Equal(launches, f.Launches);
        FakeAgents.Install(f.Fakes, f.Client, FakeAgents.Resuming(f.Client, "session-1")
            .RecordWorkingDirectory(Path.Combine(f.Evidence, "continue-cwd.txt"))
            .Copy("keep.txt", Path.Combine(f.Evidence, "continue-keep.txt"))
            .Print(FakeAgents.SessionLine(f.Client, "session-1"))
            .Print(FakeAgents.ReplyLines(f.Client, "Done.")));
        var intent = new TurnIntent.First(f.Preparation.Op(), T, new AttemptCause.Continue(previous, confirmation));
        var first = f.Runs.StartTurn(f.Preparation.Permit, intent);
        var second = f.Runs.StartTurn(f.Preparation.Permit, intent);
        var running = Assert.IsType<TurnStart.Started>(await first.WaitAsync(Bound)).Turn;
        Assert.Same(running, Assert.IsType<TurnStart.Existing>(await second.WaitAsync(Bound)).Running);
        var turn = await f.Settled(running);
        Assert.Equal("Succeeded", turn.Attempt.Status.ToString());
        Assert.Equal(1, f.Preparation.Read().Attempts.Values.Count(attempt => attempt.Cause is AttemptCause.Continue));
        Assert.Equal(launches + 1, f.Launches);
        Assert.Equal("session-1", turn.Attempt.SessionId);
        var requested = Assert.IsType<AttemptEvent.Requested>(f.Log(turn.Address.Launch).Events[0]);
        Assert.Equal((new Continuation(previous, "session-1") { Folder = f.Checkout }, f.Checkout), (requested.Continues, requested.WorkingFolder));
        Assert.Equal(f.Checkout, File.ReadAllText(Path.Combine(f.Evidence, "continue-cwd.txt")));
        Assert.Equal("keep\n", File.ReadAllText(Path.Combine(f.Evidence, "continue-keep.txt")));
        Assert.IsType<TurnStart.Existing>(await f.Runs.StartTurn(f.Preparation.Permit, intent).WaitAsync(Bound));
        Assert.Equal(launches + 1, f.Launches);
    }

    [Fact]
    public async Task Continue_without_a_session_never_launches()
    {
        await using var f = new TurnFixture();
        await f.Open(FakeAgents.Fresh(f.Client).Exit(1));
        var failed = await f.Settled(await f.Start());
        Assert.Equal("Failed", failed.Attempt.Status.ToString());
        Assert.Null(failed.Attempt.SessionId);
        Assert.IsType<RunDecision.Recorded>(f.Preparation.Store.CloseAttempt(f.Preparation.Permit, f.Preparation.Op(),
            failed.Address.Launch.Attempt, TerminalAttemptOutcome.Failed, failed.Log));
        Assert.IsType<Release.Released>(failed.Release());
        var launches = f.Launches;
        var refused = Assert.IsType<TurnStart.Refused>(await f.Runs.StartTurn(f.Preparation.Permit, new TurnIntent.First(f.Preparation.Op(), T,
            new AttemptCause.Continue(failed.Address.Launch.Attempt, f.Preparation.Op()))).WaitAsync(Bound));
        Assert.Equal("SessionUnavailable", refused.Reason.Problem.ToString());
        Assert.Equal(launches, f.Launches);
        Assert.Single(f.Preparation.Read().Claims);
    }

    [Fact]
    public async Task Retry_starts_a_fresh_session()
    {
        await using var f = new TurnFixture();
        var previous = (await Interrupted(f)).Address.Launch.Attempt;
        var launches = f.Launches;
        await f.Reopen();
        var reconciled = Assert.IsType<Reconciliation.Found>(await f.Runs.Reconcile(f.Preparation.Permit, f.Preparation.Op(), new(previous, 1))
            .WaitAsync(Bound));
        Assert.IsType<Release.Released>(Assert.IsType<TurnSettlement.Settled>(reconciled.Settlement).Turn.Release());
        Assert.Equal(launches, f.Launches);
        var p = f.Preparation;
        var salvage = Assert.IsType<Salvage.Retained>(await p.Materializer().Salvage(p.Lease(T), p.Op(), previous));
        Assert.IsType<RetryReset.Reset>(p.Materializer().ResetForRetry(p.Lease(T), p.Op(), salvage.Receipt.Plan, p.Op()));
        p.Release(T);
        Assert.False(File.Exists(Path.Combine(f.Checkout, "keep.txt")));
        // Only a fresh thread matches, so a resumed session would fail the turn.
        FakeAgents.Install(f.Fakes, f.Client, FakeAgents.Fresh(f.Client)
            .Print(FakeAgents.SessionLine(f.Client, "session-2")).Print(FakeAgents.ReplyLines(f.Client, "Done.")));
        var intent = new TurnIntent.First(p.Op(), T, new AttemptCause.Retry(previous, p.Op()));
        var first = f.Runs.StartTurn(p.Permit, intent);
        var second = f.Runs.StartTurn(p.Permit, intent);
        var running = Assert.IsType<TurnStart.Started>(await first.WaitAsync(Bound)).Turn;
        Assert.Same(running, Assert.IsType<TurnStart.Existing>(await second.WaitAsync(Bound)).Running);
        var turn = await f.Settled(running);
        Assert.Equal("Succeeded", turn.Attempt.Status.ToString());
        Assert.Equal("session-2", turn.Attempt.SessionId);
        Assert.Null(Assert.IsType<AttemptEvent.Requested>(f.Log(turn.Address.Launch).Events[0]).Continues);
        Assert.Equal(1, p.Read().Attempts.Values.Count(attempt => attempt.Cause is AttemptCause.Retry));
        Assert.Equal(launches + 1, f.Launches);
    }
}
