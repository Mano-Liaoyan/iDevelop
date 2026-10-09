using System.Collections.Immutable;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Conversation;
using IDevelop.Desktop.Execution;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Tests;

/// <summary>What a run's task says on its card and in the inspector, for the states recovery and reviews add (E3g.2).</summary>
public sealed class WorkflowRunTextTests
{
    private static readonly TaskId Task = TestTasks.Build;

    private static string Title(TaskId task) => task == TestTasks.Design ? "A" : "B";

    [Fact]
    public void A_review_without_a_task_to_review_says_so()
    {
        var view = new TaskView(Task, TaskState.Unsupported);

        Assert.Equal((NodeState.Idle, "Nothing to review"), WorkflowRunText.Of(view, Title));
        Assert.Equal("Connect the task to review into this review with a dependency first.", WorkflowRunText.Detail(view, Title));
    }

    [Fact]
    public void A_closure_by_recovery_says_whether_the_task_started_and_why()
    {
        var stopped = new TaskView(Task, TaskState.Failed) { End = new AttemptEnd.Recovered(RecoveryOutcome.Stopped, new(Guid.NewGuid()), "Gone after the crash.") };
        var unstarted = new TaskView(Task, TaskState.Failed) { End = new AttemptEnd.Recovered(RecoveryOutcome.NotStarted, new(Guid.NewGuid()), WorkflowRunCoordinator.StoppedReason) };

        Assert.Equal((NodeState.Interrupted, "Closed as stopped"), WorkflowRunText.Of(stopped, Title));
        Assert.Equal("Gone after the crash.", WorkflowRunText.Detail(stopped, Title));
        Assert.Equal((NodeState.Cancelled, "Not started"), WorkflowRunText.Of(unstarted, Title));
        Assert.Equal("The workflow was stopped before this task started.", WorkflowRunText.Detail(unstarted, Title));
    }

    [Fact]
    public void A_task_held_by_another_task_s_block_names_that_task()
    {
        MaterializationBlock Block(TaskId owner) => new(new(Guid.NewGuid()), owner, new(Guid.NewGuid()), MaterializationProblem.DirtyWorktree, null, [],
            "The checkout changed after its result was accepted.") { Scope = new BlockScope.Checkout(["result.txt"]) };

        Assert.Equal("The checkout of \"A\" holds it. The checkout changed after its result was accepted.",
            WorkflowRunText.Detail(new TaskView(Task, TaskState.Blocked) { Block = Block(TestTasks.Design) }, Title));
        Assert.Equal("Files changed in the task's checkout after its turn ended. The checkout changed after its result was accepted.",
            WorkflowRunText.Detail(new TaskView(Task, TaskState.Blocked) { Block = Block(Task) }, Title));
    }

    [Fact]
    public void An_interrupted_fix_and_a_stale_result_ask_for_the_person()
    {
        var fix = new TaskView(Task, TaskState.Waiting) { Status = AttemptStatus.InReview, Fix = new FixRecovery(new(Guid.NewGuid()), 1, null) };
        var stale = new TaskView(Task, TaskState.Stale);

        Assert.Equal((NodeState.Waiting, "Fix interrupted"), WorkflowRunText.Of(fix, Title));
        Assert.Equal(new Attention.Waiting("Fix interrupted"), WorkflowRunText.Needs(fix, Title));
        Assert.Equal(new Attention.Problem("Inputs changed: A task before it handed on a newer result after this one finished."), WorkflowRunText.Needs(stale, Title));
        Assert.Null(WorkflowRunText.Needs(new TaskView(Task, TaskState.Waiting) { Status = AttemptStatus.InReview }, Title));
    }

    [Fact]
    public void A_fix_round_says_how_it_ended_and_why_it_cannot_continue()
    {
        var fix = new FixRecovery(new(Guid.NewGuid()), 2, null);

        Assert.Equal("Closing iDevelop interrupted fix round 2 of \"A\". Continue the fix in its session, or retry it in a fresh one.",
            WorkflowRunText.FixChoice(fix, "A", new AttemptEnd.Logged(TerminalAttemptOutcome.Interrupted, new(0, new(new string('0', 64))))));
        Assert.Equal("Fix round 2 of \"A\" ended without a recorded end, and it was closed as stopped. Continue the fix in its session, or retry it in a fresh one.",
            WorkflowRunText.FixChoice(fix, "A", new AttemptEnd.Recovered(RecoveryOutcome.Stopped, new(Guid.NewGuid()), "Gone.")));
        Assert.Equal("Closing iDevelop interrupted fix round 2 of \"A\". The fix's client never started, so its session cannot go on. Retry fix starts the round in a fresh session.",
            WorkflowRunText.FixChoice(fix with { ContinueUnavailable = RunReviews.NotStartedFix }, "A", null));
    }

    [Fact]
    public void A_stopping_run_says_whether_it_waits_for_a_closure_or_a_settlement()
    {
        Assert.Equal("Stopping. Finished work stays.", WorkflowRunText.Stopping([new TaskView(Task, TaskState.Done)], Title));
        Assert.Equal("Stopping waits for you to close \"B\" as stopped.", WorkflowRunText.Stopping([new TaskView(Task, TaskState.Uncertain)], Title));
        Assert.Equal("Stopping waits for \"B\" to settle. Open the project again to retry.",
            WorkflowRunText.Stopping([new TaskView(Task, TaskState.Uncertain) { RootExited = true }], Title));
    }

    [Fact]
    public void Every_path_ref_and_update_shows()
    {
        string[] paths = ["a.txt", "b.txt", "c.txt", "d.txt", "e.txt", "f.txt"];
        var block = new MaterializationBlock(new(Guid.NewGuid()), Task, null, MaterializationProblem.DirtyWorktree, null, [], "drift")
            { Scope = new BlockScope.Checkout([.. paths], Branch: true) };

        Assert.Equal(string.Join("\n", paths) + "\nthe task's branch", RecoveryText.Paths(block));
        Assert.Equal(string.Join("\n", paths), RecoveryText.CandidatePaths(new RebaseCandidate.Clean(new(new string('1', 40)), new(new string('2', 40)), [.. paths])));
        Assert.Equal(string.Join("\n", paths), RecoveryText.Refs(block with { Scope = new BlockScope.Refs([.. paths]) }));
    }

    [Fact]
    public void Several_busy_tasks_show_by_what_they_do_and_the_pill_counts_them()
    {
        var titles = new Dictionary<TaskId, string>();
        TaskView Busy(string title, TaskState state)
        {
            var task = new TaskId(Guid.NewGuid());
            titles[task] = title;
            return new TaskView(task, state);
        }

        string Titled(TaskId task) => titles[task];
        var one = new[] { Busy("Solo", TaskState.Starting) };
        var mixed = new[] { Busy("Parse", TaskState.Running), Busy("Docs", TaskState.Settling), Busy("API", TaskState.Running), Busy("UI", TaskState.Starting) };
        var many = new[] { "Delta", "Alpha", "Echo", "Bravo", "Charlie" }.Select(title => Busy(title, TaskState.Running)).ToArray();

        Assert.Equal("Starting \"Solo\"", WorkflowRunText.Working(one, Titled));
        Assert.Equal("Running \"API\" and \"Parse\" · Starting \"UI\" · Finishing \"Docs\"", WorkflowRunText.Working(mixed, Titled));
        Assert.Equal("Running \"Alpha\", \"Bravo\", and 3 more", WorkflowRunText.Working(many, Titled));
        Assert.Null(WorkflowRunText.Working([new TaskView(Task, TaskState.Waiting)], Title));

        RunView Run(RunStatus status, IEnumerable<TaskView> tasks) => new(new("/project", new WorkflowId(Guid.NewGuid()), new RunId(Guid.NewGuid())),
            status == RunStatus.Stopping ? RunPhase.StopRequested : RunPhase.Approved, status, true, true, 0,
            tasks.ToImmutableSortedDictionary(task => task.Task, task => task));
        Assert.Equal("Running", WorkflowRunText.Status(Run(RunStatus.Running, one)));
        Assert.Equal("4 running", WorkflowRunText.Status(Run(RunStatus.Running, mixed)));
        Assert.Equal("Stopping", WorkflowRunText.Status(Run(RunStatus.Stopping, mixed)));
    }

    [Fact]
    public void A_task_that_waits_for_several_counts_them_on_its_card_and_names_them_in_its_detail()
    {
        var one = new TaskView(Task, TaskState.Pending) { HeldBy = [TestTasks.Design] };
        var two = new TaskView(TestTasks.Review, TaskState.Pending) { HeldBy = [TestTasks.Design, Task] };

        Assert.Equal((NodeState.Idle, "Waits for \"A\""), WorkflowRunText.Of(one, Title));
        Assert.Equal((NodeState.Idle, "Waits for 2 tasks"), WorkflowRunText.Of(two, Title));
        Assert.Equal("It starts once \"A\" and \"B\" hand on a result.", WorkflowRunText.Detail(two, Title));
    }
}
