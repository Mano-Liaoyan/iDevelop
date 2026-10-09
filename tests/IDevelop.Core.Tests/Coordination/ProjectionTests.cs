using IDevelop.Core.Tests.Runs;
using IDevelop.Execution;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Coordination;

/// <summary>The run projection on hand-built journals, without Git or clients (E3b).</summary>
public sealed class ProjectionTests
{
    private static readonly Dictionary<TaskId, LiveStage> NoLive = [];
    private static readonly Dictionary<TaskId, TaskHold> NoHolds = [];

    private static RunView View(RunFixtures f, bool controlled = true, bool resumed = true, IReadOnlyDictionary<TaskId, TaskHold>? holds = null)
    {
        var record = f.Read();
        return RunProjection.Of(new(f.Project, W, Run), record,
            attempt => AttemptEvidence.Read(f.Store.AttemptFolder(W, Run, attempt.Task, attempt.Id)).Record, NoLive, holds ?? NoHolds, controlled, resumed);
    }

    /// <summary><c>T → U</c>, with a context connection <c>C ~ U</c>.</summary>
    private static Workflow Graph(ConversationMode consumer = ConversationMode.Autonomous) =>
        Connect(Connect(FixtureWorkflow(Task(), Task(U) with { Conversation = consumer }, Task(C)), T, U), C, U, ConnectionKind.Context);

    private static void Fail(RunFixtures f, RunEvent.Reserved reservation)
    {
        f.Claim(reservation);
        var checkpoint = f.WriteLog(reservation, TerminalAttemptOutcome.Failed);
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), reservation.Attempt.Id, TerminalAttemptOutcome.Failed, checkpoint));
    }

    private static OperationId Block(RunFixtures f, TaskId task, AttemptId attempt)
    {
        var operation = f.Op();
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, operation, new RunEvent.Blocked(
            new(operation, task, attempt, MaterializationProblem.DirtyWorktree, null, [], "drift") { Scope = BlockScope.Checkout.Whole })));
        return operation;
    }

    [Fact]
    public void A_dependency_holds_its_consumer_and_a_context_connection_never_does()
    {
        using var f = new RunFixtures(Graph());
        f.Approve();
        var start = View(f);
        Assert.Equal([TaskState.Ready, TaskState.Pending, TaskState.Ready], new[] { T, U, C }.Select(task => start.Tasks[task].State));
        Assert.Equal([T], start.Tasks[U].HeldBy.ToArray());
        Assert.Equal(RunStatus.Running, start.Status);

        f.Complete(f.Reserve(T), report: "T ready.\n");
        var released = View(f);
        Assert.Equal([TaskState.Done, TaskState.Ready, TaskState.Ready], new[] { T, U, C }.Select(task => released.Tasks[task].State));
    }

    [Fact]
    public void A_waiting_task_names_the_predecessors_it_waits_for_itself_not_the_tasks_before_them()
    {
        // T → U → C: C waits for U, which waits for T.
        using var f = new RunFixtures(Connect(Connect(FixtureWorkflow(Task(), Task(U), Task(C)), T, U), U, C));
        f.Approve();

        var view = View(f);

        Assert.Equal([T], view.Tasks[U].HeldBy.ToArray());
        Assert.Equal([U], view.Tasks[C].HeldBy.ToArray());
        f.Complete(f.Reserve(T));
        Assert.Equal([U], View(f).Tasks[C].HeldBy.ToArray());
    }

    [Fact]
    public void A_node_run_projects_the_tasks_nobody_ran_and_those_it_can_no_longer_start()
    {
        // T → U and a root C, run from T: C is not part of the run, and nothing in it can start C.
        using var f = new RunFixtures(Connect(FixtureWorkflow(Task(), Task(U), Task(C)), T, U));
        f.Approve(node: T);

        var start = View(f);
        Assert.Equal([TaskState.Ready, TaskState.Pending, TaskState.Unrequested], new[] { T, U, C }.Select(task => start.Tasks[task].State));
        Assert.Equal([false, false, true], new[] { T, U, C }.Select(task => start.Tasks[task].Dormant));
        Assert.Equal(RunStatus.Running, start.Status);

        f.Complete(f.Reserve(T));
        f.Complete(f.Reserve(U));
        var done = View(f);
        Assert.Equal([TaskState.Done, TaskState.Done, TaskState.Unrequested], new[] { T, U, C }.Select(task => done.Tasks[task].State));
        // Nothing more can start, so the run is about to complete rather than needing attention.
        Assert.Equal(RunStatus.Running, done.Status);
    }

    [Fact]
    public void Each_durable_stage_of_an_attempt_projects_its_own_state()
    {
        using var f = new RunFixtures(Graph());
        f.Approve();
        var reservation = f.Reserve(T);
        Assert.Equal((TaskState.Ready, reservation.Attempt.Id), (View(f).Tasks[T].State, View(f).Tasks[T].Attempt));
        Assert.Equal(TaskState.Pending, View(f).Tasks[U].State);

        f.Claim(reservation);
        Assert.Equal(TaskState.Uncertain, View(f).Tasks[T].State);

        var checkpoint = f.WriteLog(reservation, TerminalAttemptOutcome.Failed);
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), reservation.Attempt.Id, TerminalAttemptOutcome.Failed, checkpoint));
        var failed = View(f);
        Assert.Equal(TaskState.Failed, failed.Tasks[T].State);
        Assert.Equal(TerminalAttemptOutcome.Failed, Assert.IsType<AttemptEnd.Logged>(failed.Tasks[T].End).Outcome);
        Assert.Equal(TaskState.Pending, failed.Tasks[U].State);
        Assert.Equal([T], failed.Tasks[U].HeldBy.ToArray());
        Assert.Equal(RunStatus.Running, failed.Status);
        Assert.Equal(RunStatus.Paused, View(f, resumed: false).Status);
        Assert.Equal(RunStatus.Elsewhere, View(f, controlled: false).Status);

        f.Complete(f.Reserve(C), report: "C ready.\n");
        var exhausted = View(f);
        Assert.Equal(RunStatus.NeedsAttention, exhausted.Status);
        Assert.Equal("Needs attention", exhausted.Label);
    }

    [Fact]
    public void A_block_holds_a_task_only_on_its_current_attempt_or_result()
    {
        using var f = new RunFixtures(Graph());
        f.Approve();
        var first = f.Reserve(T);
        Fail(f, first);
        Block(f, T, first.Attempt.Id);
        var blocked = View(f);
        Assert.Equal(TaskState.Blocked, blocked.Tasks[T].State);
        Assert.Equal("drift", blocked.Tasks[T].Block!.Detail);

        var retry = f.Reserve(T, new AttemptCause.Retry(first.Attempt.Id, f.Op()));
        f.Complete(retry, report: "T ready.\n");
        var replaced = View(f);
        Assert.Equal(TaskState.Done, replaced.Tasks[T].State);
        Assert.Equal(retry.Attempt.Id, replaced.Tasks[T].Attempt);
        Assert.Equal(TaskState.Ready, replaced.Tasks[U].State);

        var drift = Block(f, T, retry.Attempt.Id);
        var held = View(f);
        Assert.Equal(TaskState.Blocked, held.Tasks[T].State);
        Assert.Equal(drift, held.Tasks[T].Block!.Operation);
        Assert.Equal(TaskState.Pending, held.Tasks[U].State);
        Assert.Equal([T], held.Tasks[U].HeldBy.ToArray());
    }

    [Fact]
    public void A_resting_attempt_waits_unless_something_else_needs_attention()
    {
        using var f = new RunFixtures(Connect(FixtureWorkflow(Task(), Task(U) with { Conversation = ConversationMode.Chat }, Task(C)), T, C));
        f.Approve();
        var chat = f.Reserve(U);
        f.Claim(chat);
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseTurn(f.Permit, f.Op(), new(chat.Attempt.Id, 1), f.WriteLog(chat)));
        var waiting = View(f);
        Assert.Equal(TaskState.Waiting, waiting.Tasks[U].State);
        Assert.Equal(AttemptStatus.WaitingForInput, waiting.Tasks[U].Status);
        Fail(f, f.Reserve(T));
        Assert.Equal(RunStatus.NeedsAttention, View(f).Status);

        using var g = new RunFixtures(FixtureWorkflow(Task(U) with { Conversation = ConversationMode.Chat }));
        g.Approve();
        var only = g.Reserve(U);
        g.Claim(only);
        Assert.IsType<RunDecision.Recorded>(g.Store.CloseTurn(g.Permit, g.Op(), new(only.Attempt.Id, 1), g.WriteLog(only)));
        Assert.Equal(RunStatus.Waiting, View(g).Status);
        Assert.Equal("Waiting", View(g).Label);
    }

    [Fact]
    public void A_stale_result_is_not_done()
    {
        using var f = new RunFixtures(Connect(FixtureWorkflow(Task(), Task(U), Task(C) with { Conversation = ConversationMode.Chat }), T, U));
        f.Approve();
        var chat = f.Reserve(C);
        f.Claim(chat);
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseTurn(f.Permit, f.Op(), new(chat.Attempt.Id, 1), f.WriteLog(chat)));
        var first = f.Reserve(T);
        var old = f.Complete(first, report: "T ready.\n");
        f.Complete(f.Reserve(U), report: "U ready.\n");
        Assert.Equal(RunStatus.Waiting, View(f).Status);
        f.Complete(f.Reserve(T, new AttemptCause.Retry(first.Attempt.Id, f.Op())), report: "T again.\n", supersedes: old.Id);
        var stale = View(f);
        Assert.Equal(TaskState.Done, stale.Tasks[T].State);
        Assert.Equal(TaskState.Stale, stale.Tasks[U].State);
        Assert.Equal(f.Read().CurrentResults[U].Id, stale.Tasks[U].Result);
        Assert.Equal(RunStatus.NeedsAttention, stale.Status);
    }

    [Fact]
    public void A_newer_attempt_outranks_an_older_result()
    {
        using var f = new RunFixtures(Connect(FixtureWorkflow(Task(), Task(U)), T, U));
        f.Approve();
        var first = f.Reserve(T);
        var old = f.Complete(first, report: "T ready.\n");
        var retry = f.Reserve(T, new AttemptCause.Retry(first.Attempt.Id, f.Op()));
        var reserved = View(f);
        Assert.Equal((TaskState.Ready, retry.Attempt.Id, old.Id), (reserved.Tasks[T].State, reserved.Tasks[T].Attempt, reserved.Tasks[T].Result));
        Assert.Equal(TaskState.Pending, reserved.Tasks[U].State);

        f.Claim(retry);
        var claimed = View(f);
        Assert.Equal(TaskState.Uncertain, claimed.Tasks[T].State);
        Assert.Equal([T], claimed.Tasks[U].HeldBy.ToArray());

        var checkpoint = f.WriteLog(retry, TerminalAttemptOutcome.Failed);
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), retry.Attempt.Id, TerminalAttemptOutcome.Failed, checkpoint));
        Assert.Equal(TaskState.Failed, View(f).Tasks[T].State);
    }

    [Fact]
    public void This_window_s_holds_and_work_come_before_the_journal()
    {
        using var f = new RunFixtures(Graph());
        f.Approve();
        var busy = new TaskHold.Refused(new(RunProblem.TaskBusy), null, Transient: true);
        var refused = View(f, holds: new Dictionary<TaskId, TaskHold> { [T] = busy });
        Assert.Equal(TaskState.Refused, refused.Tasks[T].State);
        Assert.Equal(RunProblem.TaskBusy, refused.Tasks[T].Refusal!.Problem);
        Assert.Equal(RunStatus.Running, refused.Status);
        var permanent = View(f, holds: new Dictionary<TaskId, TaskHold>
        {
            [T] = busy with { Reason = new(RunProblem.TaskUnconfigured), Transient = false },
            [C] = busy with { Reason = new(RunProblem.TaskUnconfigured), Transient = false },
        });
        Assert.Equal(RunStatus.NeedsAttention, permanent.Status);

        var live = RunProjection.Of(new(f.Project, W, Run), f.Read(), _ => null,
            new Dictionary<TaskId, LiveStage> { [T] = LiveStage.Running, [C] = LiveStage.Settling }, NoHolds, true, true);
        Assert.Equal(TaskState.Running, live.Tasks[T].State);
        Assert.Equal(TaskState.Settling, live.Tasks[C].State);
        Assert.Equal(1, live.Slots);
    }

    [Fact]
    public void An_unresolved_turn_says_whether_its_root_exit_is_recorded()
    {
        using var f = new RunFixtures(Graph());
        f.Approve();
        var reservation = f.Reserve(T);
        f.Claim(reservation);
        var held = new Dictionary<TaskId, TaskHold> { [T] = new TaskHold.Unresolved(UnresolvedReason.IncompleteEvidence, new(RunProblem.EvidenceMismatch), Transient: false) };
        Assert.Equal((TaskState.Uncertain, false), (View(f, holds: held).Tasks[T].State, View(f, holds: held).Tasks[T].RootExited));

        var prepared = f.Read().Preparations[new(reservation.Attempt.Id, 1)];
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, f.Op(), new RunEvent.RootExitObserved(
            prepared.Launch, new RootExit.Exited(0), At, prepared.Location.AttemptBase, prepared.Location.Owner.Branch, TipOwnership.Explained)));

        var exited = View(f, holds: held).Tasks[T];
        Assert.Equal((TaskState.Uncertain, true, RunProblem.EvidenceMismatch), (exited.State, exited.RootExited, exited.Refusal!.Problem));
        // Without the hold, a turn whose root exited is still settling.
        Assert.Equal(TaskState.Settling, View(f).Tasks[T].State);
    }

    [Fact]
    public void A_report_result_carries_no_code_to_rebase()
    {
        using var f = new RunFixtures(Graph());
        f.Approve();
        f.Complete(f.Reserve(T), report: "T ready.\n");

        Assert.Equal((TaskState.Done, false), (View(f).Tasks[T].State, View(f).Tasks[T].CarriesCode));
    }
}
