using IDevelop.Core.Tests.Turns;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Coordination.CoordinatorFixture;

namespace IDevelop.Core.Tests.Coordination;

/// <summary>Reopening a run: nothing launches before Resume, and Resume reconciles before it schedules (E3b).</summary>
public sealed class RecoveryTests
{
    [Theory]
    [InlineData("runner.claim.after", 0, "Uncertain")]
    [InlineData("journal.root-exit.before", 1, "Uncertain")]
    [InlineData("journal.capture-1.before", 1, "Missing")]
    [InlineData("journal.capture-1.after", 1, "Accepted")]
    [InlineData("journal.close-turn.after", 1, "Accepted")]
    [InlineData("runner.prepared", 0, "Reserved")]
    [InlineData("runner.request.after", 0, "Reserved")]
    public async Task A_reopened_run_reconciles_before_it_schedules(string point, int launches, string outcome)
    {
        await using var f = new CoordinatorFixture(Chain());
        f.Answer(A, Writes(A, "result.txt", "done\n")).Answer(B, Writes(B, "b.txt", "B\n")).Answer(X, Reports(X));
        await f.Crash(A, point);
        Assert.Equal(launches, f.Launches(A));
        await f.Open();
        var dispatched = 0;
        f.Runs.Probe = point => { if (point.StartsWith("coordinator.", StringComparison.Ordinal) || point.StartsWith("runner.", StringComparison.Ordinal)) Interlocked.Increment(ref dispatched); };
        var paused = await f.Decided();
        Assert.Equal(RunStatus.Paused, paused.Status);
        Assert.Equal(outcome == "Uncertain" ? TaskState.Uncertain : outcome == "Reserved" ? TaskState.Ready : TaskState.Settling, paused.Tasks[A].State);
        Assert.Equal(TaskState.Ready, paused.Tasks[X].State);
        Assert.Equal(0, dispatched);
        Assert.Equal(launches, f.TotalLaunches);
        Assert.Empty(f.Read().Results);
        f.Runs.Probe = null;

        await f.Resume();
        switch (outcome)
        {
            case "Uncertain":
            {
                var stuck = await f.Until(view => view.Status == RunStatus.NeedsAttention && view.Tasks[X].State == TaskState.Done);
                Assert.Equal((TaskState.Uncertain, UnresolvedReason.Uncertain), (stuck.Tasks[A].State, stuck.Tasks[A].Unresolved));
                Assert.Equal(TaskState.Pending, stuck.Tasks[B].State);
                Assert.Equal([launches, 0, 1], new[] { f.Launches(A), f.Launches(B), f.Launches(X) });
                Assert.DoesNotContain(f.Read().Results, result => result.Task == A);
                Assert.Single(f.Read().Claims, claim => f.Read().Attempts[claim.Key.Attempt].Task == A);
                break;
            }
            case "Missing":
            {
                var stuck = await f.Until(view => view.Status == RunStatus.NeedsAttention && view.Tasks[X].State == TaskState.Done);
                Assert.Equal(TaskState.Blocked, stuck.Tasks[A].State);
                Assert.Equal("Missing turn-end capture evidence.", stuck.Tasks[A].Block!.Detail);
                Assert.DoesNotContain(f.Read().Results, result => result.Task == A);
                Assert.Equal("done\n", File.ReadAllText(Path.Combine(f.Checkout(A), "result.txt")));
                Assert.Equal([1, 0], new[] { f.Launches(A), f.Launches(B) });
                break;
            }
            default:
                await f.UntilStatus(RunStatus.Completed);
                Assert.Equal("done\n", f.ResultFile(A, "result.txt"));
                Assert.Equal([1, 1, 1], new[] { f.Launches(A), f.Launches(B), f.Launches(X) });
                Assert.Single(f.Read().Claims, claim => f.Read().Attempts[claim.Key.Attempt].Task == A);
                break;
        }
    }

    [Fact]
    public async Task A_successful_closure_without_its_publication_publishes_on_resume()
    {
        await using var f = new CoordinatorFixture(Chain());
        f.Answer(A, Writes(A, "result.txt", "done\n")).Answer(B, Writes(B, "b.txt", "B\n")).Answer(X, Reports(X));
        f.Install();
        var operation = RunOperations.Initial(f.Preparation.RunId, A);
        await using (var runs = f.OpenRuns(await f.Fakes.DiscoverAsync()))
        {
            using var permit = Assert.IsType<ControlTake.Owned>(f.Preparation.Store.TakeControl(Runs.RunFixtures.W, f.Preparation.RunId)).Permit;
            var turn = Assert.IsType<TurnStart.Started>(await runs.StartTurn(permit, new TurnIntent.First(operation, A, new AttemptCause.Initial())).WaitAsync(Bound)).Turn;
            var settled = Assert.IsType<TurnSettlement.Settled>(await turn.Settlement.WaitAsync(Bound)).Turn;
            Assert.IsType<RunDecision.Recorded>(f.Preparation.Store.CloseAttempt(permit, RunOperations.CloseAttempt(operation),
                settled.Address.Launch.Attempt, TerminalAttemptOutcome.Succeeded, settled.Log));
            // The window ends before its publication, as a crash does, and gives up the task's lock with its process.
            settled.Lease.Dispose();
        }
        await f.Open();
        Assert.Equal(TaskState.Settling, (await f.UntilStatus(RunStatus.Paused)).Tasks[A].State);
        Assert.Empty(f.Read().Results);
        await f.Resume();
        await f.UntilStatus(RunStatus.Completed);
        Assert.Equal("done\n", f.ResultFile(A, "result.txt"));
        Assert.Equal([1, 1, 1], new[] { f.Launches(A), f.Launches(B), f.Launches(X) });
    }

    [Fact]
    public async Task Closing_the_project_interrupts_running_work_and_reopening_relaunches_nothing()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Agent(B), Agent(X, readOnly: true)], (A, B)));
        f.Answer(A, FakeRule.On().Print(FakeAgents.SessionLine(ClientId.Codex, "session-A")).Write("result.txt", "done\n")
            .WaitForFile(Path.Combine(f.Evidence, "never")).Print(FakeAgents.ReplyLines(ClientId.Codex, "A ready.\n"))).Answer(X, Reports(X));
        await f.Open();
        await f.Resume();
        await f.Until(view => view.Tasks[A].State == TaskState.Running);
        await TurnFixture.WaitUntilAsync(() => f.Launches(A) == 1);
        await f.Reopen();
        var paused = await f.UntilStatus(RunStatus.Paused);
        Assert.Equal(TaskState.Failed, paused.Tasks[A].State);
        Assert.Equal(TerminalAttemptOutcome.Interrupted, Assert.IsType<AttemptEnd.Logged>(paused.Tasks[A].End).Outcome);
        await f.Resume();
        var stuck = await f.Until(view => view.Status == RunStatus.NeedsAttention && view.Tasks[X].State == TaskState.Done);
        Assert.Equal(TaskState.Failed, stuck.Tasks[A].State);
        Assert.Equal([1, 0, 1], new[] { f.Launches(A), f.Launches(B), f.Launches(X) });
        Assert.Single(f.Read().Attempts.Values, attempt => attempt.Task == A);
    }
}
