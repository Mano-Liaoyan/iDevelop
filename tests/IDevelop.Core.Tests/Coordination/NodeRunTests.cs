using IDevelop.Core.Tests.Turns;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Coordination.CoordinatorFixture;

namespace IDevelop.Core.Tests.Coordination;

/// <summary>
/// A run that a node's Run started (#90): it runs that node, and each task after it once all of that task's dependency
/// predecessors have results in the run. A node the person runs while the run is active joins it. Nothing starts merely
/// because it comes after another task, and the run completes once nothing more can start.
/// </summary>
public sealed class NodeRunTests
{
    /// <summary><c>A → C ← B</c>.</summary>
    private static Workflow Join() => Graph([Agent(A), Agent(B), Agent(C)], (A, C), (B, C));

    private static string Gate(CoordinatorFixture f, string name) => Path.Combine(f.Evidence, name);

    private static void Open(CoordinatorFixture f, string name) => File.WriteAllText(Gate(f, name), "go");

    /// <summary>A turn that writes <paramref name="file"/> once <paramref name="gate"/> opens, then answers.</summary>
    private static FakeRule WritesAfter(CoordinatorFixture f, TaskId task, string gate, string file) => FakeRule.On()
        .Print(FakeAgents.SessionLine(ClientId.Codex, "session-" + Name(task)))
        .WaitForFile(Gate(f, gate))
        .Write(file, $"{Name(task)}\n")
        .Print(FakeAgents.ReplyLines(ClientId.Codex, $"{Name(task)} ready.\n"));

    /// <summary>A turn of the join that copies both predecessors' files out of its checkout, so the test sees what it got.</summary>
    private static FakeRule Joins(CoordinatorFixture f) => FakeRule.On()
        .Print(FakeAgents.SessionLine(ClientId.Codex, "session-C"))
        .Copy("a.txt", Path.Combine(f.Evidence, "c-a.txt"))
        .Copy("b.txt", Path.Combine(f.Evidence, "c-b.txt"))
        .Print(FakeAgents.ReplyLines(ClientId.Codex, "C ready.\n"));

    private static (DateTimeOffset Launched, DateTimeOffset Exited) Interval(CoordinatorFixture f, TaskId task) =>
        ConcurrencyTests.Interval(f.Preparation.Store.AttemptFolder(Runs.RunFixtures.W, f.Preparation.RunId, task, RunProjection.LatestAttempts(f.Read())[task]));

    private static DateTimeOffset Accepted(CoordinatorFixture f, TaskId task) =>
        f.Read().Receipts.Values.Single(entry => entry.Event is RunEvent.ResultAccepted accepted && accepted.Result.Task == task).At;

    private static int Requests(CoordinatorFixture f) => f.Read().Receipts.Values.Count(entry => entry.Event is RunEvent.Requested);

    private static async Task<RunCommand> Request(CoordinatorFixture f, TaskId task, OperationId confirmation) =>
        await f.Coordinator.Request(f.Address, task, confirmation).WaitAsync(Bound);

    [Fact]
    public async Task Running_one_root_of_a_join_starts_neither_the_other_root_nor_the_join_and_completes_when_nothing_more_can_start()
    {
        await using var f = new CoordinatorFixture(Join(), node: A);
        f.Answer(A, Writes(A, "a.txt", "A\n")).Answer(B, Writes(B, "b.txt", "B\n")).Answer(C, Joins(f));
        await f.Open();
        await f.Resume();

        var done = await f.UntilStatus(RunStatus.Completed);

        Assert.Equal([1, 0, 0], new[] { A, B, C }.Select(f.Launches));
        Assert.Equal((TaskState.Done, TaskState.Unrequested, TaskState.Pending), (done.Tasks[A].State, done.Tasks[B].State, done.Tasks[C].State));
        Assert.Equal([B], done.Tasks[C].HeldBy);
        Assert.Equal((false, true, true), (done.Tasks[A].Dormant, done.Tasks[B].Dormant, done.Tasks[C].Dormant));
        var record = f.Read();
        Assert.Equal(RunPhase.Completed, record.Phase);
        Assert.Equal([A], record.Requested!);
        Assert.Equal([A], record.CurrentResults.Keys);
    }

    [Fact]
    public async Task A_root_run_while_the_run_is_active_joins_it_and_the_join_starts_once_both_have_results()
    {
        await using var f = new CoordinatorFixture(Join(), node: A);
        f.Answer(A, WritesAfter(f, A, "a-go", "a.txt")).Answer(B, Writes(B, "b.txt", "B\n")).Answer(C, Joins(f));
        await f.Open();
        await f.Resume();
        await f.Until(view => view.Tasks[A].State == TaskState.Running);

        Assert.IsType<RunCommand.Accepted>(await Request(f, B, new(Guid.NewGuid())));
        // B finishes first, while A still runs, and the join waits for A.
        var waiting = await f.Until(view => view.Tasks[B].State == TaskState.Done);
        Assert.Equal((TaskState.Running, TaskState.Pending), (waiting.Tasks[A].State, waiting.Tasks[C].State));
        Assert.Equal([A], waiting.Tasks[C].HeldBy);
        Assert.False(waiting.Tasks[C].Dormant);
        Assert.Equal(0, f.Launches(C));
        Open(f, "a-go");
        await f.UntilStatus(RunStatus.Completed);

        Assert.Equal([1, 1, 1], new[] { A, B, C }.Select(f.Launches));
        Assert.Equal(("A\n", "B\n"), (File.ReadAllText(Path.Combine(f.Evidence, "c-a.txt")), File.ReadAllText(Path.Combine(f.Evidence, "c-b.txt"))));
        Assert.True(Interval(f, C).Launched > new[] { Accepted(f, A), Accepted(f, B) }.Max());
        ConcurrencyTests.Overlap(Interval(f, A), Interval(f, B));
        var record = f.Read();
        Assert.Equal([A, B], record.Requested!);
        Assert.Equal(1, Requests(f));
    }

    [Fact]
    public async Task A_join_whose_other_predecessor_still_runs_waits_for_it_and_starts_once_it_finishes()
    {
        await using var f = new CoordinatorFixture(Join(), node: A);
        f.Answer(A, WritesAfter(f, A, "a-go", "a.txt")).Answer(B, WritesAfter(f, B, "b-go", "b.txt")).Answer(C, Joins(f));
        await f.Open();
        await f.Resume();
        await f.Until(view => view.Tasks[A].State == TaskState.Running);
        Assert.IsType<RunCommand.Accepted>(await Request(f, B, new(Guid.NewGuid())));
        await f.Until(view => view.Tasks[B].State == TaskState.Running);

        // A finishes first, and the join waits for B, which still runs in the same run.
        Open(f, "a-go");
        var waiting = await f.Until(view => view.Tasks[A].State == TaskState.Done);
        Assert.Equal((TaskState.Running, TaskState.Pending), (waiting.Tasks[B].State, waiting.Tasks[C].State));
        Assert.Equal([B], waiting.Tasks[C].HeldBy);
        Assert.Equal(RunStatus.Running, waiting.Status);
        Open(f, "b-go");
        await f.UntilStatus(RunStatus.Completed);

        Assert.Equal([1, 1, 1], new[] { A, B, C }.Select(f.Launches));
        Assert.True(Interval(f, C).Launched > Accepted(f, B));
        Assert.Equal(("A\n", "B\n"), (File.ReadAllText(Path.Combine(f.Evidence, "c-a.txt")), File.ReadAllText(Path.Combine(f.Evidence, "c-b.txt"))));
    }

    [Fact]
    public async Task A_chain_advances_by_itself_and_a_root_nobody_ran_never_starts()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Agent(B), Agent(C), Agent(X, readOnly: true)], (A, B), (B, C)), node: A);
        f.Answer(A, Writes(A, "a.txt", "A\n")).Answer(B, Writes(B, "b.txt", "B\n")).Answer(C, Writes(C, "c.txt", "C\n")).Answer(X, Reports(X));
        await f.Open();
        await f.Resume();

        var done = await f.UntilStatus(RunStatus.Completed);

        Assert.Equal([1, 1, 1, 0], new[] { A, B, C, X }.Select(f.Launches));
        Assert.True(Interval(f, B).Launched > Accepted(f, A));
        Assert.True(Interval(f, C).Launched > Accepted(f, B));
        Assert.Equal("A\n", f.ResultFile(C, "a.txt"));
        Assert.Equal(TaskState.Unrequested, done.Tasks[X].State);
        Assert.Equal(RunPhase.Completed, f.Read().Phase);
    }

    [Fact]
    public async Task Successors_that_become_ready_together_start_together()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Agent(B), Agent(C)], (A, B), (A, C)), node: A);
        var started = (TaskId task) => Path.Combine(f.Evidence, $"{Name(task)}.started");
        FakeRule Meets(TaskId task, TaskId other) => FakeRule.On().RecordArguments(started(task))
            .Print(FakeAgents.SessionLine(ClientId.Codex, "session-" + Name(task))).WaitForFile(started(other))
            .Write($"{Name(task).ToLowerInvariant()}.txt", $"{Name(task)}\n").Print(FakeAgents.ReplyLines(ClientId.Codex, $"{Name(task)} ready.\n"));
        f.Answer(A, Writes(A, "a.txt", "A\n")).Answer(B, Meets(B, C)).Answer(C, Meets(C, B));
        await f.Open();
        await f.Resume();

        await f.UntilStatus(RunStatus.Completed);

        ConcurrencyTests.Overlap(Interval(f, B), Interval(f, C));
        Assert.Equal([1, 1, 1], new[] { A, B, C }.Select(f.Launches));
    }

    [Fact]
    public async Task A_task_whose_predecessor_has_no_result_is_refused_and_names_it()
    {
        // A → C ← B → D: D comes after B, which nobody ran.
        await using var f = new CoordinatorFixture(Graph([Agent(A), Agent(B), Agent(C), Agent(D)], (A, C), (B, C), (B, D)), node: A);
        f.Answer(A, WritesAfter(f, A, "a-go", "a.txt")).Answer(B, Writes(B, "b.txt", "B\n")).Answer(C, Joins(f)).Answer(D, Writes(D, "d.txt", "D\n"));
        await f.Open();
        await f.Resume();
        var running = await f.Until(view => view.Tasks[A].State == TaskState.Running);
        Assert.Equal((TaskState.Unrequested, true), (running.Tasks[D].State, running.Tasks[D].Dormant));
        Assert.Equal([B], running.Tasks[D].HeldBy);

        var refused = Assert.IsType<RunCommand.Refused>(await Request(f, D, new(Guid.NewGuid())));

        Assert.Equal(new RunRejection(RunProblem.MissingDependencyResult, Task: B), refused.Reason);
        Assert.Equal(0, Requests(f));
        Open(f, "a-go");
        await f.UntilStatus(RunStatus.Completed);
        Assert.Equal([1, 0, 0, 0], new[] { A, B, C, D }.Select(f.Launches));
    }

    [Fact]
    public async Task A_request_is_recorded_once_and_a_window_that_only_reads_the_run_cannot_make_one()
    {
        await using var f = new CoordinatorFixture(Join(), node: A);
        f.Answer(A, WritesAfter(f, A, "a-go", "a.txt")).Answer(B, WritesAfter(f, B, "b-go", "b.txt")).Answer(C, Joins(f));
        await f.Open();
        await f.Resume();
        await f.Until(view => view.Tasks[A].State == TaskState.Running);
        var (runs, reader) = await f.SecondWindow();
        await using var _ = runs;

        Assert.Equal(new RunCommand.Unavailable(WorkflowRunCoordinator.ElsewhereMessage),
            await reader.Request(reader.Address, B, new(Guid.NewGuid())).WaitAsync(Bound));
        var confirmation = new OperationId(Guid.NewGuid());
        Assert.IsType<RunCommand.Accepted>(await Request(f, B, confirmation));
        Assert.IsType<RunCommand.Accepted>(await Request(f, B, confirmation));
        // Another click while B runs adds nothing either.
        Assert.IsType<RunCommand.Accepted>(await Request(f, B, new(Guid.NewGuid())));
        Open(f, "a-go");
        Open(f, "b-go");
        await f.UntilStatus(RunStatus.Completed);

        Assert.Equal(1, Requests(f));
        Assert.Equal([1, 1, 1], new[] { A, B, C }.Select(f.Launches));
    }

    [Fact]
    public async Task A_request_survives_a_restart_launches_nothing_before_Resume_and_starts_its_task_once()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Agent(B)]), node: A);
        f.Answer(A, WritesAfter(f, A, "a-go", "a.txt")).Answer(B, Writes(B, "b.txt", "B\n"));
        Open(f, "a-go");
        await f.Open();
        // The window never resumes, as after a crash; its Run of B is recorded all the same.
        Assert.IsType<RunCommand.Accepted>(await Request(f, B, new(Guid.NewGuid())));
        var paused = await f.Decided();
        Assert.Equal((TaskState.Ready, TaskState.Ready), (paused.Tasks[A].State, paused.Tasks[B].State));

        await f.Reopen();
        var reopened = await f.Decided();

        Assert.Equal(RunStatus.Paused, reopened.Status);
        Assert.Equal(TaskState.Ready, reopened.Tasks[B].State);
        Assert.Equal(0, f.TotalLaunches);
        await f.Resume();
        await f.UntilStatus(RunStatus.Completed);
        Assert.Equal([1, 1], new[] { A, B }.Select(f.Launches));
        Assert.Equal([A, B], f.Read().Requested!);
    }

    [Fact]
    public async Task Stop_ends_a_node_run_and_its_tasks_cannot_be_added_any_more()
    {
        await using var f = new CoordinatorFixture(Join(), node: A);
        f.Answer(A, WritesAfter(f, A, "never", "a.txt")).Answer(B, Writes(B, "b.txt", "B\n")).Answer(C, Joins(f));
        await f.Open();
        await f.Resume();
        await f.Until(view => view.Tasks[A].State == TaskState.Running);

        Assert.IsType<RunCommand.Accepted>(await f.Coordinator.Stop(f.Address, new(Guid.NewGuid())).WaitAsync(Bound));
        var stopped = await f.UntilStatus(RunStatus.Stopped);

        Assert.Equal(new RunCommand.Refused(new(RunProblem.RunStopped)), await Request(f, B, new(Guid.NewGuid())));
        Assert.Equal([1, 0, 0], new[] { A, B, C }.Select(f.Launches));
        Assert.Equal(TaskState.Failed, stopped.Tasks[A].State);
        Assert.Equal(0, Requests(f));
    }

    [Fact]
    public async Task Run_Workflow_still_starts_every_root_and_waits_for_every_task()
    {
        await using var f = new CoordinatorFixture(Join());
        f.Answer(A, Writes(A, "a.txt", "A\n")).Answer(B, Writes(B, "b.txt", "B\n")).Answer(C, Joins(f));
        await f.Open();
        await f.Resume();

        await f.UntilStatus(RunStatus.Completed);

        Assert.Equal([1, 1, 1], new[] { A, B, C }.Select(f.Launches));
        Assert.Null(f.Read().Requested);
        // A request in a run of every root adds nothing: every task is part of it already.
        Assert.Equal(0, Requests(f));
    }
}
