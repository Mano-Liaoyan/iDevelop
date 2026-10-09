using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.WorkflowRunFixture;

namespace IDevelop.Desktop.Tests;

/// <summary>
/// A node's Run (#90): it runs that node in a workflow run, through the same preflight as Run Workflow, and each task after
/// it starts by itself once all of its predecessors have results. A node the person runs while the run is active joins
/// it, and a node whose predecessors have no results starts nothing and says what it runs after.
/// </summary>
[Collection(ProcessCollection.Name)]
public sealed class NodeRunTests
{
    private static readonly TaskId A = TestTasks.Design;
    private static readonly TaskId B = TestTasks.Build;
    private static readonly TaskId C = TestTasks.Review;

    /// <summary><c>A → C ← B</c>, with the two roots above each other and the join to their right, all in view.</summary>
    private static WorkflowRunFixture Join() =>
        new(Task(A, "A", 105), Below(B, "B"), At(C, "C", 405, 170), Dependency(A, C), Dependency(B, C));

    private static WorkflowEdit.PlaceNode Below(TaskId id, string title) => At(id, title, 105, 250);

    private static WorkflowEdit.PlaceNode At(TaskId id, string title, double x, double y) =>
        AppTempFolder.TaskAt(id, title, x, y, WorkflowRunFixture.Codex, $"Build {title}.");

    private static void Select(Shell shell, string title) => shell.Click(shell.Header(shell.Node(title)));

    /// <summary>Clicks the inspector's Run of <paramref name="title"/> and waits until the node's preflight has read the project.</summary>
    private static RunPreflightView OpenNodePreflight(Shell shell, string title)
    {
        Select(shell, title);
        shell.Click(shell.InView<Button>("RunTask"));
        shell.WaitUntil(() => shell.Preflight is { IsReady: true }, "the node's preflight reads the project", () => $"Notice: {shell.Status}");
        return new(shell.Preflight!.Heading, shell.TextsOf("PreflightTask"));
    }

    private sealed record RunPreflightView(string Heading, string[] Tasks);

    [AvaloniaFact]
    public void A_node_s_Run_opens_its_preflight_and_runs_only_that_node_and_the_tasks_after_it()
    {
        using var f = new WorkflowRunFixture(Task(A, "A", 105), Task(B, "B", 405), Below(C, "C"), Dependency(A, B));
        f.Answer("A", f.Says("A ready.")).Answer("B", f.Says("B ready.")).Answer("C", f.Says("C ready."));
        var shell = f.Window();

        var preflight = OpenNodePreflight(shell, "A");

        Assert.Equal("Run \"A\"", preflight.Heading);
        Assert.Equal(["A", "B"], preflight.Tasks);
        Assert.Equal("Run \"A\"", shell.Text("PreflightTitle"));
        Assert.Empty(f.RunFolders());
        shell.Click(shell.Find<Button>("PreflightStart"));
        shell.WaitForStatus("Completed");

        Assert.Equal((1, 1, 0), (f.Launches("A"), f.Launches("B"), f.Launches("C")));
        Assert.Equal(["Succeeded", "Succeeded", "Not started"], new[] { "A", "B", "C" }.Select(title => shell.CardText(title, "CardStatus")));
        Assert.Equal(("Completed", "2 of 3 done", "Every task it ran has a current result."),
            (shell.RunStatus, shell.Text("RunProgress"), shell.Text("RunActivity")));
        Assert.Equal([A], f.Record().Requested!);
    }

    [AvaloniaFact]
    public void A_node_after_another_task_says_it_runs_after_it_and_starts_nothing()
    {
        using var f = Join();
        var shell = f.Window();
        Select(shell, "C");

        Assert.Equal("Runs after \"A\" and \"B\". Run them first.", shell.InView<TextBlock>("StartProblem").Text);
        shell.Click(shell.InView<Button>("RunTask"));

        Assert.Equal("Runs after \"A\" and \"B\". Run them first.", shell.Status);
        Assert.Null(shell.Preflight);
        Assert.Empty(f.RunFolders());
        Assert.Equal(0, f.Launches("C"));
    }

    [AvaloniaFact]
    public void A_node_run_while_the_run_is_active_joins_it_and_the_join_starts_once_both_have_results()
    {
        using var f = Join();
        f.Answer("A", f.Says("A ready.", gate: "a")).Answer("B", f.Says("B ready.", gate: "b")).Answer("C", f.Says("C ready."));
        var shell = f.Window();
        OpenNodePreflight(shell, "A");
        shell.Click(shell.Find<Button>("PreflightStart"));
        shell.WaitForCard("A", "Running");

        Assert.Equal(["Running", "Not started", "Waits for 2 tasks"], new[] { "A", "B", "C" }.Select(title => shell.CardText(title, "CardStatus")));
        Select(shell, "C");
        Assert.Equal("It starts once \"A\" and \"B\" hand on a result. \"B\" runs only when you run it.", shell.Text("RunTaskDetail"));
        Assert.False(shell.Find<Button>("RunTask").IsEffectivelyVisible);
        Select(shell, "B");
        Assert.Equal("Not started", shell.InView<TextBlock>("RunTaskStatus").Text);
        Assert.Equal("A run of the \"Workflow\" workflow is active. Run adds this task to it.", shell.Text("RunOwner"));
        Assert.True(shell.InView<Button>("RunTask").IsEffectivelyEnabled);
        Assert.False(shell.Find<Button>("CancelRun").IsEffectivelyVisible);

        shell.Click(shell.InView<Button>("RunTask"));

        shell.WaitForCard("B", "Running");
        Assert.Null(shell.Preflight);
        Assert.Equal("Waits for 2 tasks", shell.CardText("C", "CardStatus"));
        f.Open("b");
        shell.WaitForCard("B", "Succeeded");
        Assert.Equal("Waits for \"A\"", shell.CardText("C", "CardStatus"));
        Assert.Equal(0, f.Launches("C"));
        f.Open("a");
        shell.WaitForStatus("Completed");

        Assert.Equal((1, 1, 1), (f.Launches("A"), f.Launches("B"), f.Launches("C")));
        Assert.Equal(["Succeeded", "Succeeded", "Succeeded"], new[] { "A", "B", "C" }.Select(title => shell.CardText(title, "CardStatus")));
        Assert.Equal("Every task has a current result.", shell.Text("RunActivity"));
        Assert.Single(f.RunFolders());
        Assert.Equal([A, B], f.Record().Requested!);
    }

    [AvaloniaFact]
    public void The_card_menu_and_the_shortcut_run_a_node_as_its_Run_button_does()
    {
        using var f = Join();
        var shell = f.Window();
        Select(shell, "C");

        shell.Window.ViewModel.Canvas!.Nodes.Single(node => node.Title == "C").RunCommand.Execute(null);
        Assert.Equal("Runs after \"A\" and \"B\". Run them first.", shell.Status);

        shell.Window.ViewModel.Canvas!.Nodes.Single(node => node.Title == "A").RunCommand.Execute(null);
        shell.WaitUntil(() => shell.Preflight is { IsReady: true }, "A's preflight reads the project", () => $"Notice: {shell.Status}");
        Assert.Equal("Run \"A\"", shell.Preflight!.Heading);
        Assert.Empty(f.RunFolders());
    }
}
