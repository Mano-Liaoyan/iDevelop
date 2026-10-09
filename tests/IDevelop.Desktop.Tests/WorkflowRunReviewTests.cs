using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using IDevelop.Desktop.Execution;
using IDevelop.Execution;
using IDevelop.Projects;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.AppTempFolder;
using static IDevelop.Desktop.Tests.WorkflowRunFixture;

namespace IDevelop.Desktop.Tests;

/// <summary>
/// A workflow run's review in the window (E3g.2): a consumer that an accepted fix made stale, rebased from its Updated
/// inputs section, and a fix round that closing iDevelop interrupted, which waits for Continue fix or Retry fix.
/// </summary>
[Collection(ProcessCollection.Name)]
public sealed class WorkflowRunReviewTests
{
    private static readonly TaskId A = TestTasks.Design;
    private static readonly TaskId B = TestTasks.Build;
    private static readonly TaskId R = TestTasks.Review;
    private static readonly TaskId D = new(Guid.Parse("019a9d2e-5d10-7a00-8000-000000000044"));

    private const string Changes = """{"status": "verdict", "verdict": "changes", "findings": [{"id": "1", "text": "add subtracts.", "change": "Return a + b."}]}""";
    private const string Agree = """{"status": "verdict", "verdict": "approve", "findings": []}""";
    private const string Fixed = """[{"id": "1", "answer": "fixed", "note": "It adds now."}]""";

    /// <summary><c>A → Review → B</c>, where the review asks for one change and agrees after the fix.</summary>
    private static WorkflowRunFixture Reviewed(params WorkflowEdit[] more)
    {
        var f = new WorkflowRunFixture([Task(A, "A", 105), Review(R, "Review", 405), Task(B, "B", 705), Dependency(A, R), Dependency(R, B), .. more]);
        f.Answer("Review", f.Says(Verdict(Changes), "session-r"), f.Says(Verdict(Agree), "session-r")).Answer("B", f.Writes("b.txt", "B\n", "B ready."))
            .Route("A reviewer read your change", "A").Route("Closing iDevelop interrupted", "A").Route("You retry this fix round", "A")
            .Route("The implementer answered", "Review");
        return f;
    }

    [AvaloniaFact]
    public void A_consumer_an_accepted_fix_made_stale_is_rebased_from_its_panel_without_another_run()
    {
        using var f = Reviewed(TaskAt(D, "D", 405, 300, WorkflowRunFixture.Codex, "Build D."), Dependency(A, D));
        f.Answer("A", f.Writes("calc.txt", "a - b\n", "A ready."), f.Writes("calc.txt", "a + b\n", Answers(Fixed)))
            .Answer("D", f.Writes("d.txt", "D\n", "D ready."));
        var shell = f.Window();
        shell.StartRun();

        shell.WaitForCard("D", "Inputs changed");
        shell.WaitForStatus("Needs attention");
        Assert.Equal(["Succeeded", "Succeeded", "Succeeded"], new[] { "A", "Review", "B" }.Select(title => shell.CardText(title, "CardStatus")));
        Assert.True(shell.InCard<Button>("D", "CardAttention").IsEffectivelyEnabled);
        shell.Click(shell.InCard<Button>("D", "CardAttention"));
        Assert.Equal("D", shell.Window.ViewModel.Canvas!.SelectedNode?.Title);
        Assert.Null(shell.Window.ViewModel.Conversation);
        Assert.Equal("A task before it handed on a newer result after this one finished.", shell.Text("RunTaskDetail"));

        var second = f.Window();
        second.WaitUntil(() => second.WorkflowRun is not null, "the second window shows the run");
        second.Click(second.Header(second.Node("D")));
        Assert.False(second.InView<Button>("ReviewUpdatedInputs").IsEffectivelyEnabled);

        // Another step holds the repository's lock for a moment, so the first preview is refused as busy and tried again.
        var held = new FileStream(Path.Combine(f.Project, ".git", "idevelop", "mutation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        _ = System.Threading.Tasks.Task.Delay(TimeSpan.FromMilliseconds(1500)).ContinueWith(_ => held.Dispose());
        shell.Click(shell.InView<Button>("ReviewUpdatedInputs"));
        shell.WaitUntil(() => shell.Has<Button>("ApproveRebase") && shell.Find<Button>("ApproveRebase").IsEffectivelyVisible, "the rebase is previewed",
            () => $"Notice: {(shell.Has<TextBlock>("RebaseNotice") ? shell.Text("RebaseNotice") : null)}");
        Assert.Equal("\"A\"", shell.Find<TextBox>("RebaseUpdated").Text);
        Assert.Equal("d.txt", shell.Find<TextBox>("RebaseChanges").Text);
        Assert.Equal("D ready.", shell.Find<TextBox>("RebaseReport").Text);
        Assert.Equal("Its change replays cleanly. Its checkout gets these files from the newer inputs:", shell.Text("RebaseCandidate"));
        Assert.Equal("calc.txt", shell.Find<TextBox>("RebaseCandidatePaths").Text);
        Assert.False(shell.Find<TextBlock>("RebaseUnavailable").IsEffectivelyVisible);
        Assert.Equal("a - b\n", File.ReadAllText(Path.Combine(f.Checkout(D), "calc.txt")));
        InspectorLayoutTests.AssertLaidOut(shell);

        shell.Click(shell.InView<Button>("ApproveRebase"));

        shell.WaitForStatus("Completed");
        Assert.Equal("Succeeded", shell.CardText("D", "CardStatus"));
        Assert.Equal(("a + b\n", "D\n"), (File.ReadAllText(Path.Combine(f.Checkout(D), "calc.txt")), File.ReadAllText(Path.Combine(f.Checkout(D), "d.txt"))));
        Assert.Equal((2, 2, 1, 1), (f.Launches("A"), f.Launches("Review"), f.Launches("B"), f.Launches("D")));
        Assert.Single(f.Record().Results, result => result.Origin is ResultOrigin.Rebased);
        Assert.False(shell.Has<Button>("ReviewUpdatedInputs") && shell.Find<Button>("ReviewUpdatedInputs").IsEffectivelyVisible);
    }

    /// <summary>A read-only agent whose prompt starts with its title, so its result is a report with no code of its own.</summary>
    private static WorkflowEdit.PlaceNode Reader(TaskId id, string title, double x, double y) =>
        new(id, new Blueprint(new("example.reader", 1), "Reader",
            new WorkSpec.Agent(AgentAccess.ReadOnly, Proposes: false, PromptTemplate.Parse("{{#title}}# {{title}}\n\n{{/title}}Read the project.\n")), [],
            new(WorkflowRunFixture.Codex, ConversationMode.Autonomous)), new CanvasPoint(x, y))
        { Title = title, Settings = new NodeSettings(WorkflowRunFixture.Codex, ConversationMode.Autonomous) };

    [AvaloniaFact]
    public void A_stale_report_says_it_cannot_be_rebased_and_offers_no_rebase()
    {
        using var f = Reviewed(Reader(D, "D", 405, 300), Dependency(A, D));
        f.Answer("A", f.Writes("calc.txt", "a - b\n", "A ready."), f.Writes("calc.txt", "a + b\n", Answers(Fixed))).Answer("D", f.Says("D read it."));
        var shell = f.Window();
        shell.StartRun();
        shell.WaitForCard("D", "Inputs changed");
        shell.WaitForStatus("Needs attention");

        shell.Click(shell.InCard<Button>("D", "CardAttention"));

        Assert.Equal(RecoveryText.NoRebase, shell.InView<TextBlock>("RebaseUnavailable").Text);
        Assert.False(shell.Find<Button>("ReviewUpdatedInputs").IsEffectivelyVisible);
        Assert.Equal(1, f.Launches("D"));
    }

    [AvaloniaFact]
    public void A_fix_round_whose_turn_end_a_crash_lost_is_closed_as_stopped_and_then_continues_in_its_session()
    {
        using var f = Reviewed();
        f.Answer("A", f.Writes("calc.txt", "a - b\n", "A ready."), f.Writes("keep.txt", "keep\n", "Fixing."), f.Writes("calc.txt", "a + b\n", Answers(Fixed)));
        // The window loses the fix turn's root exit, as a crash between the client's exit and its record does.
        var lost = 0;
        f.Probe = point =>
        {
            if (point == "journal.root-exit.before" && f.Launches("A") == 2 && Interlocked.Exchange(ref lost, 1) == 0)
            {
                throw new InvalidOperationException("Crashed before the root exit was recorded.");
            }
        };
        var shell = f.Window();
        shell.StartRun();
        shell.WaitForCard("A", "Uncertain");
        shell.Click(shell.InCard<Button>("A", "CardAttention"));
        shell.WaitUntil(() => shell.Has<TextBox>("RecoveryReason") && shell.Find<TextBox>("RecoveryReason").IsEffectivelyVisible, "the turn can be closed");
        shell.Click(shell.InView<TextBox>("RecoveryReason"));
        shell.Type("The fix's client is gone.");
        shell.Click(shell.InView<Button>("CloseAsStopped"));

        shell.WaitForCard("Review", "Fix interrupted");
        shell.Click(shell.InCard<Button>("Review", "CardAttention"));
        Assert.Equal(WorkflowRunText.Unbroken("Fix round 1 of \"A\" ended without a recorded end, and it was closed as stopped. Continue the fix in its session, or retry it in a fresh one."),
            shell.Text("RunTaskDetail"));
        shell.Click(shell.InView<Button>("ContinueFix"));

        shell.WaitForStatus("Completed");
        Assert.Equal((3, 2, 1), (f.Launches("A"), f.Launches("Review"), f.Launches("B")));
        Assert.StartsWith("Closing iDevelop interrupted your work on these findings.", f.Prompt("A", 3));
        Assert.Single(f.Record().Attempts.Values, attempt => attempt.Cause is AttemptCause.Continue);
        Assert.Equal("a + b\n", File.ReadAllText(Path.Combine(f.Checkout(B), "calc.txt")));
    }

    [AvaloniaFact]
    public void A_fix_round_without_a_session_says_why_it_cannot_continue_and_offers_only_Retry_fix()
    {
        using var f = Reviewed();
        // Neither A's turn nor its fix round reports a session, so the interrupted round has none to go on in.
        var silent = FakeRule.On().Write("keep.txt", "keep\n").Write(Path.Combine(f.Evidence, "fix-wrote"), "yes").WaitForFile(f.Gate("never"));
        f.Answer("A", FakeRule.On().Write("calc.txt", "a - b\n").Print(FakeAgents.ReplyLines(ClientId.Codex, "A ready.")), silent)
            .Route("Your earlier session could not continue", "A");
        var shell = Interrupted(f);

        Assert.Equal(WorkflowRunText.Unbroken("Closing iDevelop interrupted fix round 1 of \"A\". The fix reported no session, so it cannot go on. Retry fix starts the round in a fresh session."),
            shell.Text("RunTaskDetail"));
        Assert.Equal((false, true), (shell.InView<Button>("ContinueFix").IsEffectivelyEnabled, shell.Find<Button>("RetryFix").IsEffectivelyEnabled));
    }

    /// <summary>Runs the review until its fix round writes <c>keep.txt</c>, then closes the project and opens it in a new window.</summary>
    private static Shell Interrupted(WorkflowRunFixture f)
    {
        var first = f.Window();
        first.StartRun();
        first.WaitUntil(() => File.Exists(Path.Combine(f.Evidence, "fix-wrote")), "the fix round writes its file");
        var closing = first.Window.ViewModel.Projects[0].CloseAsync().AsTask();
        first.WaitUntil(() => closing.IsCompleted, "the window lets go of the project");

        var shell = f.Window();
        shell.WaitUntil(() => shell.WorkflowRun is not null, "the window shows the run");
        shell.WaitForCard("Review", "Fix interrupted");
        Assert.Equal(("Paused", "Interrupted"), (shell.RunStatus, shell.CardText("A", "CardStatus")));
        shell.Click(shell.InCard<Button>("Review", "CardAttention"));
        Assert.Equal("Review", shell.Window.ViewModel.Canvas!.SelectedNode?.Title);
        Assert.Null(shell.Window.ViewModel.Conversation);
        return shell;
    }

    private static FakeRule Interrupting(WorkflowRunFixture f) => FakeRule.On().Print(FakeAgents.SessionLine(ClientId.Codex, "session-1"))
        .Write("keep.txt", "keep\n").Write(Path.Combine(f.Evidence, "fix-wrote"), "yes").WaitForFile(f.Gate("never"));

    [AvaloniaFact]
    public void A_fix_round_that_closing_iDevelop_interrupted_waits_for_Continue_fix_and_goes_on_in_its_session()
    {
        using var f = Reviewed();
        f.Answer("A", f.Writes("calc.txt", "a - b\n", "A ready."), Interrupting(f), f.Writes("calc.txt", "a + b\n", Answers(Fixed)));
        var shell = Interrupted(f);

        Assert.Equal(WorkflowRunText.Unbroken("Closing iDevelop interrupted fix round 1 of \"A\". Continue the fix in its session, or retry it in a fresh one."),
            shell.Text("RunTaskDetail"));
        Assert.True(shell.InView<Button>("ContinueFix").IsEffectivelyEnabled);
        Assert.True(shell.InView<Button>("RetryFix").IsEffectivelyEnabled);
        Assert.Equal("1 task waits for you", shell.Text("WaitingCount"));
        // Resume does not choose: no fix starts until the person does.
        shell.Click(shell.Find<Button>("ResumeRun"));
        shell.WaitForStatus("Needs attention");
        Assert.Equal(2, f.Launches("A"));

        var node = shell.Window.ViewModel.Canvas!.SelectedNode!;
        node.ContinueFixCommand.Execute(null);
        node.ContinueFixCommand.Execute(null);

        shell.WaitForStatus("Completed");
        Assert.Equal((3, 2, 1), (f.Launches("A"), f.Launches("Review"), f.Launches("B")));
        Assert.StartsWith("Closing iDevelop interrupted your work on these findings.", f.Prompt("A", 3));
        Assert.Single(f.Record().Attempts.Values, attempt => attempt.Cause is AttemptCause.Continue);
        Assert.Equal("a + b\n", File.ReadAllText(Path.Combine(f.Checkout(B), "calc.txt")));
        Assert.False(shell.Has<Button>("ContinueFix") && shell.Find<Button>("ContinueFix").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void A_standalone_interrupted_fix_offers_no_choice_once_a_workflow_run_owns_its_review()
    {
        using var f = new WorkflowRunFixture(Task(A, "A", 105), Review(R, "Review", 405), Dependency(A, R));
        f.Answer("A", f.Writes("calc.txt", "a - b\n", "A ready."), Interrupting(f), f.Says("A again.", gate: "run-a"))
            .Answer("Review", f.Says(Verdict(Changes), "session-r")).Route("A reviewer read your change", "A");
        // The review's fix round runs on its own, outside any workflow run, until closing the project interrupts it.
        var workflow = WorkflowDocument.OpenProject(f.Project).Single().Current;
        var standalone = ProjectRuns.Open(f.Project, f.Clients());
        f.Configure(standalone);
        standalone.Follow(workflow);
        Assert.IsType<StartResult.Started>(standalone.Start(workflow.Tasks[A]));
        Until(() => standalone.Latest.GetValueOrDefault(A) is { Status: AttemptStatus.Succeeded } && standalone.Active.IsEmpty, "A succeeds on its own");
        Assert.IsType<StartResult.Started>(standalone.Start(workflow.Tasks[R]));
        Until(() => File.Exists(Path.Combine(f.Evidence, "fix-wrote")), "the fix round writes its file");
        var leaving = standalone.DisposeAsync().AsTask();
        Until(() => leaving.IsCompleted, "the standalone runs stop");

        var shell = f.Window();
        shell.Click(shell.Header(shell.Node("Review")));
        shell.WaitUntil(() => shell.Has<Button>("ContinueFix") && shell.Find<Button>("ContinueFix").IsEffectivelyVisible, "the review offers its own choice");

        shell.StartRun();
        shell.WaitForCard("A", "Running");
        shell.Click(shell.Header(shell.Node("Review")));

        Assert.Equal("Waits for \"A\"", shell.InView<TextBlock>("RunTaskStatus").Text);
        Assert.False(shell.Find<Button>("ContinueFix").IsEffectivelyVisible);
        Assert.False(shell.Find<Button>("RetryFix").IsEffectivelyVisible);
        shell.Click(shell.Find<Button>("StopWorkflow"));
        shell.WaitForStatus("Stopped");
    }

    private static void Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting until {what}.");
            Thread.Sleep(20);
        }
    }

    [AvaloniaFact]
    public void Retry_fix_starts_the_interrupted_round_again_in_a_fresh_session()
    {
        using var f = Reviewed();
        f.Answer("A", f.Writes("calc.txt", "a - b\n", "A ready."), Interrupting(f), f.Writes("calc.txt", "a + b\n", Answers(Fixed), "session-2"));
        var shell = Interrupted(f);
        shell.Click(shell.Find<Button>("ResumeRun"));
        shell.WaitForStatus("Needs attention");

        shell.Click(shell.InView<Button>("RetryFix"));

        shell.WaitForStatus("Completed");
        Assert.Equal((3, 2, 1), (f.Launches("A"), f.Launches("Review"), f.Launches("B")));
        Assert.StartsWith("You retry this fix round in a fresh session.", f.Prompt("A", 3));
        Assert.Single(f.Record().Attempts.Values, attempt => attempt.Cause is AttemptCause.Retry);
        Assert.Equal("a + b\n", File.ReadAllText(Path.Combine(f.Checkout(B), "calc.txt")));
    }
}
