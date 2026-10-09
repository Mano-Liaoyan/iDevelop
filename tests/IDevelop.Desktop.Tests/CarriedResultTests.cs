using Avalonia.Controls;
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
        Assert.Equal(WorkflowRunText.Unbroken("Runs after \"A\". Run \"A\" first."), shell.InView<TextBlock>("StartProblem").Text);
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
        Assert.Equal(WorkflowRunText.Unbroken("Runs after \"A\". Run \"A\" first."), reopened.InView<TextBlock>("StartProblem").Text);
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
            Assert.Contains($"Waits for \"{longTitle}\".", (string?)ToolTip.GetTip(shell.InCard<Panel>("C", "TaskCard")));
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
        Assert.Equal(WorkflowRunText.Unbroken("Out of date because \"B\" changed. Run \"B\"\u00A0first."), shell.InView<TextBlock>("StartProblem").Text);
    }

    [AvaloniaFact]
    public void A_node_s_preflight_says_why_it_cannot_use_an_earlier_result_whose_code_conflicts_with_the_base()
    {
        using var f = Join();
        var shell = f.Window();
        RunNode(shell, "B");
        File.WriteAllText(Path.Combine(f.Project, "out-b.txt"), "other\n");
        f.Run("add", "out-b.txt");
        f.Run("-c", "commit.gpgSign=false", "commit", "-q", "-m", "Conflict with B");

        OpenPreflight(shell, "A");

        Assert.Equal([WorkflowRunText.Unbroken("Can't use \"B\"'s result from an earlier run: its code conflicts with this base in out-b.txt. The tasks after it wait until it runs\u00A0again.")],
            shell.TextsOf("PreflightCarriedResult"));
        Start(shell, "A");
        Assert.Equal((1, 1, 0), (f.Launches("A"), f.Launches("B"), f.Launches("C")));
        Assert.Equal("Waits for \"B\"", shell.CardText("C", "CardStatus"));
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
        shell.Render();

        Assert.Equal("Succeeded", shell.CardText("B", "CardStatus"));
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
        Assert.Equal(WorkflowRunText.Unbroken("Runs after \"A\". Run \"A\" first."), shell.InView<TextBlock>("StartProblem").Text);
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
