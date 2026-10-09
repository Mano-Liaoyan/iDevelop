using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
        Assert.Equal(WorkflowRunText.Unbroken("It starts once \"A\" hands on a result. \"A\" runs only when you run it."), shell.Text("RunTaskDetail"));
        Assert.Equal(WorkflowRunText.Unbroken("Runs after \"A\". Run \"A\" first."), shell.InView<TextBlock>("StartProblem").Text);
        shell.Window.Close();
        shell.Render();

        var reopened = f.Window();
        reopened.WaitUntil(() => reopened.CardText("B", "CardStatus") == "Succeeded", "the reopened window reads the earlier run",
            () => string.Join(", ", Cards(reopened)));
        Assert.Null(reopened.WorkflowRun);
        Assert.Equal([agent, "Succeeded", "Waits for \"A\""], Cards(reopened));
        Select(reopened, "B");
        Assert.Equal("Succeeded", reopened.InView<TextBlock>("RunTaskStatus").Text);
        Assert.Equal(WorkflowRunText.Unbroken("Its result from the last run of the \"Workflow\" workflow still counts."), reopened.Text("RunOwner"));
        Assert.Equal(WorkflowRunText.Unbroken("When you run a task after it, the run uses this result instead of running it again."), reopened.Text("RunTaskDetail"));
        Select(reopened, "C");
        Assert.Equal(WorkflowRunText.Unbroken("Runs after \"A\". Run \"A\" first."), reopened.InView<TextBlock>("StartProblem").Text);
    }

    [AvaloniaFact]
    public void A_node_s_Run_uses_an_earlier_result_its_preflight_lists_and_an_edit_puts_that_result_out_of_date()
    {
        using var f = Join();
        var shell = f.Window();
        RunNode(shell, "B");

        OpenPreflight(shell, "A");

        Assert.Equal(["Uses \"B\"'s result from an earlier run."], shell.TextsOf("PreflightCarriedResult"));
        Assert.Equal(["A", "C"], shell.TextsOf("PreflightTask"));
        Start(shell, "A");
        Assert.Equal("Completed", shell.RunStatus);
        Assert.Equal((1, 1, 1), (f.Launches("A"), f.Launches("B"), f.Launches("C")));
        Assert.Equal(["Succeeded", "Succeeded", "Succeeded"], Cards(shell));
        Select(shell, "B");
        Assert.Equal("Succeeded", shell.InView<TextBlock>("RunTaskStatus").Text);

        // A new model for B is a new definition: its result no longer counts, nor does C's, which used it.
        var canvas = shell.Window.ViewModel.Canvas!;
        canvas.Edit(new WorkflowEdit.SetExecution(B, WorkflowRunFixture.Codex with { Model = "gpt-5.4" }));
        shell.Render();

        Assert.Equal(["Succeeded", "Out of date", "Out of date"], Cards(shell));
        Assert.Equal("Out of date", shell.InView<TextBlock>("RunTaskStatus").Text);
        Assert.Equal(WorkflowRunText.Unbroken("It changed since it ran, or so did the connections into it. Run it again to bring it up to date."), shell.Text("RunTaskDetail"));
        Select(shell, "C");
        Assert.Equal(WorkflowRunText.Unbroken("The result of \"B\" that it used is out of date. Run it again to bring it up to date."), shell.Text("RunTaskDetail"));
        Assert.Equal(WorkflowRunText.Unbroken("Runs after \"B\". Run \"B\" first."), shell.InView<TextBlock>("StartProblem").Text);
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

        Assert.Equal(["Can't use \"B\"'s result from an earlier run: its code conflicts with this base in out-b.txt. The tasks after it wait until it runs again."],
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
        f.Open("d");
        shell.WaitForStatus("Completed");
        Assert.Equal((1, 1, 1, 1), (f.Launches("A"), f.Launches("B"), f.Launches("C"), f.Launches("D")));
        Assert.Null(shell.Preflight);
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
