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
}
