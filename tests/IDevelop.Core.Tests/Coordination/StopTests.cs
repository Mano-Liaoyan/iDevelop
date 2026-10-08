using IDevelop.Core.Tests.Turns;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Coordination.CoordinatorFixture;

namespace IDevelop.Core.Tests.Coordination;

/// <summary>Stop Workflow: the stop is recorded first, then nothing claims, running turns cancel, and the run settles stopped (E3b).</summary>
public sealed class StopTests
{
    private static FakeRule Waits(TaskId task, string gate) => FakeRule.On()
        .Print(FakeAgents.SessionLine(ClientId.Codex, "session-" + Name(task)))
        .Write("partial.txt", "partial\n")
        .WaitForFile(gate)
        .Print(FakeAgents.ReplyLines(ClientId.Codex, $"{Name(task)} ready.\n"));

    [Fact]
    public async Task Stop_before_the_claim_starts_no_client()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Agent(B)], (A, B)));
        f.Answer(A, Writes(A, "a.txt", "A\n")).Answer(B, Writes(B, "b.txt", "B\n"));
        await f.Open();
        var stop = f.Preparation.Op();
        using (var claim = new TurnFixture.ProbeBarrier("runner.claim.before"))
        {
            f.Runs.Probe = claim.Probe;
            await f.Resume();
            await claim.Reached.Task.WaitAsync(Bound);
            Assert.IsType<RunCommand.Accepted>(await f.Coordinator.Stop(f.Address, stop).WaitAsync(Bound));
            Assert.Equal(RunPhase.StopRequested, f.Read().Phase);
            Assert.Equal("Stopping", (await f.UntilStatus(RunStatus.Stopping)).Label);
        }
        var stopped = await f.UntilStatus(RunStatus.Stopped);
        Assert.Equal("Stopped", stopped.Label);
        Assert.Equal(RunPhase.Stopped, f.Read().Phase);
        Assert.Equal(0, f.TotalLaunches);
        Assert.Empty(f.Read().Claims);
        var end = Assert.IsType<AttemptEnd.Recovered>(Assert.Single(f.Read().Closures).Value);
        Assert.Equal((RecoveryOutcome.NotStarted, stop, WorkflowRunCoordinator.StoppedReason), (end.Outcome, end.Confirmation, end.Reason));
        Assert.Equal(TaskState.Failed, stopped.Tasks[A].State);
        Assert.Equal(TaskState.Pending, stopped.Tasks[B].State);
        await f.Until(view => view.PinsReleased);
        Assert.IsType<RunCommand.Refused>(await f.Coordinator.Resume(f.Address).WaitAsync(Bound));
    }

    [Fact]
    public async Task Stop_cancels_a_running_turn_and_keeps_its_partial_write()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Agent(B)], (A, B)));
        f.Answer(A, Waits(A, Path.Combine(f.Evidence, "never"))).Answer(B, Writes(B, "b.txt", "B\n"));
        await f.Open();
        await f.Resume();
        await f.Until(view => view.Tasks[A].State == TaskState.Running);
        await TurnFixture.WaitUntilAsync(() => File.Exists(Path.Combine(f.Checkout(A), "partial.txt")));
        Assert.IsType<RunCommand.Accepted>(await f.Coordinator.Stop(f.Address, f.Preparation.Op()).WaitAsync(Bound));
        var stopped = await f.UntilStatus(RunStatus.Stopped);
        Assert.Empty(f.Read().Results);
        Assert.Equal(TerminalAttemptOutcome.Cancelled, Assert.IsType<AttemptEnd.Logged>(stopped.Tasks[A].End).Outcome);
        Assert.Equal("partial\n", File.ReadAllText(Path.Combine(f.Checkout(A), "partial.txt")));
        Assert.Equal([1, 0], new[] { f.Launches(A), f.Launches(B) });
    }

    [Fact]
    public async Task Stop_closes_a_waiting_attempt_as_cancelled()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A, conversation: ConversationMode.Chat), Agent(B)], (A, B)));
        f.Answer(A, Writes(A, "a.txt", "A\n")).Answer(B, Writes(B, "b.txt", "B\n"));
        await f.Open();
        await f.Resume();
        var waiting = await f.UntilStatus(RunStatus.Waiting);
        Assert.Equal((TaskState.Waiting, AttemptStatus.WaitingForInput), (waiting.Tasks[A].State, waiting.Tasks[A].Status));
        Assert.Equal(0, waiting.Slots);
        Assert.Empty(f.Read().Closures);
        Assert.Equal("Waiting", waiting.Label);
        Assert.IsType<RunCommand.Accepted>(await f.Coordinator.Stop(f.Address, f.Preparation.Op()).WaitAsync(Bound));
        var stopped = await f.UntilStatus(RunStatus.Stopped);
        Assert.Equal(TerminalAttemptOutcome.Cancelled, Assert.IsType<AttemptEnd.Logged>(stopped.Tasks[A].End).Outcome);
        Assert.Equal([1, 0], new[] { f.Launches(A), f.Launches(B) });
        Assert.Empty(f.Read().Results);
    }

    [Fact]
    public async Task A_claim_that_wins_before_the_stop_settles_as_cancelled()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Agent(B)], (A, B)));
        f.Answer(A, Waits(A, Path.Combine(f.Evidence, "never"))).Answer(B, Writes(B, "b.txt", "B\n"));
        await f.Open();
        using (var claimed = new TurnFixture.ProbeBarrier("runner.claim.after"))
        {
            f.Runs.Probe = claimed.Probe;
            await f.Resume();
            await claimed.Reached.Task.WaitAsync(Bound);
            Assert.IsType<RunCommand.Accepted>(await f.Coordinator.Stop(f.Address, f.Preparation.Op()).WaitAsync(Bound));
            Assert.Single(f.Read().Claims);
        }
        var stopped = await f.UntilStatus(RunStatus.Stopped);
        Assert.Equal(TerminalAttemptOutcome.Cancelled, Assert.IsType<AttemptEnd.Logged>(stopped.Tasks[A].End).Outcome);
        Assert.Empty(f.Read().Results);
        Assert.Equal(0, f.Launches(B));
    }

    [Fact]
    public async Task Stop_closes_a_reserved_attempt_whose_start_was_blocked()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Agent(B)], (A, B)));
        f.Answer(A, Writes(A, "result.txt", "done\n")).Answer(B, Writes(B, "b.txt", "B\n"));
        await f.Open();
        var drifted = false;
        f.Runs.Probe = point =>
        {
            if (point != "runner.claim.before" || drifted || f.Read().Results.IsEmpty) return;
            drifted = true;
            File.WriteAllText(Path.Combine(f.Checkout(A), "result.txt"), "late\n");
        };
        await f.Resume();
        var stuck = await f.UntilStatus(RunStatus.NeedsAttention);
        Assert.Equal(TaskState.Blocked, stuck.Tasks[B].State);
        var reserved = RunProjection.LatestAttempts(f.Read())[B];
        Assert.IsType<RunCommand.Accepted>(await f.Coordinator.Stop(f.Address, f.Preparation.Op()).WaitAsync(Bound));
        await f.UntilStatus(RunStatus.Stopped);
        Assert.Equal(RecoveryOutcome.NotStarted, Assert.IsType<AttemptEnd.Recovered>(f.Read().Closures[reserved]).Outcome);
        Assert.Equal(0, f.Launches(B));
    }

    [Fact]
    public async Task A_start_blocked_after_the_stop_still_closes_its_reserved_attempt()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Agent(B)], (A, B)));
        f.Answer(A, Writes(A, "result.txt", "done\n")).Answer(B, Writes(B, "b.txt", "B\n"));
        await f.Open();
        using var claim = new TurnFixture.ProbeBarrier("runner.claim.before");
        f.Runs.Probe = point =>
        {
            // B's claim waits here; by the time it rechecks its producer, the run is stopping and A's checkout drifted.
            if (point != "runner.claim.before" || f.Read().Results.IsEmpty) return;
            File.WriteAllText(Path.Combine(f.Checkout(A), "result.txt"), "late\n");
            claim.Probe(point);
        };
        await f.Resume();
        await claim.Reached.Task.WaitAsync(Bound);
        Assert.IsType<RunCommand.Accepted>(await f.Coordinator.Stop(f.Address, f.Preparation.Op()).WaitAsync(Bound));
        await f.UntilStatus(RunStatus.Stopping);
        var reserved = RunProjection.LatestAttempts(f.Read())[B];
        claim.Dispose();
        await f.UntilStatus(RunStatus.Stopped);
        Assert.Equal(RecoveryOutcome.NotStarted, Assert.IsType<AttemptEnd.Recovered>(f.Read().Closures[reserved]).Outcome);
        Assert.Contains(f.Read().Blocks.Values, block => !block.Resolved && block.Block.Task == A);
        Assert.Equal(0, f.Launches(B));
    }

    [Fact]
    public async Task Stop_lets_a_settling_success_publish()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Agent(B)], (A, B)));
        f.Answer(A, Writes(A, "result.txt", "done\n")).Answer(B, Writes(B, "b.txt", "B\n"));
        await f.Open();
        using (var cleanup = new TurnFixture.ProbeBarrier("runner.cleanup.inside"))
        {
            f.Runs.Probe = cleanup.Probe;
            await f.Resume();
            await cleanup.Reached.Task.WaitAsync(Bound);
            Assert.IsType<RunCommand.Accepted>(await f.Coordinator.Stop(f.Address, f.Preparation.Op()).WaitAsync(Bound));
            Assert.Equal(TaskState.Settling, (await f.UntilStatus(RunStatus.Stopping)).Tasks[A].State);
        }
        var stopped = await f.UntilStatus(RunStatus.Stopped);
        Assert.Equal(TaskState.Done, stopped.Tasks[A].State);
        Assert.Equal("done\n", f.ResultFile(A, "result.txt"));
        Assert.Equal([1, 0], new[] { f.Launches(A), f.Launches(B) });
    }

    [Theory]
    [InlineData("journal.capture-1.after", "Stopped")]
    [InlineData("runner.request.after", "Stopped")]
    [InlineData("runner.claim.after", "Stopping")]
    public async Task Stop_after_a_reopen_reconciles_without_resume(string point, string status)
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Agent(B)], (A, B)));
        f.Answer(A, Writes(A, "result.txt", "done\n")).Answer(B, Writes(B, "b.txt", "B\n"));
        await f.Crash(A, point);
        var launches = f.Launches(A);
        await f.Open();
        Assert.IsType<RunCommand.Accepted>(await f.Coordinator.Stop(f.Address, f.Preparation.Op()).WaitAsync(Bound));
        if (status == "Stopped")
        {
            var stopped = await f.UntilStatus(RunStatus.Stopped);
            if (point == "journal.capture-1.after") Assert.Equal("done\n", f.ResultFile(A, "result.txt"));
            else Assert.IsType<AttemptEnd.Recovered>(stopped.Tasks[A].End);
        }
        else
        {
            var stopping = await f.Until(view => view.Status == RunStatus.Stopping && view.Tasks[A].State == TaskState.Uncertain);
            Assert.Equal(UnresolvedReason.Uncertain, stopping.Tasks[A].Unresolved);
            Assert.Equal(RunPhase.StopRequested, f.Read().Phase);
        }
        Assert.Equal([launches, 0], new[] { f.Launches(A), f.Launches(B) });
    }

    [Fact]
    public async Task A_repeated_stop_converges_and_a_settled_run_refuses_it()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A)]));
        f.Answer(A, Writes(A, "a.txt", "A\n"));
        await f.Open();
        var first = f.Preparation.Op();
        await Task.WhenAll(f.Coordinator.Stop(f.Address, first), f.Coordinator.Stop(f.Address, first), f.Coordinator.Stop(f.Address, f.Preparation.Op()));
        await f.UntilStatus(RunStatus.Stopped);
        Assert.Single(f.Read().Receipts.Values, entry => entry.Event is RunEvent.StopRequested);
        Assert.Equal(first, RunOperations.Stop(f.Read()));
        Assert.Equal(RunProblem.RunStopped, Assert.IsType<RunCommand.Refused>(await f.Coordinator.Stop(f.Address, f.Preparation.Op()).WaitAsync(Bound)).Reason.Problem);
        Assert.Equal(0, f.TotalLaunches);
    }
}
