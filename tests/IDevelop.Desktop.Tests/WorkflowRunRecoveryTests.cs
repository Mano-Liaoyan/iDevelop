using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using IDevelop.Desktop.Execution;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.AppTempFolder;
using static IDevelop.Desktop.Tests.WorkflowRunFixture;

namespace IDevelop.Desktop.Tests;

/// <summary>
/// A workflow run's recovery panels (E3g.2): a block that holds a task, a turn whose end a crash left unresolved, and Stop
/// in each phase of a task.
/// </summary>
[Collection(ProcessCollection.Name)]
public sealed class WorkflowRunRecoveryTests
{
    private static readonly TaskId A = TestTasks.Design;
    private static readonly TaskId B = TestTasks.Build;

    private static WorkflowRunFixture Chain() => new(Task(A, "A", 105), Task(B, "B", 405), Dependency(A, B));

    private static bool Shows(Shell shell, string automationId) => shell.Has<Control>(automationId) && shell.Find<Control>(automationId).IsEffectivelyVisible;

    [AvaloniaFact]
    public void A_blocked_join_says_how_the_run_ends_and_offers_no_closure()
    {
        var c = TestTasks.Review;
        var d = new TaskId(Guid.Parse("019a9d2e-5d10-7a00-8000-000000000044"));
        using var f = new WorkflowRunFixture(Task(A, "A", 105), Task(B, "B", 405), TaskAt(c, "C", 405, 300, WorkflowRunFixture.Codex, "Build C."),
            TaskAt(d, "D", 105, 300, WorkflowRunFixture.Codex, "Build D."), Dependency(A, B), Dependency(A, c), Dependency(B, d), Dependency(c, d));
        // B and C change one file each their own way, so D's inputs cannot join.
        f.Answer("A", f.Says("A ready.")).Answer("B", f.Writes("settings.txt", "B\n", "B ready.")).Answer("C", f.Writes("settings.txt", "C\n", "C ready."))
            .Answer("D", f.Says("D ready."));
        var shell = f.Window();
        shell.StartRun();
        shell.WaitForCard("D", "Conflict");
        shell.WaitForStatus("Needs attention");

        shell.Click(shell.Header(shell.Node("D")));

        Assert.StartsWith("The work it starts from conflicts in settings.txt, and no Merge node takes the conflict.", shell.Text("RunTaskDetail"));
        Assert.Equal(["The results it joins conflict."], shell.TextsOf("RecoveryBlockProblem"));
        Assert.Equal("Stop Workflow ends the run, and finished work stays.", shell.Text("RecoveryNoAction"));
        Assert.False(Shows(shell, "CloseAsStopped"));
        Assert.Equal(0, f.Launches("D"));
        // The section stays while another operation records a block on the same attempt, and changes with the attempt.
        var conflict = shell.Window.ViewModel.Canvas!.SelectedNode!.RunTask!.Block! with { Attempt = new AttemptId(Guid.NewGuid()) };
        using var panel = new RecoveryViewModel(shell.WorkflowRun!, new TaskView(d, TaskState.Blocked) { Block = conflict });
        Assert.NotNull(panel.NoActionNote);
        var view = new TaskView(d, TaskState.Blocked) { Block = conflict };
        Assert.True(panel.Shows(shell.WorkflowRun!, view with { Block = conflict with { Operation = new OperationId(Guid.NewGuid()) } }));
        Assert.False(panel.Shows(shell.WorkflowRun!, view with { Block = conflict with { Attempt = new AttemptId(Guid.NewGuid()) } }));
    }

    [AvaloniaFact]
    public void A_restart_after_a_crash_shows_the_unresolved_turn_and_Close_as_stopped_settles_it()
    {
        using var f = Chain();
        f.Answer("A", f.Writes("result.txt", "done\n", "A ready.")).Answer("B", f.Says("B ready."));
        f.Crash(A, "A", "journal.root-exit.before");

        var shell = f.Window();

        shell.WaitUntil(() => shell.WorkflowRun is not null, "the window shows the run");
        shell.WaitForCard("A", "Uncertain");
        Assert.Equal("Paused", shell.RunStatus);
        Assert.True(shell.InCard<Button>("A", "CardAttention").IsEffectivelyEnabled);
        shell.Click(shell.InCard<Button>("A", "CardAttention"));
        Assert.Equal("A", shell.Window.ViewModel.Canvas!.SelectedNode?.Title);
        Assert.Null(shell.Window.ViewModel.Conversation);
        Assert.Equal("Its turn's end was not recorded, so the run never starts it again by itself.", shell.Text("RunTaskDetail"));
        shell.WaitUntil(() => Shows(shell, "RecoveryClient"), "the section reads the evidence");
        Assert.EndsWith("It no longer runs.", shell.Text("RecoveryClient"));
        Assert.Equal("No exit was recorded.", shell.Text("RecoveryTurnEnd"));
        Assert.False(Shows(shell, "RecoveryStillRuns"));
        Assert.False(shell.InView<Button>("CloseAsStopped").IsEffectivelyEnabled);

        shell.Click(shell.InView<TextBox>("RecoveryReason"));
        shell.Type("The client was gone after the crash.");
        shell.Click(shell.InView<Button>("CloseAsStopped"));

        shell.WaitForCard("A", "Closed as stopped");
        Assert.Equal("The client was gone after the crash.", shell.Text("RunTaskDetail"));
        // A turn whose client exited but whose settlement failed cannot be closed; the section says what retries it.
        var unsettled = new TaskView(A, TaskState.Uncertain)
        {
            Attempt = f.Record().Attempts.Keys.Single(), RootExited = true, Unresolved = UnresolvedReason.IncompleteEvidence,
            Refusal = new(RunProblem.EvidenceMismatch),
        };
        using (var panel = new RecoveryViewModel(shell.WorkflowRun!, unsettled))
        {
            Assert.Equal((false, false), (panel.CanClose, panel.CloseCommand.CanExecute(null)));
            Assert.Equal("Its client exited, but iDevelop could not settle its turn. Its log does not match what the run recorded. Resume tries again.",
                panel.UnsettledNote);
        }
        Assert.False(Shows(shell, "CloseAsStopped"));
        var end = Assert.IsType<AttemptEnd.Recovered>(Assert.Single(f.Record().Closures.Values));
        Assert.Equal((RecoveryOutcome.Stopped, "The client was gone after the crash."), (end.Outcome, end.Reason));
        shell.Click(shell.Find<Button>("ResumeRun"));
        shell.WaitForStatus("Needs attention");
        shell.Click(shell.Find<Button>("StopWorkflow"));
        shell.WaitForStatus("Stopped");
        Assert.Equal((1, 0), (f.Launches("A"), f.Launches("B")));
        Assert.Empty(f.Record().Results);
    }

    [AvaloniaFact]
    public void Stop_after_a_restart_waits_for_the_unresolved_turn_until_the_person_closes_it()
    {
        using var f = Chain();
        f.Answer("A", f.Says("A ready.")).Answer("B", f.Says("B ready."));
        f.Crash(A, "A", "journal.root-exit.before");
        var shell = f.Window();
        shell.WaitUntil(() => shell.WorkflowRun is not null, "the window shows the run");
        shell.WaitForCard("A", "Uncertain");

        shell.Click(shell.Find<Button>("StopWorkflow"));

        shell.WaitForStatus("Stopping");
        Thread.Sleep(300);
        shell.Render();
        Assert.Equal(("Stopping", "Uncertain"), (shell.RunStatus, shell.CardText("A", "CardStatus")));
        Assert.Equal("Stopping waits for you to close\u00A0\"A\" as stopped.", shell.Text("RunActivity"));
        Assert.Equal(RunPhase.StopRequested, f.Record().Phase);
        // A task the stopping run never started has no conversation to talk to (#90).
        shell.Click(shell.Header(shell.Node("B")));
        Assert.Equal(WorkflowRunText.Unbroken("A run of the \"Workflow\" workflow is stopping and will not start this task."), shell.Text("RunOwner"));
        shell.Click(shell.InCard<Button>("A", "CardAttention"));
        shell.Click(shell.InView<TextBox>("RecoveryReason"));
        shell.Type("Nothing runs any more.");
        shell.Click(shell.InView<Button>("CloseAsStopped"));

        shell.WaitForStatus("Stopped");
        Assert.Equal(("Closed as stopped", "Waits for \"A\""), (shell.CardText("A", "CardStatus"), shell.CardText("B", "CardStatus")));
        Assert.Equal((1, 0), (f.Launches("A"), f.Launches("B")));
    }

    [AvaloniaFact]
    public void Stop_before_the_claim_starts_no_client()
    {
        using var f = Chain();
        f.Answer("A", f.Says("A ready.")).Answer("B", f.Says("B ready."));
        using var claim = new ManualResetEventSlim();
        using var reached = new ManualResetEventSlim();
        f.Probe = point =>
        {
            if (point == "runner.claim.before" && !reached.IsSet)
            {
                reached.Set();
                claim.Wait(TimeSpan.FromSeconds(30));
            }
        };
        var shell = f.Window();
        shell.StartRun();
        shell.WaitUntil(() => reached.IsSet, "A's start reaches its claim");

        shell.Click(shell.Find<Button>("StopWorkflow"));
        shell.WaitForStatus("Stopping");
        claim.Set();

        shell.WaitForStatus("Stopped");
        Assert.Equal(("Not started", "Waits for \"A\""), (shell.CardText("A", "CardStatus"), shell.CardText("B", "CardStatus")));
        Assert.Equal((0, 0), (f.Launches("A"), f.Launches("B")));
    }

    [AvaloniaFact]
    public void Stop_while_a_turn_settles_lets_its_success_publish_and_starts_nothing_after_it()
    {
        using var f = Chain();
        f.Answer("A", f.Writes("result.txt", "done\n", "A ready.")).Answer("B", f.Says("B ready."));
        using var cleanup = new ManualResetEventSlim();
        using var reached = new ManualResetEventSlim();
        f.Probe = point =>
        {
            if (point == "runner.cleanup.inside" && !reached.IsSet)
            {
                reached.Set();
                cleanup.Wait(TimeSpan.FromSeconds(30));
            }
        };
        var shell = f.Window();
        shell.StartRun();
        shell.WaitUntil(() => reached.IsSet, "A's turn settles");

        shell.Click(shell.Find<Button>("StopWorkflow"));
        shell.WaitForStatus("Stopping");
        cleanup.Set();

        shell.WaitForStatus("Stopped");
        Assert.Equal(("Succeeded", "Not started"), (shell.CardText("A", "CardStatus"), shell.CardText("B", "CardStatus")));
        Assert.Single(f.Record().Results);
        Assert.Equal((1, 0), (f.Launches("A"), f.Launches("B")));
    }

    [AvaloniaFact]
    public void Stop_while_a_task_waits_for_a_reply_closes_it_without_its_queued_text()
    {
        using var f = new WorkflowRunFixture(Task(A, "A", 105, ConversationMode.Chat), Task(B, "B", 405), Dependency(A, B));
        f.Answer("A", f.Says("Which file?")).Answer("B", f.Says("B ready."));
        var shell = f.Window();
        shell.StartRun();
        shell.WaitForCard("A", "Waiting for you");

        shell.Click(shell.Find<Button>("StopWorkflow"));

        shell.WaitForStatus("Stopped");
        Assert.Equal(("Cancelled", "Waits for \"A\""), (shell.CardText("A", "CardStatus"), shell.CardText("B", "CardStatus")));
        Assert.Equal((1, 0), (f.Launches("A"), f.Launches("B")));
        Assert.False(shell.Has<Button>("NextWaiting") && shell.Find<Button>("NextWaiting").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Stop_while_an_approval_waits_closes_its_request()
    {
        var gate = TestTasks.Review;
        using var f = new WorkflowRunFixture(Task(A, "A", 105), Approval(gate, "Approve", 405), Dependency(A, gate));
        f.Answer("A", f.Says("A ready."));
        var shell = f.Window();
        shell.StartRun();
        shell.WaitForCard("Approve", "Waiting for approval");

        shell.Click(shell.Find<Button>("StopWorkflow"));

        shell.WaitForStatus("Stopped");
        Assert.Equal(("Succeeded", "Closed"), (shell.CardText("A", "CardStatus"), shell.CardText("Approve", "CardStatus")));
        shell.Click(shell.Header(shell.Node("Approve")));
        Assert.False(Shows(shell, "ApproveGate"));
    }

}
