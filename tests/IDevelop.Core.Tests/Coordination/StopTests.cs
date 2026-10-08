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
