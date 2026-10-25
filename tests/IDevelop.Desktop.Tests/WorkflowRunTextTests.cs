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
    public void Every_reason_the_run_refuses_has_its_own_sentence()
    {
        Assert.All(Enum.GetValues<RunProblem>(), problem =>
            Assert.NotEqual("The run could not do this now.", WorkflowRunText.Problem(new RunRejection(problem))));
    }

    [Fact]
    public void A_refused_task_whose_attempt_ended_says_its_agent_finished_and_the_run_refused_its_result()
    {
        var view = new TaskView(Task, TaskState.Refused)
        {
            Attempt = new AttemptId(Guid.NewGuid()), Refusal = new RunRejection(RunProblem.OutcomeMismatch),
            End = new AttemptEnd.Logged(TerminalAttemptOutcome.Succeeded, new LogCheckpoint(1, new Digest(new string('0', 64)))),
        };

        Assert.Equal((NodeState.Failed, "Not accepted"), WorkflowRunText.Of(view, Title));
        Assert.Equal("Its agent finished, but the run could not accept its result: how its attempt ended does not match what the run " +
            "recorded. Retry runs it again.", WorkflowRunText.Detail(view, Title));
        Assert.Equal("Its agent finished, but the run could not accept its result: how its attempt ended does not match what the run " +
            "recorded.", WorkflowRunText.Detail(view, Title, active: false));
        Assert.Equal((NodeState.Failed, "Couldn't start"), WorkflowRunText.Of(view with { End = null }, Title));
    }

    [Fact]
    public void A_run_s_Pi_task_whose_folder_moved_points_to_Retry()
    {
        var view = new TaskView(Task, TaskState.Refused) { Problem = new StartProblem.SessionFolderMoved(ClientId.Pi) };

        Assert.Equal("Pi can't continue this conversation after the project folder moved, so it needs a new conversation. Retry starts a new attempt.",
            WorkflowRunText.Detail(view, Title));
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
        Assert.Equal(new Attention.Problem("Inputs changed: A task before it handed on a newer result after this one finished. Stop Workflow ends the run, and finished work stays."), WorkflowRunText.Needs(stale, Title));
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
        // The pill counts only the tasks whose client runs, and names what goes on when none does.
        Assert.Equal("2 running", WorkflowRunText.Status(Run(RunStatus.Running, mixed)));
        Assert.Equal("Running", WorkflowRunText.Status(Run(RunStatus.Running, [.. mixed.Skip(1)])));
        Assert.Equal("Starting", WorkflowRunText.Status(Run(RunStatus.Running, one)));
        Assert.Equal("Finishing", WorkflowRunText.Status(Run(RunStatus.Running, [mixed[1]])));
        Assert.Equal("Running", WorkflowRunText.Status(Run(RunStatus.Running, [new TaskView(Task, TaskState.Ready)])));
        Assert.Equal("Stopping", WorkflowRunText.Status(Run(RunStatus.Stopping, mixed)));
    }

    [Fact]
    public void A_line_of_titles_wraps_only_between_titles()
    {
        const string nb = "\u00A0";
        Assert.Equal($"Running{nb}\"Parse{nb}input\", \"Render{nb}view\", and{nb}\"UI\u2011kit\"{nb}· Starting{nb}\"Docs/\u2060API\"",
            WorkflowRunText.Unbroken("Running \"Parse input\", \"Render view\", and \"UI-kit\" · Starting \"Docs/API\""));
        Assert.Equal($"\"Long{nb}title\" waits for you.", WorkflowRunText.Unbroken("\"Long title\" waits for you."));
        Assert.Equal("Stopped. Finished work stays.", WorkflowRunText.Unbroken("Stopped. Finished work stays."));
        Assert.Null(WorkflowRunText.Unbroken(null));
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

    [Fact]
    public void A_task_nobody_ran_shows_as_not_started_and_a_task_waiting_for_it_says_who_runs_it()
    {
        var unrun = new TaskView(Task, TaskState.Unrequested) { Dormant = true };
        var join = new TaskView(TestTasks.Review, TaskState.Pending) { HeldBy = [TestTasks.Design, Task] };
        var tasks = new Dictionary<TaskId, TaskView> { [TestTasks.Design] = new(TestTasks.Design, TaskState.Running), [Task] = unrun, [TestTasks.Review] = join };

        Assert.Equal((NodeState.Idle, "Not started"), WorkflowRunText.Of(unrun, Title));
        Assert.Equal((NodeState.Idle, "Not started"), WorkflowRunText.Of(unrun, Title, active: false));
        Assert.Null(WorkflowRunText.Detail(unrun, Title));
        Assert.Null(WorkflowRunText.Needs(unrun, Title));
        Assert.Equal("It starts once \"A\" and \"B\" hand on a result. \"B\" runs only when you run\u00A0it.", WorkflowRunText.Detail(join, Title, tasks: tasks));
        Assert.Equal("It starts once \"A\" hands on a result.",
            WorkflowRunText.Detail(join with { HeldBy = [TestTasks.Design] }, Title, tasks: tasks));
    }

    [Fact]
    public void A_node_whose_predecessors_have_no_results_says_it_runs_after_them()
    {
        string Named(TaskId task) => task == TestTasks.Design ? "A" : task == Task ? "B" : "C";

        Assert.Null(WorkflowRunText.RunsAfter([], Named));
        Assert.Equal("Runs after \"A\". Run \"A\"\u00A0first.", WorkflowRunText.RunsAfter([TestTasks.Design], Named));
        Assert.Equal("Runs after \"A\" and \"B\". Run them\u00A0first.", WorkflowRunText.RunsAfter([TestTasks.Design, Task], Named));
        Assert.Equal("Runs after \"A\" and 2 more. Run them\u00A0first.", WorkflowRunText.RunsAfter([TestTasks.Design, Task, TestTasks.Review], Named));
        // Titles read in title order, whatever order the tasks come in.
        Assert.Equal("Runs after \"A\" and \"B\". Run them\u00A0first.", WorkflowRunText.RunsAfter([Task, TestTasks.Design], Named));
    }

    [Fact]
    public void A_node_run_counts_the_tasks_it_reaches_and_names_what_they_still_wait_for()
    {
        string Named(TaskId task) => task == TestTasks.Design ? "Parse input" : task == Task ? "Render view" : "Ship";
        RunView Completed(params TaskView[] tasks) => new(new("/project", new WorkflowId(Guid.NewGuid()), new RunId(Guid.NewGuid())),
            RunPhase.Completed, RunStatus.Completed, true, true, 0, tasks.ToImmutableSortedDictionary(task => task.Task, task => task));
        var parse = new TaskView(TestTasks.Design, TaskState.Done);
        var render = new TaskView(Task, TaskState.Unrequested) { Dormant = true };
        var ship = new TaskView(TestTasks.Review, TaskState.Pending) { HeldBy = [Task], Dormant = true };

        // Render view is not part of the run, so the count covers Parse input and Ship.
        Assert.Equal("1 of 2 done", WorkflowRunText.Progress(Completed(parse, render, ship)));
        Assert.Equal("\"Ship\" waits for \"Render view\".", WorkflowRunText.StillWaiting([parse, render, ship], Named));
        Assert.Null(WorkflowRunText.StillWaiting([parse, render], Named));
        // Run Workflow reaches every task.
        Assert.Equal("1 of 3 done", WorkflowRunText.Progress(Completed(parse, render with { State = TaskState.Failed }, ship)));
        Assert.Null(WorkflowRunText.Progress(Completed(render)));
    }

    private static readonly CommitId Commit = new(new string('a', 40));

    private static MergeConflict Conflict(MergeTarget target) => new(target, Commit, Commit, Commit, ["settings.txt"], "## The conflict");

    private static TaskView Conflicting(TaskId? mergedBy)
    {
        var evidence = new ConflictEvidence([], 1, ["settings.txt"], [], [], new("a", new(new string('0', 64)), 0), new("b", new(new string('0', 64)), 0),
            "2.47.0", [], Commit) { Merge = Conflict(new MergeTarget.Join(Task)) };
        return new TaskView(Task, TaskState.Blocked)
        {
            Block = new(new(Guid.NewGuid()), Task, null, MaterializationProblem.FanInConflict, null, [], "Merge step 1 of 1 conflicts in settings.txt.", evidence)
            { Scope = new BlockScope.Operation() },
            MergedBy = mergedBy,
        };
    }

    [Fact]
    public void A_conflict_between_predecessors_names_the_merge_node_that_merges_it_or_asks_the_person()
    {
        var merged = Conflicting(TestTasks.Design);
        Assert.Equal((NodeState.Idle, "Waits for \"A\""), WorkflowRunText.Of(merged, Title));
        Assert.Equal("The work it starts from conflicts in settings.txt. \"A\" merges it, and it starts once the merge is committed.",
            WorkflowRunText.Detail(merged, Title));
        Assert.Null(WorkflowRunText.Needs(merged, Title));

        var asks = Conflicting(null);
        Assert.Equal((NodeState.Failed, "Conflict"), WorkflowRunText.Of(asks, Title));
        Assert.Equal("The work it starts from conflicts in settings.txt, and no Merge node takes the conflict. " +
            "Open the conflict's worktree, fix the files, and mark it resolved, or add a Merge node and run\u00A0it.",
            WorkflowRunText.Detail(asks, Title));
        Assert.IsType<Attention.Problem>(WorkflowRunText.Needs(asks, Title));
    }

    [Fact]
    public void A_merge_nodes_agent_merges_and_a_merge_it_left_unfinished_asks_for_a_retry()
    {
        var conflict = Conflict(new MergeTarget.Join(TestTasks.Design));
        Assert.Equal((NodeState.Running, "Merging"), WorkflowRunText.Of(new TaskView(Task, TaskState.Running) { Merge = conflict }, Title));

        var markers = new TaskView(Task, TaskState.Failed)
        {
            Merge = conflict, End = new AttemptEnd.Logged(TerminalAttemptOutcome.Succeeded, new(1, new(new string('0', 64)))),
        };
        Assert.Equal((NodeState.Failed, "Not merged"), WorkflowRunText.Of(markers, Title));
        Assert.Equal("Its agent left conflict markers in settings.txt, so the merge was not committed. Retry it.", WorkflowRunText.Detail(markers, Title));
    }

    [Fact]
    public void The_final_merge_says_what_it_waits_for()
    {
        Assert.Equal("The run's work waits to merge into main. Commit or stash your changes to root.txt, and it merges. iDevelop never touches them.",
            WorkflowRunText.Merging(new BranchMergeWait.Uncommitted("refs/heads/main", "/project", ["root.txt"]), Title));
        Assert.Equal("The run's work conflicts with main in a.txt and b.txt. Open the conflict's worktree, fix the files, and mark it resolved, " +
            "or add a Merge node and run\u00A0it.",
            WorkflowRunText.Merging(new BranchMergeWait.Conflict("refs/heads/main", ["a.txt", "b.txt"], null), Title));
        Assert.Equal("\"A\" merges the run's work into main, which conflicts in a.txt.",
            WorkflowRunText.Merging(new BranchMergeWait.Conflict("refs/heads/main", ["a.txt"], TestTasks.Design), Title));
    }

    [Fact]
    public void The_persons_steps_on_a_conflict_say_what_came_of_them()
    {
        Assert.Equal("The conflict's worktree is open at /w. Fix a.txt there, then choose Mark resolved.",
            WorkflowRunText.ConflictOpened("/w", ["a.txt"], shown: true));
        Assert.Equal("The conflict's worktree is at /w, and iDevelop could not open it. Open it yourself, fix a.txt and b.txt, then choose Mark resolved.",
            WorkflowRunText.ConflictOpened("/w", ["a.txt", "b.txt"], shown: false));
        // Mark resolved is refused, with the reason, while the markers remain.
        Assert.Equal("a.txt still has conflict markers. Fix it, then mark the conflict resolved.",
            WorkflowRunText.Conflict(new ConflictReply.MarkersRemain(["a.txt"])));
        Assert.Equal("a.txt and b.txt still have conflict markers. Fix them, then mark the conflict resolved.",
            WorkflowRunText.Conflict(new ConflictReply.MarkersRemain(["a.txt", "b.txt"])));
        Assert.Equal("Open the conflict's worktree first, and fix the files there.", WorkflowRunText.Conflict(new ConflictReply.Unopened()));
        Assert.Equal("Your merge is committed, and the run goes on from it.", WorkflowRunText.Conflict(new ConflictReply.Resolved(Commit)));
        Assert.Equal("The conflict no longer waits for you.", WorkflowRunText.Conflict(new ConflictReply.Refused(new(RunProblem.StaleInput))));
    }

    [Fact]
    public void Conflicting_files_without_markers_are_named_wherever_the_person_resolves_the_conflict()
    {
        var conflict = Conflict(new MergeTarget.Join(Task)) with { Paths = ["logo.bin", "settings.txt"], Unmarked = ["logo.bin"] };
        Assert.Equal("settings.txt still has conflict markers. Fix it, then mark the conflict resolved. Check logo.bin too: its conflict leaves no markers.",
            WorkflowRunText.Conflict(new ConflictReply.MarkersRemain(["settings.txt"]), conflict));
        Assert.Equal("The conflict's worktree is open at /w. Fix logo.bin and settings.txt there, then choose Mark resolved. " +
            "Check logo.bin too: its conflict leaves no markers.", WorkflowRunText.ConflictOpened("/w", conflict.Paths, shown: true, conflict));
        Assert.EndsWith("Check logo.bin too: its conflict leaves no markers.",
            WorkflowRunText.Merging(new BranchMergeWait.Conflict("refs/heads/main", conflict.Paths, null) { Merge = conflict }, Title));

        var evidence = new ConflictEvidence([], 1, conflict.Paths, [], [], new("a", new(new string('0', 64)), 0), new("b", new(new string('0', 64)), 0),
            "2.47.0", [], Commit) { Merge = conflict };
        var blocked = new TaskView(Task, TaskState.Blocked)
        {
            Block = new(new(Guid.NewGuid()), Task, null, MaterializationProblem.FanInConflict, null, [], "Merge step 1 of 1 conflicts.", evidence)
            { Scope = new BlockScope.Operation() },
        };
        Assert.EndsWith("Check logo.bin too: its conflict leaves no markers.", WorkflowRunText.Detail(blocked, Title));
    }

    [Fact]
    public void The_final_merge_waits_for_a_rebase_and_a_merge_node_that_takes_no_conflict_says_so()
    {
        Assert.Equal("The run's work waits to merge into main. main is being rebased in /project; the run's work merges once it finishes. " +
            "iDevelop never touches it meanwhile.", WorkflowRunText.Merging(new BranchMergeWait.Busy("refs/heads/main", "/project", "rebased"), Title));
        Assert.Equal("\"A\" joined the run, but it does not take the conflict: \"A\" and \"B\", whose work conflicts, don't all connect into it. " +
            "A Merge node takes the conflict once each of them connects into it.",
            WorkflowRunText.Untaken(TestTasks.Design, [TestTasks.Design, TestTasks.Build], Title));
    }
}
