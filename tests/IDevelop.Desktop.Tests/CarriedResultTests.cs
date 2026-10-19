using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Execution;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.WorkflowRunFixture;

namespace IDevelop.Desktop.Tests;

/// <summary>
/// Results of earlier runs in the window (#90): each card keeps where its task stands between runs and after a restart,
/// as Succeeded, Out of date, or Waits for the tasks before it, a node's Run counts an earlier current result as complete,
/// and its preflight lists each result it uses from an earlier run.
/// </summary>
[Collection(ProcessCollection.Name)]
public sealed class CarriedResultTests
{
    private static readonly TaskId A = TestTasks.Design;
    private static readonly TaskId B = TestTasks.Build;
    private static readonly TaskId C = TestTasks.Review;

    /// <summary><c>A → C ← B</c>, with the two roots above each other and the join to their right, all in view.</summary>
    private static WorkflowRunFixture Join()
    {
        var f = new WorkflowRunFixture(Task(A, "A", 105), At(B, "B", 105, 250), At(C, "C", 405, 170), Dependency(A, C), Dependency(B, C));
        f.Answer("A", f.Writes("out-a.txt", "A\n", "A ready.")).Answer("B", f.Writes("out-b.txt", "B\n", "B ready."), f.Writes("out-b.txt", "B2\n", "B ready."))
            .Answer("C", f.Says("C ready."));
        return f;
    }

    private static WorkflowEdit.PlaceNode At(TaskId id, string title, double x, double y) =>
        AppTempFolder.TaskAt(id, title, x, y, WorkflowRunFixture.Codex, $"Build {title}.");

    private static void Select(Shell shell, string title) => shell.Click(shell.Header(shell.Node(title)));

    /// <summary>Runs <paramref name="title"/> through its preflight, as its inspector's Run does, until the run completes.</summary>
    private static void RunNode(Shell shell, string title)
    {
        OpenPreflight(shell, title);
        Start(shell, title);
        Assert.Equal("Completed", shell.RunStatus);
    }

    /// <summary>Starts the open preflight and waits until the new run settles.</summary>
    private static void Start(Shell shell, string what)
    {
        var earlier = shell.WorkflowRun?.Address;
        shell.Click(shell.Find<Button>("PreflightStart"));
        // The run lets go of its retention pins after it settles, which a test waits for, so its Git folder is quiet.
        shell.WaitUntil(() => shell.WorkflowRun is { IsActive: false, View.PinsReleased: true } run && run.Address != earlier, $"the run of \"{what}\" settles",
            () => $"Run: {shell.RunStatus}, notice: {shell.Status}");
    }

    private static void OpenPreflight(Shell shell, string title)
    {
        Select(shell, title);
        shell.Click(shell.InView<Button>("RunTask"));
        shell.WaitUntil(() => shell.Preflight is { IsReady: true }, $"the preflight of \"{title}\" reads the project", () => $"Notice: {shell.Status}");
    }

    /// <summary>
    /// Expands the cards in order, as their chevrons do. The window opens the project directly, so it restores no expanded
    /// cards; put C before B, since B's card pushes C's column to the right, toward the inspector.
    /// </summary>
    private static void Expand(Shell shell, params string[] titles)
    {
        foreach (var title in titles)
        {
            shell.Click(shell.InCard<ToggleButton>(title, "CardExpand"));
        }

        shell.Render();
    }

    private static bool Shows(Shell shell, string automationId) => shell.Has<TextBlock>(automationId) && shell.Find<TextBlock>(automationId).IsEffectivelyVisible;

    private static string[] Cards(Shell shell) => [.. new[] { "A", "B", "C" }.Select(title => shell.CardText(title, "CardStatus"))];

    [AvaloniaFact]
    public void The_cards_keep_where_each_task_stands_after_a_run_and_after_a_restart()
    {
        using var f = Join();
        var shell = f.Window();
        var agent = shell.CardText("A", "CardStatus");

        RunNode(shell, "B");

        Assert.Equal(["Not started", "Succeeded", "Waits for \"A\""], Cards(shell));
        Select(shell, "C");
        Assert.Equal("Waits for \"A\"", shell.InView<TextBlock>("RunTaskStatus").Text);
        // Said once: the status names what it waits for, and the line under Run what to run first. The run never started
        // C, so it has no conversation with it.
        Assert.False(Shows(shell, "RunTaskDetail") || Shows(shell, "RunOwner") || Shows(shell, "RunConversationNote"));
        Assert.Equal(WorkflowRunText.Unbroken("Runs after \"A\". Run \"A\"\u00A0first."), shell.InView<TextBlock>("StartProblem").Text);
        // A card whose task nothing more is said of than that it has not started shows its agent, as outside any run.
        Assert.True(shell.InCard<TextBlock>("A", "CardAgent").IsEffectivelyVisible);
        shell.Window.Close();
        shell.Render();

        var reopened = f.Window();
        reopened.WaitUntil(() => reopened.CardText("B", "CardStatus") == "Succeeded", "the reopened window reads the earlier run",
            () => string.Join(", ", Cards(reopened)));
        Assert.Null(reopened.WorkflowRun);
        Assert.Equal([agent, "Succeeded", "Waits for \"A\""], Cards(reopened));
        // A waiting card's subtitle is what it waits for, not its agent.
        Assert.True(reopened.InCard<TextBlock>("C", "CardStatus").IsEffectivelyVisible);
        Assert.False(reopened.InCard<TextBlock>("C", "CardAgent").IsEffectivelyVisible);
        Select(reopened, "B");
        Assert.Equal("Succeeded", reopened.InView<TextBlock>("RunTaskStatus").Text);
        Assert.False(Shows(reopened, "RunOwner"));
        // No run is on the canvas, so Open conversation opens the task's own conversation and no run's.
        Assert.False(Shows(reopened, "RunConversationNote"));
        Assert.Equal("Its result from an earlier run still\u00A0counts.", reopened.Text("RunTaskDetail"));
        Select(reopened, "C");
        Assert.Equal(WorkflowRunText.Unbroken("Runs after \"A\". Run \"A\"\u00A0first."), reopened.InView<TextBlock>("StartProblem").Text);
    }

    [AvaloniaFact]
    public void A_card_between_runs_counts_the_task_it_waits_for_when_its_name_does_not_fit_and_its_tooltip_names_it()
    {
        const string longTitle = "Render the settings view for every user";
        using var f = new WorkflowRunFixture(Task(A, longTitle, 105), At(B, "B", 105, 250), At(C, "C", 405, 170), Dependency(A, C), Dependency(B, C));
        f.Answer(longTitle, f.Says("Rendered.")).Answer("B", f.Says("B ready.")).Answer("C", f.Says("C ready."));
        var shell = f.Window();

        RunNode(shell, "B");
        AssertCounted(shell);
        shell.Window.Close();
        shell.Render();

        var reopened = f.Window();
        reopened.WaitUntil(() => reopened.CardText("B", "CardStatus") == "Succeeded", "the reopened window reads the earlier run",
            () => reopened.CardText("C", "CardStatus"));
        AssertCounted(reopened);

        static void AssertCounted(Shell shell)
        {
            shell.Render();
            Assert.Equal("Waits for 1 task", shell.CardText("C", "CardStatus"));
            // The card's peek lists the task it waits for by its whole name.
            Assert.Contains(longTitle, shell.PeekTexts("C")!);
            string[] cut = [.. shell.Nodes().SelectMany(node => node.GetVisualDescendants().OfType<TextBlock>())
                .Where(text => text.IsEffectivelyVisible && (text.TextLayout.TextLines.Any(line => line.HasCollapsed) || text.TextLayout.Width > text.Bounds.Width + 0.5))
                .Select(text => text.Text ?? "")];
            Assert.Equal([longTitle], cut);
        }
    }

    [AvaloniaFact]
    public void A_node_s_Run_uses_an_earlier_result_its_preflight_lists_and_an_edit_puts_that_result_out_of_date()
    {
        using var f = Join();
        var shell = f.Window();
        RunNode(shell, "B");

        OpenPreflight(shell, "A");

        Assert.Equal([WorkflowRunText.Unbroken("Uses \"B\"'s result from an earlier\u00A0run.")], shell.TextsOf("PreflightCarriedResult"));
        Assert.Equal(["A", "C"], shell.TextsOf("PreflightTask"));
        Start(shell, "A");
        Assert.Equal("Completed", shell.RunStatus);
        Assert.Equal((1, 1, 1), (f.Launches("A"), f.Launches("B"), f.Launches("C")));
        Assert.Equal(["Succeeded", "Succeeded", "Succeeded"], Cards(shell));
        Select(shell, "B");
        Assert.Equal("Succeeded", shell.InView<TextBlock>("RunTaskStatus").Text);
        Assert.Equal(WorkflowRunText.Unbroken("The last run of the \"Workflow\" workflow used this task's result from an earlier\u00A0run."), shell.Text("RunOwner"));
        // The run carried B's result and never talked to it, so no run conversation is offered for it.
        Assert.False(Shows(shell, "RunConversationNote"));
        // The run bar counts the tasks it ran apart from the one it carried.
        Assert.Equal("2 of 2 done · 1 from an earlier run", shell.Text("RunProgress"));

        // A new model for B is a new definition: its result no longer counts, nor does C's, which used it.
        var canvas = shell.Window.ViewModel.Canvas!;
        canvas.Edit(new WorkflowEdit.SetExecution(B, WorkflowRunFixture.Codex with { Model = "gpt-5.4" }));
        shell.Render();

        Assert.Equal(["Succeeded", "Out of date", "Out of date"], Cards(shell));
        Assert.Equal("Out of date", shell.InView<TextBlock>("RunTaskStatus").Text);
        Assert.Equal("Out of date because it changed since it\u00A0ran.", shell.Text("RunTaskDetail"));
        Assert.Equal(StatusTone.Warning, shell.Window.ViewModel.Canvas!.Nodes.Single(node => node.Title == "B").RunTone);
        Assert.Equal(NodeState.OutOfDate, shell.Window.ViewModel.Canvas!.Nodes.Single(node => node.Title == "B").State);
        Select(shell, "C");
        // Said once, under Run.
        Assert.False(Shows(shell, "RunTaskDetail") || Shows(shell, "RunOwner"));
        Assert.Equal(WorkflowRunText.Unbroken("Out of date because\u200B \"B\" changed. Run \"B\"\u00A0first."), shell.InView<TextBlock>("StartProblem").Text);
    }

    [AvaloniaFact]
    public void A_task_after_one_that_is_out_of_date_names_what_put_it_out_of_date()
    {
        // A → B → C, all three ran, and then only A changed.
        using var f = new WorkflowRunFixture(Task(A, "A", 105), At(B, "B", 105, 250), At(C, "C", 405, 170), Dependency(A, B), Dependency(B, C));
        f.Answer("A", f.Writes("out-a.txt", "A\n", "A ready.")).Answer("B", f.Writes("out-b.txt", "B\n", "B ready.")).Answer("C", f.Says("C ready."));
        var shell = f.Window();
        RunNode(shell, "A");
        Assert.Equal(["Succeeded", "Succeeded", "Succeeded"], Cards(shell));

        shell.Window.ViewModel.Canvas!.Edit(new WorkflowEdit.EditTitle(A, "Parse source"));
        shell.Render();

        Assert.Equal(["Out of date", "Out of date"], new[] { "B", "C" }.Select(title => shell.CardText(title, "CardStatus")));
        Select(shell, "B");
        Assert.Equal("Out of date because \"Parse source\" changed. Run \"Parse source\" first.", Plain(shell.InView<TextBlock>("StartProblem").Text));
        // B did not change: it is out of date because A is.
        Select(shell, "C");
        Assert.Equal("Out of date because \"B\" is out of date. Run \"B\" first.", Plain(shell.InView<TextBlock>("StartProblem").Text));

        // In the narrowest inspector a line may end after "because", so "Out of date" never stands alone on the first one.
        shell.SizeInspector(280);
        Select(shell, "B");
        var problem = shell.InView<TextBlock>("StartProblem");
        Assert.Equal("Out of date because", Plain(problem.Text!.Substring(0, problem.TextLayout.TextLines[0].Length)).Trim());

        // The text as it reads, with its non-breaking and zero-width spaces made plain.
        static string Plain(string? text) => (text ?? "").Replace("\u200B", "").Replace('\u00A0', ' ');
    }

    [AvaloniaFact]
    public void A_settled_run_s_bar_stops_saying_its_results_are_current_once_an_edit_puts_one_out_of_date()
    {
        // A → B, which a node run of A runs, and C, which an earlier node run ran and this one never reaches.
        using var f = new WorkflowRunFixture(Task(A, "A", 105), At(B, "B", 405, 90), At(C, "C", 105, 250), Dependency(A, B));
        f.Answer("A", f.Says("A ready.")).Answer("B", f.Says("B ready.")).Answer("C", f.Says("C ready."));
        var shell = f.Window();
        RunNode(shell, "C");
        RunNode(shell, "A");
        var canvas = shell.Window.ViewModel.Canvas!;
        // The canvas reads the run's results once it settles, which an edit then judges.
        shell.WaitUntil(() => canvas.History?.CurrentOf(B) is not null, "the canvas reads the run's results");
        const string ran = "Every task it ran has a current result.";
        Assert.Equal(("2 of 2 done", ran), (shell.Text("RunProgress"), shell.Text("RunActivity")));

        // An edit of C puts C's result from the earlier run out of date, but no result of this run.
        canvas.Edit(new WorkflowEdit.EditTitle(C, "Write docs"));
        shell.Render();
        Assert.Equal("Out of date", shell.CardText("Write docs", "CardStatus"));
        Assert.Equal(ran, shell.Text("RunActivity"));

        // A new title for A puts A's result out of date, and B's, which took it (#85). The run still completed both.
        canvas.Edit(new WorkflowEdit.EditTitle(A, "Parse source"));
        shell.Render();
        Assert.Equal(["Out of date", "Out of date"], new[] { "Parse source", "B" }.Select(title => shell.CardText(title, "CardStatus")));
        Assert.Equal(("Completed", "2 of 2 done"), (shell.RunStatus, shell.Text("RunProgress")));
        Assert.False(Shows(shell, "RunActivity"), shell.Text("RunActivity"));

        // Undo gives A its title back, so both results count again, and the bar says so again.
        shell.Click(shell.Find<Button>("Undo"));
        shell.Render();
        Assert.Equal(["Succeeded", "Succeeded"], new[] { "A", "B" }.Select(title => shell.CardText(title, "CardStatus")));
        Assert.Equal(ran, shell.Text("RunActivity"));
        Assert.True(Shows(shell, "RunActivity"));
    }

    [AvaloniaFact]
    public void A_completed_Run_Workflow_s_bar_stops_saying_every_task_has_a_current_result_once_an_edit_puts_one_out_of_date()
    {
        using var f = new WorkflowRunFixture(Task(A, "A", 105), At(B, "B", 405, 90), Dependency(A, B));
        f.Answer("A", f.Says("A ready.")).Answer("B", f.Says("B ready."));
        var shell = f.Window();
        shell.StartRun();
        shell.WaitForStatus("Completed");
        shell.WaitUntil(() => shell.Window.ViewModel.Canvas!.History?.CurrentOf(B) is not null, "the canvas reads the run's results");
        Assert.Equal("Every task has a current result.", shell.Text("RunActivity"));

        // A new model for B is a new definition, so B's result no longer counts.
        shell.Window.ViewModel.Canvas!.Edit(new WorkflowEdit.SetExecution(B, WorkflowRunFixture.Codex with { Model = "gpt-5.4" }));
        shell.Render();

        Assert.Equal("Out of date", shell.CardText("B", "CardStatus"));
        Assert.Equal(("Completed", "2 of 2 done"), (shell.RunStatus, shell.Text("RunProgress")));
        Assert.False(Shows(shell, "RunActivity"), shell.Text("RunActivity"));
    }

    [AvaloniaFact]
    public void Open_conversation_of_a_task_a_settled_run_carried_opens_the_task_s_own_conversation()
    {
        using var f = Join();
        var shell = f.Window();
        RunNode(shell, "B");
        RunNode(shell, "A");

        Select(shell, "B");
        shell.Click(shell.InView<Button>("OpenConversation"));
        shell.WaitUntil(() => shell.Window.ViewModel.Conversation?.Target.Task == B, "B's conversation opens");
        // The run carried B's result and never talked to it.
        Assert.False(shell.Window.ViewModel.ConversationGoesThroughRun);
        shell.Window.ViewModel.CloseConversation();
        shell.Render();

        // The run talked to A, so A's conversation goes through it.
        Select(shell, "A");
        shell.Click(shell.InView<Button>("OpenConversation"));
        shell.WaitUntil(() => shell.Window.ViewModel.Conversation?.Target.Task == A, "A's conversation opens");
        Assert.True(shell.Window.ViewModel.ConversationGoesThroughRun);
    }

    [AvaloniaFact]
    public void A_node_s_preflight_uses_an_earlier_result_as_its_report_after_the_branch_changed_its_file()
    {
        using var f = Join();
        var shell = f.Window();
        RunNode(shell, "B");
        File.WriteAllText(Path.Combine(f.Project, "out-b.txt"), "other\n");
        f.Run("add", "out-b.txt");
        f.Run("-c", "commit.gpgSign=false", "commit", "-q", "-m", "Change B's file");

        OpenPreflight(shell, "A");

        // A carried result brings its report and artifacts, so the branch's own change to B's file never stops it.
        Assert.Equal([WorkflowRunText.Unbroken("Uses \"B\"'s result from an earlier\u00A0run.")], shell.TextsOf("PreflightCarriedResult"));
        Start(shell, "A");
        Assert.Equal((1, 1, 1), (f.Launches("A"), f.Launches("B"), f.Launches("C")));
    }

    [AvaloniaFact]
    public void A_task_that_runs_on_its_own_after_the_run_shows_that_run_instead_of_where_it_stood()
    {
        using var f = Join();
        f.Answer("B", f.Writes("out-b.txt", "B\n", "B ready."), f.Says("B again.", gate: "own"));
        var shell = f.Window();
        RunNode(shell, "B");
        Assert.Equal("Succeeded", shell.CardText("B", "CardStatus"));

        shell.RunOnItsOwn("B");

        shell.WaitForCard("B", "Running");
        f.Open("own");
        shell.WaitUntil(() => shell.Window.ViewModel.Canvas!.Nodes.Single(node => node.Title == "B").LastAttempt is { } attempt &&
            attempt.StatusLabel == "Succeeded", "B's own run ends");
        Select(shell, "B");
        Assert.Equal("Succeeded", shell.InView<TextBlock>("LastRunStatus").Text);
        Assert.False(shell.Find<TextBlock>("RunTaskStatus").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void A_task_whose_earlier_run_of_its_own_failed_shows_no_problem_once_a_later_workflow_run_succeeded()
    {
        using var f = Join();
        f.Answer("B", FakeRule.On().Print(FakeAgents.SessionLine(ClientId.Codex, "session-1")).Exit(1), f.Writes("out-b.txt", "B\n", "B ready."));
        var shell = f.Window();
        shell.RunOnItsOwn("B");
        shell.WaitForCard("B", "Failed");
        Assert.True(shell.InCard<Button>("B", "CardAttention").IsEffectivelyVisible);
        RunNode(shell, "B");

        shell.Click(shell.Find<Button>("DismissRun"));
        // The canvas reads where the task stands off the UI thread once the run settles, so on a busy machine the read can
        // end after the dismissal (#86).
        shell.WaitForCard("B", "Succeeded");

        Assert.False(shell.InCard<Button>("B", "CardAttention").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void A_node_run_while_a_run_is_active_counts_an_earlier_result_and_its_run_carries_it()
    {
        var d = new TaskId(Guid.Parse("019a9d2e-5d11-7a22-b3c4-5d6e7f809a44"));
        using var f = new WorkflowRunFixture(Task(A, "A", 105), At(B, "B", 105, 250), At(C, "C", 405, 170), At(d, "D", 105, 400),
            Dependency(A, C), Dependency(B, C));
        f.Answer("A", f.Writes("out-a.txt", "A\n", "A ready.")).Answer("B", f.Writes("out-b.txt", "B\n", "B ready."))
            .Answer("C", f.Says("C ready.")).Answer("D", f.Says("D ready.", gate: "d"));
        var shell = f.Window();
        RunNode(shell, "B");
        OpenPreflight(shell, "D");
        shell.Click(shell.Find<Button>("PreflightStart"));
        shell.WaitForCard("D", "Running");

        Select(shell, "C");
        Assert.Equal(WorkflowRunText.Unbroken("Runs after \"A\". Run \"A\"\u00A0first."), shell.InView<TextBlock>("StartProblem").Text);
        Select(shell, "A");
        shell.Click(shell.InView<Button>("RunTask"));

        shell.WaitForCard("C", "Succeeded");
        Assert.Equal("Succeeded", shell.CardText("B", "CardStatus"));
        Select(shell, "B");
        Assert.Equal(WorkflowRunText.Unbroken("A run of the \"Workflow\" workflow uses this task's result from an earlier run instead of running it\u00A0again."),
            shell.Text("RunOwner"));
        f.Open("d");
        shell.WaitForStatus("Completed");
        Assert.Equal((1, 1, 1, 1), (f.Launches("A"), f.Launches("B"), f.Launches("C"), f.Launches("D")));
        Assert.Null(shell.Preflight);
    }

    [AvaloniaFact]
    public void An_earlier_result_never_names_a_run_that_did_not_produce_it()
    {
        var d = new TaskId(Guid.Parse("019a9d2e-5d11-7a22-b3c4-5d6e7f809a44"));
        using var f = new WorkflowRunFixture(Task(A, "A", 105), At(B, "B", 105, 250), At(C, "C", 405, 170), At(d, "D", 105, 400),
            Dependency(A, C), Dependency(B, C));
        f.Answer("B", f.Writes("out-b.txt", "B\n", "B ready.")).Answer("D", f.Says("D ready."));
        var shell = f.Window();
        RunNode(shell, "B");

        RunNode(shell, "D");

        Select(shell, "B");
        Assert.Equal("Succeeded", shell.InView<TextBlock>("RunTaskStatus").Text);
        Assert.False(Shows(shell, "RunOwner"));
        Assert.Equal("Its result from an earlier run still\u00A0counts.", shell.Text("RunTaskDetail"));
        Assert.DoesNotContain(shell.TextsOf("RunOwner").Concat(shell.TextsOf("RunTaskDetail")), text => text.Contains("last run", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void A_task_whose_input_s_later_run_ended_without_a_result_says_so_once()
    {
        using var f = Join();
        f.Answer("B", f.Writes("out-b.txt", "B\n", "B ready."),
            FakeRule.On().Print(FakeAgents.SessionLine(ClientId.Codex, "session-1")).Exit(1));
        var shell = f.Window();
        RunNode(shell, "B");
        RunNode(shell, "A");

        // B runs again and fails, so C's result, which used B's first one, is out of date, and B has none to run after. A
        // failed task keeps its run open until the person stops it.
        OpenPreflight(shell, "B");
        var earlier = shell.WorkflowRun?.Address;
        shell.Click(shell.Find<Button>("PreflightStart"));
        shell.WaitUntil(() => shell.WorkflowRun?.Address != earlier && shell.CardText("B", "CardStatus") == "Failed", "B fails again");
        shell.Click(shell.Find<Button>("StopWorkflow"));
        shell.WaitUntil(() => shell.WorkflowRun is { IsActive: false, View.PinsReleased: true }, "the run stops", () => shell.RunStatus);

        Assert.Equal(["Succeeded", "Failed", "Out of date"], Cards(shell));
        Select(shell, "C");
        Assert.False(Shows(shell, "RunTaskDetail") || Shows(shell, "RunOwner"));
        Assert.Equal(WorkflowRunText.Unbroken("Out of date because a later run of \"B\" ended without a result. Run \"B\"\u00A0first."),
            shell.InView<TextBlock>("StartProblem").Text);
    }

    [AvaloniaFact]
    public void A_task_whose_newest_run_ended_without_a_result_shows_that_end_after_a_restart_as_before()
    {
        using var f = Join();
        // B ran on its own once before. In the run B fails, and the run stops A while it runs.
        f.Answer("A", f.Says("A ready.", gate: "a"))
            .Answer("B", f.Says("B on its own."), FakeRule.On().Print(FakeAgents.SessionLine(ClientId.Codex, "session-1")).Exit(1));
        var shell = f.Window();
        shell.RunOnItsOwn("B");
        shell.WaitUntil(() => shell.Window.ViewModel.Canvas!.Nodes.Single(node => node.Title == "B").LastAttempt is { StatusLabel: "Succeeded" },
            "B's own run ends");
        shell.StartRun();
        shell.WaitUntil(() => shell.CardText("B", "CardStatus") == "Failed" && shell.CardText("A", "CardStatus") == "Running", "B fails while A runs",
            () => string.Join(", ", Cards(shell)));
        shell.Click(shell.Find<Button>("StopWorkflow"));
        shell.WaitUntil(() => shell.WorkflowRun is { IsActive: false, View.PinsReleased: true }, "the run stops", () => shell.RunStatus);
        Assert.Equal(["Cancelled", "Failed", "Waits for 2 tasks"], Cards(shell));
        var before = Seen(shell);
        shell.Window.Close();
        shell.Render();

        var reopened = f.Window();
        reopened.WaitUntil(() => reopened.CardText("B", "CardStatus") == "Failed", "the reopened window reads the stopped run",
            () => string.Join(", ", Cards(reopened)));
        Assert.Null(reopened.WorkflowRun);
        Assert.Equal(before, Seen(reopened));
        Assert.False(reopened.InCard<TextBlock>("A", "CardAgent").IsEffectivelyVisible || reopened.InCard<TextBlock>("B", "CardAgent").IsEffectivelyVisible);

        // What each card and its inspector show: the card's status and state, and the inspector's status, detail, and owner line.
        static string[] Seen(Shell shell) => [.. new[] { "A", "B", "C" }.Select(title =>
        {
            Select(shell, title);
            var node = (TaskNodeViewModel)shell.Node(title).DataContext!;
            return string.Join(" | ", shell.CardText(title, "CardStatus"), node.State, shell.InView<TextBlock>("RunTaskStatus").Text,
                Shows(shell, "RunTaskDetail") ? shell.Text("RunTaskDetail") : "-", Shows(shell, "RunOwner") ? shell.Text("RunOwner") : "-");
        })];
    }

    [Fact]
    public void A_task_its_newest_run_closed_as_stopped_shows_it_and_says_why_as_that_run_did()
    {
        var view = new TaskView(A, TaskState.Failed)
        {
            End = new AttemptEnd.Recovered(RecoveryOutcome.Stopped, new OperationId(Guid.NewGuid()), "You closed its turn as stopped."),
        };
        var ended = new TaskStanding.Ended(view, DateTimeOffset.UnixEpoch);

        Assert.Equal((NodeState.Interrupted, "Closed as stopped"), ended.Card(_ => "A"));
        Assert.Equal("You closed its turn as stopped.", ended.Detail(_ => "A"));
        Assert.Equal("A run of the \"W\" workflow ran this task\u00A0last.", ended.Owner("W", hasAgent: true));
        Assert.Equal("A run of the \"W\" workflow asked for this approval\u00A0last.", ended.Owner("W", hasAgent: false));
    }

    [AvaloniaFact]
    public void An_expanded_card_between_runs_shows_the_earlier_result_what_it_waits_for_and_why_its_result_is_out_of_date()
    {
        using var f = Join();
        var shell = f.Window();
        RunNode(shell, "B");
        var runsAfterA = WorkflowRunText.Unbroken("Runs after \"A\". Run \"A\"\u00A0first.");

        // The peek lists what C waits for and says what to run first.
        var peek = shell.PeekTexts("C")!;
        Assert.Equal(["Starts after", "A", runsAfterA], peek.Where(text => text is "Starts after" or "A" || text == runsAfterA));
        Expand(shell, "C", "B");
        shell.Render();

        // The run that ran B is still on the canvas, so B's card reads that run's attempt.
        Assert.Equal(("Result", "B ready."), (shell.CardText("B", "CardNowHeading"), shell.CardText("B", "CardNowText")));
        Assert.StartsWith("Took ", shell.CardText("B", "CardNowMeta"));
        Assert.Equal("Starts after", shell.CardText("C", "CardNowHeading"));
        Assert.Contains("A", Shell.Texts(shell.InCard<ItemsControl>("C", "CardWaits")));
        Assert.Equal((runsAfterA, true, false), (shell.CardText("C", "CardRunNote"), shell.InCard<TextBlock>("C", "CardRunNote").IsEffectivelyVisible,
            shell.InCard<Button>("C", "CardRun").IsEffectivelyEnabled));
        Assert.False(shell.InCard<TextBlock>("B", "CardRunNote").IsEffectivelyVisible);
        shell.Window.Close();
        shell.Render();

        // After a restart no run is on the canvas, and B's card shows the earlier run's report: B's own conversation, which
        // the card reads between runs, has none.
        var reopened = f.Window();
        reopened.WaitUntil(() => reopened.CardText("B", "CardStatus") == "Succeeded", "the reopened window reads the earlier run",
            () => reopened.CardText("B", "CardStatus"));
        Expand(reopened, "C", "B");
        Assert.Equal(("Result", "B ready.", "From an earlier run"),
            (reopened.CardText("B", "CardNowHeading"), reopened.CardText("B", "CardNowText"), reopened.CardText("B", "CardNowMeta")));
        Assert.Equal(("Starts after", runsAfterA), (reopened.CardText("C", "CardNowHeading"), reopened.CardText("C", "CardRunNote")));

        // New instructions put B's result out of date, which its card says once, in its state's warning colour.
        reopened.Window.ViewModel.Canvas!.Edit(new WorkflowEdit.SetField(B, "instructions", "Build B again."));
        reopened.Render();
        Assert.Equal(("Out of date", "Out of date because it changed since it\u00A0ran."),
            (reopened.CardText("B", "CardNowHeading"), reopened.CardText("B", "CardNowText")));
        Assert.False(reopened.InCard<TextBlock>("B", "CardRunNote").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void An_expanded_card_reads_a_task_the_run_carried_as_its_conversation_does_and_says_once_what_to_run_first()
    {
        using var f = Join();
        var shell = f.Window();
        RunNode(shell, "B");
        RunNode(shell, "A");
        Expand(shell, "C", "B", "A");
        TaskNodeViewModel Node(string title) => (TaskNodeViewModel)shell.Node(title).DataContext!;

        // The run took B's result from the earlier run and never talked to B, so B's card reads B's own conversation, as
        // Open conversation does, and A's card reads the run's.
        Assert.Equal(("Result", "From an earlier run"), (shell.CardText("B", "CardNowHeading"), shell.CardText("B", "CardNowMeta")));
        Assert.Equal((null, true), (Node("B").Detail!.Route, Node("A").Detail!.Route is not null));
        Assert.Equal(("Result", "A ready."), (shell.CardText("A", "CardNowHeading"), shell.CardText("A", "CardNowText")));

        // New instructions for B put its result and C's out of date. C's card says why once: Run's refusal names what to
        // run first, so nothing repeats it under Run, nor in the peek.
        shell.Window.ViewModel.Canvas!.Edit(new WorkflowEdit.SetField(B, "instructions", "Build B again."));
        shell.Render();
        var runB = WorkflowRunText.Unbroken("Out of date because\u200B \"B\" changed. Run \"B\"\u00A0first.");
        Assert.Equal(("Out of date", runB), (shell.CardText("C", "CardNowHeading"), shell.CardText("C", "CardNowText")));
        Assert.False(shell.InCard<TextBlock>("C", "CardRunNote").IsEffectivelyVisible);
        // C's column stands right of two expanded cards now, under the inspector, so its own toggle collapses it.
        Node("C").ToggleExpandCommand.Execute(null);
        shell.Render();
        Assert.Single(shell.PeekTexts("C")!, text => text == runB);
    }

    [AvaloniaFact]
    public void An_expanded_card_whose_newest_run_ended_its_task_shows_that_end_and_never_an_older_result_of_its_own()
    {
        using var f = Join();
        // A failed and B succeeded on their own once. Then a run fails B and stops A while it runs.
        f.Answer("A", FakeRule.On().Print(FakeAgents.SessionLine(ClientId.Codex, "session-1")).Stderr("error: model not found").Exit(1),
                f.Says("A ready.", gate: "a"))
            .Answer("B", f.Says("B on its own."), FakeRule.On().Print(FakeAgents.SessionLine(ClientId.Codex, "session-1")).Exit(1));
        var shell = f.Window();
        shell.RunOnItsOwn("A");
        shell.RunOnItsOwn("B");
        shell.WaitUntil(() => shell.Window.ViewModel.Canvas!.Nodes.Where(node => node.Title is "A" or "B").Select(node => node.LastAttempt?.StatusLabel)
            .SequenceEqual(["Failed", "Succeeded"]), "A's and B's own runs end");
        var ownFailure = Assert.IsType<string>(shell.Window.ViewModel.Canvas!.Nodes.Single(node => node.Title == "A").LastAttempt!.Detail);
        Expand(shell, "C", "B", "A");
        shell.StartRun();
        shell.WaitUntil(() => shell.CardText("B", "CardStatus") == "Failed" && shell.CardText("A", "CardStatus") == "Running", "B fails while A runs",
            () => string.Join(", ", Cards(shell)));
        shell.Click(shell.Find<Button>("StopWorkflow"));
        shell.WaitUntil(() => shell.WorkflowRun is { IsActive: false, View.PinsReleased: true }, "the run stops", () => shell.RunStatus);
        shell.WaitUntil(() => shell.CardText("B", "CardNowHeading") == "Failed", "B's card reads the run's attempt",
            () => shell.CardText("B", "CardNowHeading"));
        AssertEnded(shell);
        shell.Window.Close();
        shell.Render();

        var reopened = f.Window();
        reopened.WaitUntil(() => reopened.CardText("B", "CardStatus") == "Failed", "the reopened window reads the stopped run",
            () => string.Join(", ", Cards(reopened)));
        Expand(reopened, "C", "B", "A");
        AssertEnded(reopened);
        // Between runs the card reads A's own conversation, whose newest attempt is older than the run that stopped A.
        Assert.Equal(("Cancelled", ((TaskNodeViewModel)reopened.Node("A").DataContext!).RunOwner),
            (reopened.CardText("A", "CardNowHeading"), reopened.CardText("A", "CardNowText")));
        Assert.DoesNotContain(ownFailure, reopened.CardText("A", "CardNowText"));

        static void AssertEnded(Shell shell)
        {
            Assert.Equal(("Failed", "Starts after"), (shell.CardText("B", "CardNowHeading"), shell.CardText("C", "CardNowHeading")));
            Assert.NotEqual("B on its own.", shell.CardText("B", "CardNowText"));
            Assert.NotEmpty(shell.CardText("B", "CardNowText"));
            Assert.Equal(["A", "B"], Shell.Texts(shell.InCard<ItemsControl>("C", "CardWaits")).Where(text => text is "A" or "B"));
        }
    }

    [AvaloniaFact]
    public void Run_Workflow_lists_no_earlier_result_and_runs_every_task_again()
    {
        using var f = Join();
        var shell = f.Window();
        RunNode(shell, "B");

        shell.OpenPreflight();

        Assert.Empty(shell.TextsOf("PreflightCarriedResult"));
        Start(shell, "Workflow");
        Assert.Equal("Completed", shell.RunStatus);
        Assert.Equal((1, 2, 1), (f.Launches("A"), f.Launches("B"), f.Launches("C")));
    }
}
