using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using IDevelop.Desktop.Execution;
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
    private static readonly TaskId D = new(Guid.Parse("019a9d2e-5d11-7a44-8c22-1f3e5a7b9d44"));

    /// <summary><c>A → C ← B</c>, with the two roots above each other and the join to their right, all in view.</summary>
    private static WorkflowRunFixture Join() =>
        new(Task(A, "A", 105), Below(B, "B"), At(C, "C", 405, 170), Dependency(A, C), Dependency(B, C));

    private static WorkflowEdit.PlaceNode Below(TaskId id, string title) => At(id, title, 105, 250);

    private static WorkflowEdit.PlaceNode Below2(TaskId id, string title) => At(id, title, 105, 410);

    private static WorkflowEdit.PlaceNode At(TaskId id, string title, double x, double y) =>
        AppTempFolder.TaskAt(id, title, x, y, WorkflowRunFixture.Codex, $"Build {title}.");

    /// <summary>What the inspector shows for <paramref name="text"/>: each quoted title kept whole.</summary>
    private static string Whole(string text) => WorkflowRunText.Unbroken(text);

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
        // The count covers the tasks the run reaches: A and the task after it, not C, which nobody ran.
        Assert.Equal(("Completed", "2 of 2 done", "Every task it ran has a current result."),
            (shell.RunStatus, shell.Text("RunProgress"), shell.Text("RunActivity")));
        Select(shell, "C");
        Assert.Equal(Whole("The last run of the \"Workflow\" workflow did not start this task."), shell.Text("RunOwner"));
        Select(shell, "B");
        Assert.Equal(Whole("A run of the \"Workflow\" workflow ran this task last."), shell.Text("RunOwner"));
        Assert.Equal([A], f.Record().Requested!);
    }

    [AvaloniaFact]
    public void A_node_after_another_task_keeps_Run_off_and_says_it_runs_after_it()
    {
        using var f = Join();
        var shell = f.Window();
        Select(shell, "C");

        var reason = Whole("Runs after \"A\" and \"B\". Run them first.");
        Assert.Equal(reason, shell.InView<TextBlock>("StartProblem").Text);
        var run = shell.InView<Button>("RunTask");
        Assert.Equal((true, false, reason), (run.IsEffectivelyVisible, run.IsEffectivelyEnabled, ToolTip.GetTip(run)));
        shell.Click(run);

        Assert.Equal("", shell.Status);
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
        var preflight = OpenNodePreflight(shell, "A");
        Assert.Equal(["A", "C"], preflight.Tasks);
        // The preview names a task it does not list by its title too.
        Assert.Equal(["After \"A\", \"B\""], shell.TextsOf("PreflightInputs"));
        shell.Click(shell.Find<Button>("PreflightStart"));
        shell.WaitForCard("A", "Running");

        Assert.Equal(["Running", "Not started", "Waits for 2 tasks"], new[] { "A", "B", "C" }.Select(title => shell.CardText(title, "CardStatus")));
        Select(shell, "C");
        Assert.Equal(Whole("It starts once \"A\" and \"B\" hand on a result. \"B\" runs only when you run\u00A0it."), shell.Text("RunTaskDetail"));
        Assert.False(shell.Find<Button>("RunTask").IsEffectivelyVisible);
        Select(shell, "B");
        Assert.Equal("Not started", shell.InView<TextBlock>("RunTaskStatus").Text);
        Assert.Equal(Whole("A run of the \"Workflow\" workflow is active. Run this task to add it to that run."), shell.Text("RunOwner"));
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
    public void The_card_menu_offers_Run_only_where_it_can_start_and_runs_the_node_as_its_Run_button_does()
    {
        using var f = Join();
        var shell = f.Window();

        shell.RightClick(shell.Header(shell.Node("C")));
        Assert.DoesNotContain("Run", shell.MenuHeaders());
        shell.Press(Avalonia.Input.Key.Escape);
        shell.RightClick(shell.Header(shell.Node("A")));
        Assert.Contains("Run", shell.MenuHeaders());
        shell.Press(Avalonia.Input.Key.Escape);

        shell.Window.ViewModel.Canvas!.Nodes.Single(node => node.Title == "A").RunCommand.Execute(null);
        shell.WaitUntil(() => shell.Preflight is { IsReady: true }, "A's preflight reads the project", () => $"Notice: {shell.Status}");
        Assert.Equal("Run \"A\"", shell.Preflight!.Heading);
        Assert.Empty(f.RunFolders());
    }

    [AvaloniaFact]
    public void A_node_run_that_completes_counts_its_own_tasks_and_says_what_they_still_wait_for()
    {
        using var f = new WorkflowRunFixture(Task(A, "A", 105), Below(B, "B"), At(C, "C", 405, 170), Below2(D, "D"), Dependency(A, C), Dependency(B, C));
        f.Answer("A", f.Says("A ready.")).Answer("B", f.Says("B ready.")).Answer("C", f.Says("C ready.")).Answer("D", f.Says("D ready."));
        var shell = f.Window();
        OpenNodePreflight(shell, "A");

        shell.Click(shell.Find<Button>("PreflightStart"));
        shell.WaitForStatus("Completed");

        // The run reaches A and C; C waits for B, which nobody ran, and D is not part of it.
        Assert.Equal(("Completed", "1 of 2 done", Whole("\"C\" waits for \"B\".")), (shell.RunStatus, shell.Text("RunProgress"), shell.Text("RunActivity")));
        Assert.Equal((1, 0, 0, 0), (f.Launches("A"), f.Launches("B"), f.Launches("C"), f.Launches("D")));
    }

    /// <summary>Starts a run of <paramref name="title"/> from its Run and waits until its card runs.</summary>
    private static void StartNodeRun(Shell shell, string title)
    {
        OpenNodePreflight(shell, title);
        shell.Click(shell.Find<Button>("PreflightStart"));
        shell.WaitForCard(title, "Running");
    }

    private static void Apply(Shell shell, WorkflowEdit edit)
    {
        Assert.IsType<EditResult.Applied>(shell.Window.ViewModel.Canvas!.Document.Apply(edit));
        shell.Render();
    }

    [AvaloniaFact]
    public void A_node_added_while_a_node_run_is_active_keeps_Run_off_until_the_run_finishes()
    {
        using var f = Join();
        f.Answer("A", f.Says("A ready.", gate: "a")).Answer("E", f.Says("E ready."));
        var shell = f.Window();
        StartNodeRun(shell, "A");

        Apply(shell, At(D, "E", 405, 330));
        Select(shell, "E");

        var reason = Whole("Added after this run started. Run it once the run finishes.");
        var run = shell.InView<Button>("RunTask");
        Assert.Equal((reason, false, reason), (shell.InView<TextBlock>("StartProblem").Text, run.IsEffectivelyEnabled, ToolTip.GetTip(run)));
        f.Open("a");
        shell.WaitForStatus("Completed");
        Assert.True(shell.InView<Button>("RunTask").IsEffectivelyEnabled);
        Assert.Equal(0, f.Launches("E"));
    }

    [AvaloniaFact]
    public void A_run_uses_its_tasks_as_they_were_when_it_started_and_will_not_add_one_that_could_not_start_then()
    {
        using var f = new WorkflowRunFixture(Task(A, "A", 105), AppTempFolder.TaskAt(B, "B", 105, 250, null, "Build B."), At(C, "C", 405, 170),
            Dependency(A, C), Dependency(B, C));
        f.Answer("A", f.Says("A ready.", gate: "a")).Answer("B", f.Says("B ready.")).Answer("C", f.Says("C ready."));
        var shell = f.Window();
        StartNodeRun(shell, "A");

        // B had no agent when the run started; the person gives it one now.
        Apply(shell, new WorkflowEdit.SetExecution(B, WorkflowRunFixture.Codex));
        Select(shell, "B");

        var reason = Whole("This run uses \"B\" as it was when the run started, and it had no agent then. Run it once the run finishes.");
        var run = shell.InView<Button>("RunTask");
        Assert.Equal((reason, false, reason), (shell.InView<TextBlock>("StartProblem").Text, run.IsEffectivelyEnabled, ToolTip.GetTip(run)));
        Assert.False(shell.Find<TextBlock>("RunOwner").IsEffectivelyVisible);
        Apply(shell, new WorkflowEdit.SetField(A, "instructions", "Build A again."));
        Select(shell, "A");
        Assert.Equal(Whole("A run of the \"Workflow\" workflow owns this task. Talk to it through its conversation, or stop the run. " +
            "The run uses this task as it was when the run started."), shell.Text("RunOwner"));
        f.Open("a");
        shell.WaitForStatus("Completed");
        Assert.Equal((1, 0, 0), (f.Launches("A"), f.Launches("B"), f.Launches("C")));
    }

    [AvaloniaFact]
    public void A_task_after_a_root_nobody_ran_shows_only_why_its_Run_is_off_and_a_waiting_task_has_not_started()
    {
        using var f = new WorkflowRunFixture(Task(A, "A", 105), Below(B, "B"), At(C, "C", 405, 170), Below2(D, "D"),
            Dependency(A, C), Dependency(B, C), Dependency(B, D));
        f.Answer("A", f.Says("A ready.", gate: "a"));
        var shell = f.Window();
        StartNodeRun(shell, "A");

        Select(shell, "D");
        Assert.Equal(Whole("Runs after \"B\". Run \"B\" first."), shell.InView<TextBlock>("StartProblem").Text);
        Assert.False(shell.Find<TextBlock>("RunOwner").IsEffectivelyVisible);
        Select(shell, "C");
        Assert.Equal(Whole("A run of the \"Workflow\" workflow owns this task. It has not started it yet."), shell.Text("RunOwner"));
        f.Open("a");
        shell.WaitForStatus("Completed");
    }

    [AvaloniaFact]
    public void A_Run_that_meets_a_run_that_just_completed_opens_a_new_run_s_preflight()
    {
        using var f = Join();
        f.Answer("A", f.Says("A ready.")).Answer("B", f.Says("B ready."));
        var shell = f.Window();
        StartNodeRun(shell, "A");
        shell.WaitForStatus("Completed");
        var canvas = shell.Window.ViewModel.Canvas!;

        // As a click on B's Run whose request reaches the run just after it completed.
        canvas.JoinRunAsync(shell.WorkflowRun!, canvas.Nodes.Single(node => node.Title == "B")).Wait(TimeSpan.FromSeconds(30));
        shell.WaitUntil(() => shell.Preflight is { IsReady: true }, "B's preflight reads the project", () => $"Notice: {shell.Status}");

        Assert.Equal(("Run \"B\"", ""), (shell.Preflight!.Heading, shell.Status));
    }

    [AvaloniaFact]
    public void A_task_s_own_later_attempt_shows_again_in_place_of_the_settled_run_s_state()
    {
        using var f = new WorkflowRunFixture(Task(A, "A", 105), Task(B, "B", 405), Dependency(A, B));
        f.Answer("A", f.Says("A ready."), f.Says("A again.")).Answer("B", f.Says("B ready."));
        var shell = f.Window();
        StartNodeRun(shell, "A");
        shell.WaitForStatus("Completed");
        Select(shell, "A");
        Assert.Equal("Succeeded", shell.InView<TextBlock>("RunTaskStatus").Text);

        shell.RunOnItsOwn("A");

        shell.WaitUntil(() => shell.Has<TextBlock>("LastRunStatus") && shell.Find<TextBlock>("LastRunStatus").Text == "Succeeded", "A's own run succeeds");
        Assert.False(shell.Find<TextBlock>("RunTaskStatus").IsEffectivelyVisible);
        Assert.Equal(("Succeeded", "Succeeded"), (shell.CardText("A", "CardStatus"), shell.CardText("B", "CardStatus")));
        Assert.Equal(2, f.Launches("A"));
    }

    [AvaloniaFact]
    public void A_conversation_of_its_own_that_waits_stays_counted_and_cancellable_after_a_node_run_settles()
    {
        using var f = new WorkflowRunFixture(Task(A, "A", 105, ConversationMode.Chat), Below(B, "B"));
        f.Answer("A", f.Says("Here is a plan.")).Answer("B", f.Says("B ready."));
        var shell = f.Window();
        Select(shell, "A");
        shell.RunOnItsOwn("A");
        shell.WaitForCard("A", "Waiting for you");
        Assert.Equal(1, shell.Window.ViewModel.Canvas!.WaitingCount);

        StartNodeRun(shell, "B");
        shell.WaitForStatus("Completed");

        Assert.Equal(("Waiting for you", 1), (shell.CardText("A", "CardStatus"), shell.Window.ViewModel.Canvas!.WaitingCount));
        Select(shell, "A");
        Assert.True(shell.InView<Button>("CancelRun").IsEffectivelyEnabled);
        Assert.Equal((1, 1), (f.Launches("A"), f.Launches("B")));
    }

    [AvaloniaFact]
    public void A_node_s_preview_lists_a_task_s_inputs_in_title_order()
    {
        using var f = new WorkflowRunFixture(Task(A, "Zed", 105), Below(B, "Alpha"), At(C, "C", 405, 170), Dependency(A, C), Dependency(B, C));
        var shell = f.Window();

        OpenNodePreflight(shell, "Zed");

        Assert.Equal(["After \"Alpha\", \"Zed\""], shell.TextsOf("PreflightInputs"));
    }

    [AvaloniaFact]
    public void A_card_names_the_task_it_waits_for_when_the_name_fits_and_counts_it_when_it_does_not()
    {
        const string longTitle = "Render the settings view for every user";
        using var f = new WorkflowRunFixture(Task(A, longTitle, 105), Below(B, "B"), At(C, "C", 405, 90), At(D, "D", 405, 250),
            Dependency(A, C), Dependency(B, D));
        f.Answer(longTitle, f.Says("Rendered.", gate: "go")).Answer("B", f.Says("B ready.", gate: "go")).Answer("C", f.Says("C ready.")).Answer("D", f.Says("D ready."));
        var shell = f.Window();

        shell.StartRun();
        shell.WaitForCard("B", "Running");

        Assert.Equal(("Waits for 1 task", "Waits for \"B\""), (shell.CardText("C", "CardStatus"), shell.CardText("D", "CardStatus")));
        Assert.Contains($"It starts once \"{longTitle}\" hands on a result.", (string?)ToolTip.GetTip(shell.InCard<Panel>("C", "TaskCard")));
        string[] cut = [.. shell.Nodes().SelectMany(node => node.GetVisualDescendants().OfType<TextBlock>())
            .Where(text => text.IsEffectivelyVisible && (text.TextLayout.TextLines.Any(line => line.HasCollapsed) || text.TextLayout.Width > text.Bounds.Width + 0.5))
            .Select(text => text.Text ?? "")];
        Assert.Equal([longTitle], cut);
        f.Open("go");
        shell.WaitForStatus("Completed");
    }
}
