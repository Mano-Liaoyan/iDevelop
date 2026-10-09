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
/// A workflow run's recovery panels (E3g.2): a drifted checkout's Preserve and restore, a turn whose end a crash left
/// unresolved, and Stop in each phase of a task.
/// </summary>
[Collection(ProcessCollection.Name)]
public sealed class WorkflowRunRecoveryTests
{
    private static readonly TaskId A = TestTasks.Design;
    private static readonly TaskId B = TestTasks.Build;

    private static WorkflowRunFixture Chain() => new(Task(A, "A", 105), Task(B, "B", 405), Dependency(A, B));

    /// <summary>A writes <c>result.txt="done\n"</c>, and its checkout drifts to <c>"late\n"</c> before B's first claim.</summary>
    private static WorkflowRunFixture Drifting()
    {
        var f = Chain();
        f.Answer("A", f.Writes("result.txt", "done\n", "A ready.")).Answer("B", f.Says("B ready."));
        var drifted = 0;
        f.Probe = point =>
        {
            if (point == "runner.claim.before" && !f.Record().Results.IsEmpty && Interlocked.Exchange(ref drifted, 1) == 0)
            {
                File.WriteAllText(Path.Combine(f.Checkout(A), "result.txt"), "late\n");
            }
        };
        return f;
    }

    private static void ShowBlocked(Shell shell)
    {
        shell.StartRun();
        shell.WaitForStatus("Needs attention");
        shell.WaitForCard("A", "Blocked");
        shell.WaitForCard("B", "Blocked");
    }

    private static bool Shows(Shell shell, string automationId) => shell.Has<Control>(automationId) && shell.Find<Control>(automationId).IsEffectivelyVisible;

    [AvaloniaFact]
    public void A_producer_s_drifted_checkout_holds_its_consumer_until_Preserve_and_restore_put_it_back()
    {
        using var f = Drifting();
        var shell = f.Window();
        ShowBlocked(shell);

        shell.Click(shell.Header(shell.Node("B")));
        Assert.Equal("The block is on the checkout of \"A\". Restore it there.", shell.Text("RecoveryOwner"));
        Assert.False(Shows(shell, "PreserveCheckout"));
        shell.Click(shell.InView<Button>("RecoveryShowOwner"));
        Assert.Equal("A", shell.Window.ViewModel.Canvas!.SelectedNode?.Title);

        shell.WaitUntil(() => Shows(shell, "RecoveryCaptures"), "the section reads the evidence");
        Assert.Equal("result.txt", shell.Find<TextBox>("RecoveryPaths").Text);
        Assert.Equal(["Files changed in the task's checkout after its turn ended."], shell.TextsOf("RecoveryBlockProblem"));
        Assert.StartsWith("Initial attempt · ", shell.Text("RecoveryAttempt"));
        var second = f.Window();
        second.WaitUntil(() => second.WorkflowRun is not null, "the second window shows the run");
        second.Click(second.Header(second.Node("A")));
        Assert.False(second.InView<Button>("PreserveCheckout").IsEffectivelyEnabled);
        Assert.Equal("The client exited with code 0.", shell.Text("RecoveryTurnEnd"));
        Assert.Equal("2 captures. They match.", shell.Text("RecoveryCaptures"));
        Assert.Equal("Completed.", shell.Text("RecoveryCleanup"));
        Assert.StartsWith("Process ", shell.Text("RecoveryClient"));
        Assert.False(Shows(shell, "RecoveryHandRepair"));
        Assert.False(Shows(shell, "CloseAsStopped"));

        shell.Click(shell.InView<Button>("PreserveCheckout"));
        shell.WaitUntil(() => Shows(shell, "RestoreCheckout"), "the restoration is previewed", () => $"Notice: {Notice(shell)}");
        Assert.Equal("late\n", File.ReadAllText(Path.Combine(f.Checkout(A), "result.txt")));
        Assert.Equal("Restore puts the checkout back to the result it handed on.", shell.Text("RestoreTarget"));
        Assert.Equal(["result.txt gets its recorded content back"], shell.TextsOf("RestoreMove"));
        // The preservation found the checkout off its baseline and recorded that too, so the section lists both blocks.
        shell.WaitUntil(() => shell.TextsOf("RecoveryBlockDetail").Length == 2, "the section lists every block",
            () => $"Blocks: [{string.Join("|", shell.TextsOf("RecoveryBlockDetail"))}]");
        Assert.Equal("The checkout differs from its recorded baseline.", shell.TextsOf("RecoveryBlockDetail")[1]);
        Assert.StartsWith("What the checkout holds now stays retained under refs/idp/", shell.Text("RestoreRetained"));
        Assert.False(Shows(shell, "PreserveCheckout"));
        Assert.Equal(0, f.Launches("B"));

        shell.Click(shell.InView<Button>("RestoreCheckout"));

        shell.WaitForStatus("Completed");
        Assert.Equal(["Succeeded", "Succeeded"], new[] { "A", "B" }.Select(title => shell.CardText(title, "CardStatus")));
        Assert.Equal("done\n", File.ReadAllText(Path.Combine(f.Checkout(A), "result.txt")));
        var record = f.Record();
        var preserved = Assert.Single(record.Preservations.Values);
        Assert.Equal("late\n", f.Run("show", preserved.Ref + ":result.txt"));
        Assert.Single(record.Results, result => result.Task == A);
        Assert.DoesNotContain(record.Blocks.Values, block => !block.Resolved);
        Assert.Equal((1, 1), (f.Launches("A"), f.Launches("B")));
        Assert.False(Shows(shell, "RestoreCheckout"));
    }

    [AvaloniaFact]
    public void A_checkout_that_keeps_changing_while_it_is_preserved_keeps_its_block_and_moves_nothing()
    {
        using var f = Drifting();
        var drifting = f.Probe!;
        f.Probe = point =>
        {
            drifting(point);
            if (point == "git.preserve-observe-1.after")
            {
                File.WriteAllText(Path.Combine(f.Checkout(A), "result.txt"), "changing\n");
            }
        };
        var shell = f.Window();
        ShowBlocked(shell);
        shell.Click(shell.Header(shell.Node("A")));

        shell.Click(shell.InView<Button>("PreserveCheckout"));

        shell.WaitUntil(() => Shows(shell, "RecoveryNotice"), "the preservation is refused");
        Assert.Equal("The checkout changed between preservation observations. Nothing moved.", Notice(shell));
        Assert.False(Shows(shell, "RestoreCheckout"));
        Assert.True(shell.Find<Button>("PreserveCheckout").IsEffectivelyEnabled);
        Assert.Equal("changing\n", File.ReadAllText(Path.Combine(f.Checkout(A), "result.txt")));
        Assert.Empty(f.Record().Restorations);
        Assert.Equal(("Blocked", 0), (shell.CardText("A", "CardStatus"), f.Launches("B")));
    }

    [AvaloniaFact]
    public void On_macOS_the_section_says_to_change_files_by_hand_and_a_new_preservation_then_clears_the_block()
    {
        var handRepair = RecoveryViewModel.HandRepair;
        RecoveryViewModel.HandRepair = true;
        try
        {
            using var f = Drifting();
            // macOS reads no file identity, so Restore cannot prove that the checkout and its Git folder share a file system.
            f.Volumes = _ => null;
            var shell = f.Window();
            ShowBlocked(shell);
            shell.Click(shell.Header(shell.Node("A")));
            Assert.Equal(RecoveryText.HandRepair, shell.Text("RecoveryHandRepair"));

            shell.Click(shell.InView<Button>("PreserveCheckout"));

            shell.WaitUntil(() => Shows(shell, "RecoveryNotice"), "the preview is refused");
            Assert.Equal("iDevelop cannot confirm that the checkout and its Git folder share a file system on this system, so Restore will not replace files. " +
                "Change them outside iDevelop, then preserve again.", Notice(shell));
            Assert.False(Shows(shell, "RestoreCheckout"));
            Assert.Equal("late\n", File.ReadAllText(Path.Combine(f.Checkout(A), "result.txt")));

            File.WriteAllText(Path.Combine(f.Checkout(A), "result.txt"), "done\n");
            shell.Click(shell.InView<Button>("PreserveCheckout"));
            shell.WaitUntil(() => Shows(shell, "RestoreCheckout"), "the second preservation is previewed", () => $"Notice: {Notice(shell)}");
            Assert.Empty(shell.TextsOf("RestoreMove"));
            Assert.StartsWith("Nothing moves: the checkout matches its baseline.", shell.Text("RestoreRest"));
            shell.Click(shell.InView<Button>("RestoreCheckout"));

            shell.WaitForStatus("Completed");
            Assert.Equal(1, f.Launches("B"));
        }
        finally
        {
            RecoveryViewModel.HandRepair = handRepair;
        }
    }

    [AvaloniaFact]
    public void A_block_that_restoring_cannot_clear_offers_no_Preserve_and_says_how_the_run_ends()
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
        shell.WaitForCard("D", "Blocked");
        shell.WaitForStatus("Needs attention");

        shell.Click(shell.Header(shell.Node("D")));

        Assert.StartsWith("The results it joins conflict.", shell.Text("RunTaskDetail"));
        Assert.Equal("settings.txt", shell.Find<TextBox>("RecoveryPaths").Text);
        Assert.Equal(["The results it joins conflict."], shell.TextsOf("RecoveryBlockProblem"));
        Assert.Equal("Preserve and restore cannot clear this block. Stop Workflow ends the run, and finished work stays.", shell.Text("RecoveryNoAction"));
        Assert.False(Shows(shell, "PreserveCheckout"));
        Assert.False(Shows(shell, "CloseAsStopped"));
        Assert.Equal(0, f.Launches("D"));
        // The join's block names no attempt here; one that does is still not a block Preserve and restore clears.
        var conflict = shell.Window.ViewModel.Canvas!.SelectedNode!.RunTask!.Block! with { Attempt = new AttemptId(Guid.NewGuid()) };
        using var panel = new RecoveryViewModel(shell.WorkflowRun!, new TaskView(d, TaskState.Blocked) { Block = conflict }, _ => "D", _ => { });
        Assert.Equal((false, false), (panel.CanRestore, panel.ShowsPreserve));
        Assert.NotNull(panel.NoActionNote);
        // A preservation records blocks of its own on the same checkout, and the section stays with its preview.
        var view = new TaskView(d, TaskState.Blocked) { Block = conflict };
        Assert.True(panel.Shows(shell.WorkflowRun!, view with { Block = conflict with { Operation = new OperationId(Guid.NewGuid()) } }));
        Assert.False(panel.Shows(shell.WorkflowRun!, view with { Block = conflict with { Attempt = new AttemptId(Guid.NewGuid()) } }));
    }

    [AvaloniaFact]
    public void A_moved_stash_says_how_to_put_it_back_and_Restore_says_the_block_stays_until_it_is_back()
    {
        using var f = Chain();
        f.Answer("A", f.Writes("result.txt", "done\n", "A ready.")).Answer("B", f.Says("B ready."));
        var head = f.Run("rev-parse", "HEAD").Trim();
        var moved = 0;
        f.Probe = point =>
        {
            if (point == "coordinator.publish.before" && Interlocked.Exchange(ref moved, 1) == 0)
            {
                f.Run("update-ref", "refs/stash", head);
            }
        };
        var shell = f.Window();
        shell.StartRun();
        shell.WaitForCard("A", "Blocked");
        shell.Click(shell.Header(shell.Node("A")));
        shell.WaitUntil(() => Shows(shell, "RecoveryRefRepair"), "the section reads the stash's evidence");

        Assert.Equal(["A shared ref changed that no task of the run changed."], shell.TextsOf("RecoveryBlockProblem"));
        Assert.Equal("refs/stash", shell.Find<TextBox>("RecoveryRefs").Text);
        Assert.Equal($"refs/stash pointed at nothing when the turn started and pointed at {head[..12]} when this was found. " +
            "iDevelop does not move these refs. Put each one back outside iDevelop, then preserve and restore so iDevelop checks them again. " +
            "The block clears once they are back.", shell.Text("RecoveryRefRepair"));
        shell.Click(shell.InView<Button>("PreserveCheckout"));
        shell.WaitUntil(() => Shows(shell, "RestoreCheckout"), "the restoration is previewed", () => $"Notice: {Notice(shell)}");
        Assert.Equal("Nothing moves: the checkout matches its baseline. It clears no block. " +
            "It checks 1 block on shared refs again, which clears only if those refs are back as recorded.", shell.Text("RestoreRest"));

        shell.Click(shell.InView<Button>("RestoreCheckout"));

        shell.WaitUntil(() => Notice(shell) is { Length: > 0 }, "the restore says what it did");
        Assert.Equal("Restore finished, but 1 block still holds this task. Each one says what it needs.", Notice(shell));
        Assert.Equal(("Blocked", 0), (shell.CardText("A", "CardStatus"), f.Launches("B")));
        Assert.True(Shows(shell, "PreserveCheckout"));

        f.Run("update-ref", "-d", "refs/stash");
        shell.Click(shell.InView<Button>("PreserveCheckout"));
        shell.WaitUntil(() => Shows(shell, "RestoreCheckout"), "the second restoration is previewed", () => $"Notice: {Notice(shell)}");
        shell.Click(shell.InView<Button>("RestoreCheckout"));
        shell.WaitForStatus("Completed");
        Assert.Equal((1, 1), (f.Launches("A"), f.Launches("B")));
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
        Assert.Equal(("No exit was recorded.", "No turn-end capture was recorded."), (shell.Text("RecoveryTurnEnd"), shell.Text("RecoveryCaptures")));
        Assert.False(Shows(shell, "RecoveryStillRuns"));
        Assert.False(Shows(shell, "PreserveCheckout"));
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
        using (var panel = new RecoveryViewModel(shell.WorkflowRun!, unsettled, _ => "A", _ => { }))
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
        Assert.Equal("Stopping waits for you to close \"A\" as stopped.", shell.Text("RunActivity"));
        Assert.Equal(RunPhase.StopRequested, f.Record().Phase);
        shell.Click(shell.InCard<Button>("A", "CardAttention"));
        shell.Click(shell.InView<TextBox>("RecoveryReason"));
        shell.Type("Nothing runs any more.");
        shell.Click(shell.InView<Button>("CloseAsStopped"));

        shell.WaitForStatus("Stopped");
        Assert.Equal(("Closed as stopped", "Not started"), (shell.CardText("A", "CardStatus"), shell.CardText("B", "CardStatus")));
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
        Assert.Equal(("Not started", "Not started"), (shell.CardText("A", "CardStatus"), shell.CardText("B", "CardStatus")));
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
        Assert.Equal(("Cancelled", "Not started"), (shell.CardText("A", "CardStatus"), shell.CardText("B", "CardStatus")));
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

    private static string? Notice(Shell shell) => shell.Has<TextBlock>("RecoveryNotice") ? shell.Find<TextBlock>("RecoveryNotice").Text : null;
}
