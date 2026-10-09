using IDevelop.Execution;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Runs;

/// <summary>
/// Which results of a workflow's settled runs still count as complete (#90), on hand-built runs without Git or clients:
/// its run's current, non-stale result, from the newest run that ran the task, with the task and the connections into it
/// unchanged, and each result it took counting the same way.
/// </summary>
public sealed class RunHistoryTests
{
    private static readonly RunId Third = new(Guid.Parse("00000000-0000-0000-0000-000000000012"));

    /// <summary><c>T → U → C</c>, three read-only agents.</summary>
    private static Workflow Chain() => Connect(Connect(FixtureWorkflow(Task(), Task(U), Task(C)), T, U), U, C);

    private static RunHistory History(RunFixtures f, Workflow? workflow = null) => RunHistory.Of(f.Store.Records(W), workflow ?? f.Workflow);

    /// <summary>Settles <paramref name="run"/> and lets go of it, as its window does, so the next run can take its tasks.</summary>
    private static void Settle(RunFixtures f, RunId run, RunOutcome outcome = RunOutcome.Stopped)
    {
        Assert.IsType<RunDecision.Recorded>(f.Store.Settle(f.PermitFor(run), f.Op(), outcome));
        f.ReleaseControl();
    }

    /// <summary>Runs <c>T</c>, <c>U</c>, and <c>C</c> in <paramref name="run"/> started from <c>T</c>, and settles it.</summary>
    private static (ResultRecord T, ResultRecord U, ResultRecord C) RunAll(RunFixtures f, RunId run)
    {
        f.Approve(run: run, node: T);
        var t = f.Complete(f.Reserve(T, run: run), run: run);
        var u = f.Complete(f.Reserve(U, run: run), run: run);
        var c = f.Complete(f.Reserve(C, run: run), run: run);
        Settle(f, run, RunOutcome.Completed);
        return (t, u, c);
    }

    private static TaskHistory.Current Current(RunHistory history, TaskId task) => Assert.IsType<TaskHistory.Current>(history[task]);

    [Fact]
    public void A_settled_run_s_current_results_count_and_an_active_run_s_do_not_yet()
    {
        using var f = new RunFixtures(Chain());
        f.Approve(node: T);
        var t = f.Complete(f.Reserve(T));

        Assert.IsType<TaskHistory.None>(History(f)[T]);
        Settle(f, Run);

        var history = History(f);
        Assert.Equal((Run, t.Id), (Current(history, T).Result.Run.Id, Current(history, T).Result.Result.Id));
        Assert.IsType<TaskHistory.None>(history[U]);
        Assert.Equal([Run], history.Runs.Select(record => record.Id));
    }

    [Fact]
    public void The_newest_run_that_ran_a_task_decides_even_when_it_left_no_result()
    {
        using var f = new RunFixtures(Chain());
        var first = RunAll(f, Run);
        // The second run starts from U, whose attempt fails. T's carried result counts as the same result as before.
        var seed = new RunRecord(OtherRun, W, new(Base, BaseChoice.Head), Revision.Capture(f.Workflow)) { Schema = 3, Requested = [U] };
        var operation = f.Op();
        var carried = Carrying.Build(null, seed, History(f), operation);
        Assert.Equal([T], carried.Carried.Select(item => item.Result.Task));
        Assert.IsType<RunDecision.Created>(f.Store.Approve(W, OtherRun, operation, Revision.Capture(f.Workflow), new(Base, BaseChoice.Head), node: U,
            carried: carried.Carried));
        var failed = f.Reserve(U, run: OtherRun);
        f.Claim(failed, OtherRun);
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.PermitFor(OtherRun), f.Op(), failed.Attempt.Id, TerminalAttemptOutcome.Failed,
            f.WriteLog(failed, TerminalAttemptOutcome.Failed, run: OtherRun)));
        Settle(f, OtherRun);

        var history = History(f);

        Assert.IsType<TaskHistory.None>(history[U]);
        Assert.Equal(OtherRun, Current(history, T).Result.Run.Id);
        Assert.Equal((Run, first.T.Id), history.Root(Current(history, T).Result.Run, Current(history, T).Result.Result.Id));
        // C took U's result from the first run, which no longer counts.
        Assert.Equal((OutOfDateReason.InputReplaced, (TaskId?)U), Reason(history[C]));
        Assert.Equal(first.C.Id, Assert.IsType<TaskHistory.OutOfDate>(history[C]).Result.Result.Id);
    }

    [Fact]
    public void A_task_whose_definition_or_connections_changed_since_it_ran_is_out_of_date_and_so_are_the_tasks_after_it()
    {
        using var f = new RunFixtures(Chain());
        RunAll(f, Run);
        var renamed = Edit(f.Workflow, new WorkflowEdit.SetExecution(U, f.Workflow.Tasks[U].Execution! with { Model = "m2" }));
        var connected = Edit(Edit(f.Workflow, TestSupport.TestNodes.Place(Task(D), new(0, 0))), new WorkflowEdit.Connect(new(D, U), ConnectionKind.Context));

        foreach (var workflow in new[] { renamed, connected })
        {
            var history = History(f, workflow);
            Current(history, T);
            Assert.Equal((OutOfDateReason.Changed, (TaskId?)null), Reason(history[U]));
            Assert.Equal((OutOfDateReason.InputOutOfDate, (TaskId?)U), Reason(history[C]));
        }
        Current(History(f), U);
    }

    [Fact]
    public void A_result_that_took_a_result_a_later_run_replaced_is_out_of_date()
    {
        using var f = new RunFixtures(Chain());
        RunAll(f, Run);
        // The second run reruns T and stops before U.
        f.Approve(run: OtherRun, node: T);
        var t = f.Complete(f.Reserve(T, run: OtherRun), run: OtherRun);
        Settle(f, OtherRun);

        var history = History(f);

        Assert.Equal(t.Id, Current(history, T).Result.Result.Id);
        Assert.Equal((OutOfDateReason.InputReplaced, (TaskId?)T), Reason(history[U]));
        Assert.Equal((OutOfDateReason.InputOutOfDate, (TaskId?)U), Reason(history[C]));
    }

    [Fact]
    public void A_result_that_was_stale_in_its_own_run_does_not_count()
    {
        using var f = new RunFixtures(Chain());
        f.Approve(node: T);
        var t = f.Complete(f.Reserve(T));
        f.Complete(f.Reserve(U));
        var retried = f.Reserve(T, new AttemptCause.Retry(Assert.IsType<ResultOrigin.Executed>(t.Origin).Attempt, f.Op()));
        var newer = f.Complete(retried, supersedes: t.Id);
        Settle(f, Run);

        var history = History(f);

        Assert.Equal(newer.Id, Current(history, T).Result.Result.Id);
        Assert.Equal((OutOfDateReason.InputReplaced, (TaskId?)T), Reason(history[U]));
        Assert.IsType<TaskHistory.None>(history[C]);
    }

    [Fact]
    public void A_result_that_a_later_attempt_of_its_run_replaced_or_that_a_block_holds_does_not_count()
    {
        using var f = new RunFixtures(Chain());
        f.Approve(node: T);
        var t = f.Complete(f.Reserve(T));
        f.Complete(f.Reserve(U));
        // T's retry fails after its result was accepted, so the run shows T as failed.
        var retry = f.Reserve(T, new AttemptCause.Retry(Assert.IsType<ResultOrigin.Executed>(t.Origin).Attempt, f.Op()));
        f.Claim(retry);
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), retry.Attempt.Id, TerminalAttemptOutcome.Failed,
            f.WriteLog(retry, TerminalAttemptOutcome.Failed)));
        // A block holds U's attempt, which published its result.
        var u = f.Read().CurrentResults[U];
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, f.Op(), new RunEvent.Blocked(new(f.Op(), U,
            Assert.IsType<ResultOrigin.Executed>(u.Origin).Attempt, MaterializationProblem.DirtyWorktree, u.Inputs, [], "Checkout changed.")
        { Scope = BlockScope.Checkout.Whole })));
        Settle(f, Run);

        var history = History(f);

        Assert.IsType<TaskHistory.None>(history[T]);
        Assert.IsType<TaskHistory.None>(history[U]);
    }

    [Fact]
    public void Runs_count_in_the_order_they_were_approved()
    {
        using var f = new RunFixtures(Chain());
        RunAll(f, Run);
        f.Approve(run: Third, node: T);
        var t = f.Complete(f.Reserve(T, run: Third), run: Third);
        Settle(f, Third);

        Assert.Equal([Run, Third], History(f).Runs.Select(record => record.Id));
        Assert.Equal(t.Id, Current(History(f), T).Result.Result.Id);
    }

    [Fact]
    public void A_task_its_newest_run_could_start_and_did_not_waits_for_the_predecessors_without_a_result_until_a_later_run_has_one()
    {
        // T → C ← U: the first run starts from T, so C waits for U, and U waits for nothing, since no run could start it.
        using var f = new RunFixtures(Connect(Connect(FixtureWorkflow(Task(), Task(U), Task(C), Task(D)), T, C), U, C));
        f.Approve(node: T);
        f.Complete(f.Reserve(T));
        Settle(f, Run);

        var history = History(f);
        Assert.Equal([U], history.WaitsFor(C));
        Assert.Empty(history.WaitsFor(U));
        Assert.Empty(history.WaitsFor(T));
        // A later run of D, which cannot start C, leaves C waiting, now from a run before the newest.
        f.Approve(run: OtherRun, node: D);
        f.Complete(f.Reserve(D, run: OtherRun), run: OtherRun);
        Settle(f, OtherRun);
        Assert.Equal([U], History(f).WaitsFor(C));
        // A run of U carries T's result for C, and C, though it was stopped before it started, waits for nothing more.
        var operation = f.Op();
        var seed = new RunRecord(Third, W, new(Base, BaseChoice.Head), Revision.Capture(f.Workflow)) { Schema = 3, Requested = [U] };
        Assert.IsType<RunDecision.Created>(f.Store.Approve(W, Third, operation, Revision.Capture(f.Workflow), new(Base, BaseChoice.Head), node: U,
            carried: Carrying.Build(null, seed, History(f), operation).Carried));
        f.Complete(f.Reserve(U, run: Third), run: Third);
        Assert.IsType<RunDecision.Recorded>(f.Store.Settle(f.PermitFor(Third), f.Op(), RunOutcome.Stopped));
        f.ReleaseControl();
        Assert.Empty(History(f).WaitsFor(C));
    }

    [Fact]
    public void A_predecessor_that_a_later_run_carried_ends_the_wait_of_a_task_that_run_could_not_start()
    {
        // T → C ← U, and U → E ← D. U ran first. The second run starts from T and does not carry U, as when U's code
        // conflicted then, so C waits for U. The third starts from D and carries U, which ends C's wait.
        var e = new TaskId(Guid.Parse("00000000-0000-0000-0000-000000000006"));
        var workflow = Connect(Connect(Connect(Connect(FixtureWorkflow(Task(), Task(U), Task(C), Task(D), Task(e)), T, C), U, C), U, e), D, e);
        using var f = new RunFixtures(workflow);
        f.Approve(node: U);
        f.Complete(f.Reserve(U));
        Settle(f, Run);
        f.Approve(run: OtherRun, node: T);
        f.Complete(f.Reserve(T, run: OtherRun), run: OtherRun);
        Settle(f, OtherRun);
        Assert.Equal([U], History(f).WaitsFor(C));

        var operation = f.Op();
        var seed = new RunRecord(Third, W, new(Base, BaseChoice.Head), Revision.Capture(f.Workflow)) { Schema = 3, Requested = [D] };
        Assert.IsType<RunDecision.Created>(f.Store.Approve(W, Third, operation, Revision.Capture(f.Workflow), new(Base, BaseChoice.Head), node: D,
            carried: Carrying.Build(null, seed, History(f), operation).Carried));
        Settle(f, Third);

        Assert.Empty(History(f).WaitsFor(C));
        Assert.Equal(Third, History(f).CurrentOf(U)!.Run.Id);
    }

    private static (OutOfDateReason, TaskId?) Reason(TaskHistory history) =>
        Assert.IsType<TaskHistory.OutOfDate>(history) is var outOfDate ? (outOfDate.Reason, outOfDate.Input) : default;
}
