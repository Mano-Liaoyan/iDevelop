using IDevelop.Core.Tests.Turns;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Coordination.CoordinatorFixture;
using RunFixtures = IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Coordination;

/// <summary>Dispatch along dependency connections (E3b). Every ready task starts at once (#89), so only dependencies order the claims.</summary>
public sealed class DispatchTests
{
    private static FakeRule Fails(TaskId task) => FakeRule.On().Print(FakeAgents.SessionLine(ClientId.Codex, "session-" + Name(task))).Exit(1);

    /// <summary>The order in which the run's turns were claimed.</summary>
    private static TaskId[] ClaimOrder(CoordinatorFixture f)
    {
        var record = f.Read();
        return [.. record.Receipts.Values.Where(entry => entry.Event is RunEvent.TurnClaimed).OrderBy(entry => entry.Sequence)
            .Select(entry => record.Attempts[((RunEvent.TurnClaimed)entry.Event).Key.Attempt].Task)];
    }

    /// <summary>Asserts that each task was claimed once and after each task it depends on.</summary>
    private static void ClaimedAfter(TaskId[] order, params (TaskId Before, TaskId After)[] dependencies)
    {
        Assert.Equal(order.Length, order.Distinct().Count());
        foreach (var (before, after) in dependencies)
            Assert.True(Array.IndexOf(order, before) < Array.IndexOf(order, after), $"{Name(after)} was claimed before {Name(before)}.");
    }

    [Fact]
    public async Task A_chain_runs_each_task_once_without_selection_and_completes()
    {
        await using var f = new CoordinatorFixture(Chain());
        f.Answer(A, Writes(A, "a.txt", "A\n")).Answer(B, Writes(B, "b.txt", "B\n")).Answer(X, Reports(X));
        await f.Open();
        var paused = await f.Decided();
        Assert.Equal(RunStatus.Paused, paused.Status);
        Assert.Equal([TaskState.Ready, TaskState.Pending, TaskState.Ready], new[] { A, B, X }.Select(task => paused.Tasks[task].State));
        Assert.Equal(0, f.TotalLaunches);
        await Task.WhenAll(f.Coordinator.Resume(f.Address), f.Coordinator.Resume(f.Address));
        for (var refresh = 0; refresh < 20; refresh++) f.Coordinator.Refresh();
        var done = await f.UntilStatus(RunStatus.Completed);
        Assert.Equal("Completed", done.Label);
        Assert.Equal(RunPhase.Completed, f.Read().Phase);
        Assert.Equal([1, 1, 1], new[] { f.Launches(A), f.Launches(B), f.Launches(X) });
        Assert.Equal(3, f.TotalLaunches);
        Assert.Equal([1, 1, 1], new[] { f.Claims(A), f.Claims(B), f.Claims(X) });
        Assert.Equal(3, ClaimOrder(f).Length);
        ClaimedAfter(ClaimOrder(f), (A, B));
        Assert.StartsWith("Build B.", f.Prompt(B));
        Assert.Contains("A ready.\n", f.Prompt(B));
        Assert.Equal("A\n", f.ResultFile(B, "a.txt"));
        Assert.Equal("B\n", f.ResultFile(B, "b.txt"));
        Assert.Equal("X ready.\n", f.Result(X).Report);
        Assert.All(done.Tasks.Values, task => Assert.Equal(TaskState.Done, task.State));
        await f.Until(view => view.PinsReleased);
        Assert.Equal("", f.Refs(RunLayout.PinPrefix(f.Read().RunKey!)));
        Assert.Equal(2, f.Refs($"refs/idp/{f.Read().RunKey}/result/").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public async Task A_successor_waits_for_its_producer_s_acceptance()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Agent(B)], (A, B)));
        f.Answer(A, Writes(A, "a.txt", "A\n")).Answer(B, Writes(B, "b.txt", "B\n"));
        await f.Open();
        using (var held = new TurnFixture.ProbeBarrier("coordinator.publish.before"))
        {
            f.Runs.Probe = held.Probe;
            await f.Resume();
            await held.Reached.Task.WaitAsync(Bound);
            Assert.Equal(TerminalAttemptOutcome.Succeeded, Assert.IsType<AttemptEnd.Logged>(Assert.Single(f.Read().Closures).Value).Outcome);
            f.Coordinator.Refresh();
            var waiting = await f.Until(view => view.Tasks[A].State == TaskState.Settling);
            Assert.Equal(TaskState.Pending, waiting.Tasks[B].State);
            Assert.Equal([A], waiting.Tasks[B].HeldBy);
            Assert.Equal(0, f.Launches(B));
            Assert.Empty(f.Read().Results);
        }
        await f.UntilStatus(RunStatus.Completed);
        Assert.Equal(1, f.Launches(B));
        Assert.Contains("A ready.\n", f.Prompt(B));
    }

    [Fact]
    public async Task At_the_bound_a_root_exit_frees_its_slot_while_cleanup_is_pending()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Agent(X, readOnly: true)]));
        f.Answer(A, Writes(A, "a.txt", "A\n")).Answer(X, Reports(X));
        await f.Open();
        f.Runs.ClientRoots = 1;
        using (var cleanup = new TurnFixture.ProbeBarrier("runner.cleanup.inside"))
        {
            f.Runs.Probe = cleanup.Probe;
            await f.Resume();
            await cleanup.Reached.Task.WaitAsync(Bound);
            var settling = await f.Until(view => view.Tasks[X].State is TaskState.Running or TaskState.Settling);
            Assert.Equal(TaskState.Settling, settling.Tasks[A].State);
            await TurnFixture.WaitUntilAsync(() => f.Launches(X) == 1);
            Assert.IsType<LeaseTake.Busy>(f.Coordinator.Permit!.TakeTask(A));
            Assert.Empty(f.Read().TurnClosures);
        }
        await f.UntilStatus(RunStatus.Completed);
        Assert.Equal([1, 1], new[] { f.Launches(A), f.Launches(X) });
    }

    [Fact]
    public async Task A_diamond_isolates_siblings_and_joins_them_for_the_consumer()
    {
        await using var f = new CoordinatorFixture(Diamond());
        f.Answer(A, Writes(A, "a.txt", "A\n")).Answer(B, Writes(B, "b.txt", "B\n")).Answer(C, Writes(C, "c.txt", "C\n")).Answer(X, Reports(X))
            .Answer(D, FakeRule.On()
                .Print(FakeAgents.SessionLine(ClientId.Codex, "session-D"))
                .Copy("plan.txt", Path.Combine(f.Evidence, "d-plan.txt"))
                .Copy("a.txt", Path.Combine(f.Evidence, "d-a.txt"))
                .Copy("b.txt", Path.Combine(f.Evidence, "d-b.txt"))
                .Copy("c.txt", Path.Combine(f.Evidence, "d-c.txt"))
                .Print(FakeAgents.ReplyLines(ClientId.Codex, "D ready.\n")));
        await f.Open();
        await f.Resume();
        await f.UntilStatus(RunStatus.Completed);
        Assert.Equal(5, ClaimOrder(f).Length);
        ClaimedAfter(ClaimOrder(f), (A, B), (A, C), (B, D), (C, D));
        Assert.Equal(("B\n", (string?)null), (f.ResultFile(B, "b.txt"), f.ResultFile(B, "c.txt")));
        Assert.Equal(("C\n", (string?)null), (f.ResultFile(C, "c.txt"), f.ResultFile(C, "b.txt")));
        Assert.Equal(["approved\n", "A\n", "B\n", "C\n"], new[] { "plan", "a", "b", "c" }.Select(name => File.ReadAllText(Path.Combine(f.Evidence, $"d-{name}.txt"))));
        Assert.Contains("B ready.\n", f.Prompt(D));
        Assert.Contains("C ready.\n", f.Prompt(D));
        Assert.Equal(1, f.Launches(D));
    }

    [Fact]
    public async Task Conflicting_siblings_block_the_join_while_unrelated_work_continues()
    {
        await using var f = new CoordinatorFixture(Diamond());
        f.Answer(A, Writes(A, "a.txt", "A\n")).Answer(B, Writes(B, "settings.txt", "B\n")).Answer(C, Writes(C, "settings.txt", "C\n"))
            .Answer(X, Reports(X)).Answer(D, Reports(D));
        await f.Open();
        await f.Resume();
        var stuck = await f.UntilStatus(RunStatus.NeedsAttention);
        var block = stuck.Tasks[D].Block!;
        Assert.Equal(TaskState.Blocked, stuck.Tasks[D].State);
        Assert.Equal("FanInConflict", block.Problem.ToString());
        Assert.Equal(["settings.txt"], block.Conflict!.Paths.ToArray());
        Assert.Equal([0, 1, 1, 1], new[] { f.Launches(D), f.Launches(X), f.Launches(B), f.Launches(C) });
        Assert.Equal(RunPhase.Approved, f.Read().Phase);
        Assert.Equal("Needs attention", stuck.Label);
    }

    [Fact]
    public async Task An_ordinary_failure_holds_back_only_its_dependents()
    {
        await using var f = new CoordinatorFixture(Diamond());
        f.Answer(A, Writes(A, "a.txt", "A\n")).Answer(B, Fails(B)).Answer(C, Writes(C, "c.txt", "C\n")).Answer(X, Reports(X)).Answer(D, Reports(D));
        await f.Open();
        await f.Resume();
        var stuck = await f.UntilStatus(RunStatus.NeedsAttention);
        Assert.Equal([1, 1, 0, 1], new[] { f.Launches(B), f.Launches(C), f.Launches(D), f.Launches(X) });
        Assert.Equal(TaskState.Failed, stuck.Tasks[B].State);
        Assert.Equal(TerminalAttemptOutcome.Failed, Assert.IsType<AttemptEnd.Logged>(stuck.Tasks[B].End).Outcome);
        Assert.Equal(TaskState.Pending, stuck.Tasks[D].State);
        Assert.Equal([B], stuck.Tasks[D].HeldBy.ToArray());
        Assert.Equal([TaskState.Done, TaskState.Done, TaskState.Done], new[] { A, C, X }.Select(task => stuck.Tasks[task].State));
        Assert.Equal(4, f.Read().Attempts.Count);
        Assert.Equal("Needs attention", stuck.Label);
        f.Coordinator.Refresh();
        await f.Coordinator.Resume(f.Address);
        Assert.Equal(RunStatus.NeedsAttention, (await f.UntilStatus(RunStatus.NeedsAttention)).Status);
        Assert.Equal(4, f.Read().Attempts.Count);
        Assert.Equal(1, f.Launches(B));
    }

    [Fact]
    public async Task Missing_context_never_holds_a_task_back()
    {
        var gate = new TaskDefinition(C, BuiltInBlueprints.Review) { Title = "Review" };
        var workflow = RunFixtures.Connect(Graph([Agent(A), Agent(B), gate], (A, B)), C, B, ConnectionKind.Context);
        await using var f = new CoordinatorFixture(workflow);
        f.Answer(A, Writes(A, "a.txt", "A\n")).Answer(B, Writes(B, "b.txt", "B\n"));
        await f.Open();
        await f.Resume();
        var stuck = await f.Until(view => view.Tasks[B].State == TaskState.Done);
        Assert.Equal(TaskState.Unsupported, stuck.Tasks[C].State);
        Assert.Equal(1, f.Launches(B));
        Assert.Contains("A ready.\n", f.Prompt(B));
        var inputs = f.Read().Inputs[f.Result(B).Inputs];
        Assert.Contains(inputs.Bindings, binding => binding is InputBinding.MissingContext missing && missing.Edge == new ConnectionKey(C, B));
        Assert.Equal(RunStatus.NeedsAttention, (await f.UntilStatus(RunStatus.NeedsAttention)).Status);
    }
}
