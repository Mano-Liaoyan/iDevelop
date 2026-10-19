using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.WorkflowRunFixture;

namespace IDevelop.Desktop.Tests;

/// <summary>
/// Cancel of one task in a workflow run (#124): the expanded card, its menu, and the inspector offer it while the task's
/// attempt runs or waits. The task shows Cancelled with Retry, the tasks after it wait, the others go on, and the run
/// needs attention until Retry or Stop Workflow.
/// </summary>
[Collection(ProcessCollection.Name)]
public sealed class RunCancelTests
{
    private static readonly TaskId Plan = TestTasks.Design;
    private static readonly TaskId Implement = TestTasks.Build;
    private static readonly TaskId Review = TestTasks.Review;
    private static readonly TaskId Docs = new(Guid.Parse("019a9d2e-5d00-7000-8000-000000000124"));

    /// <summary>
    /// <c>Plan → Implement → Review</c> beside a separate Docs. Implement's first attempt runs until it is cancelled, and Docs
    /// waits for its gate.
    /// </summary>
    private static WorkflowRunFixture Example()
    {
        var f = new WorkflowRunFixture(Task(Plan, "Plan", 105), Task(Implement, "Implement", 405), Task(Review, "Review", 705),
            AppTempFolder.TaskAt(Docs, "Docs", 405, 330, Codex, "Build Docs."), Dependency(Plan, Implement), Dependency(Implement, Review));
        f.Answer("Plan", f.Says("Plan ready."))
            .Answer("Implement", f.Says("never", gate: "implement"), f.Writes("impl.txt", "impl\n", "Implement ready."))
            .Answer("Review", f.Says("Review ready."))
            .Answer("Docs", f.Says("Docs ready.", gate: "docs"));
        return f;
    }

    private static Shell Running(WorkflowRunFixture f)
    {
        var shell = f.Window();
        shell.StartRun();
        shell.WaitForCard("Implement", "Running");
        return shell;
    }

    private static void Select(Shell shell, string title) => shell.Click(shell.Header(shell.Node(title)));

    private static bool Shows(Shell shell, string automationId) => shell.ShowsAny(automationId);

    [AvaloniaFact]
    public void The_expanded_card_s_Cancel_ends_the_task_and_the_rest_of_the_run_goes_on_until_Retry()
    {
        using var f = Example();
        var shell = Running(f);
        shell.Click(shell.InCard<ToggleButton>("Implement", "CardExpand"));
        Assert.False(Shows(shell, "CardStop"));

        shell.Click(shell.InCard<Button>("Implement", "CardCancel"));

        shell.WaitForCard("Implement", "Cancelled");
        Assert.Equal("Waits for \"Implement\"", shell.CardText("Review", "CardStatus"));
        Assert.Equal("Running", shell.CardText("Docs", "CardStatus"));
        Assert.False(Shows(shell, "CardCancel"));
        Assert.True(Shows(shell, "CardRetry"));

        f.Open("docs");
        shell.WaitForCard("Docs", "Succeeded");
        shell.WaitForStatus("Needs attention");
        Assert.Equal(0, f.Launches("Review"));

        shell.Click(shell.InCard<Button>("Implement", "CardRetry"));
        shell.Click(shell.InView<Button>("ConfirmRetry"));

        shell.WaitForStatus("Completed");
        Assert.Equal((2, 1, 1), (f.Launches("Implement"), f.Launches("Review"), f.Launches("Docs")));
    }

    [AvaloniaFact]
    public void The_inspector_cancels_a_waiting_task()
    {
        using var f = new WorkflowRunFixture(Task(Plan, "Plan", 105, ConversationMode.Chat), Task(Implement, "Implement", 405),
            Dependency(Plan, Implement));
        f.Answer("Plan", f.Says("Which parser?")).Answer("Implement", f.Says("Implement ready."));
        var shell = f.Window();
        shell.StartRun();
        shell.WaitForCard("Plan", "Waiting for you");
        Select(shell, "Plan");

        shell.Click(shell.InView<Button>("CancelRunTask"));

        shell.WaitForCard("Plan", "Cancelled");
        shell.WaitForStatus("Needs attention");
        Assert.False(Shows(shell, "CancelRunTask"));
        Assert.True(Shows(shell, "RetryTask"));
        Assert.Equal(0, f.Launches("Implement"));
    }

    [AvaloniaFact]
    public void The_card_menu_offers_Cancel_only_for_the_running_task()
    {
        using var f = Example();
        var shell = Running(f);
        shell.RightClick(shell.Header(shell.Node("Review")));
        Assert.DoesNotContain("Cancel", shell.MenuHeaders());
        shell.Press(Key.Escape);

        shell.RightClick(shell.Header(shell.Node("Implement")));
        Assert.Contains("Cancel", shell.MenuHeaders());
        Assert.DoesNotContain("Cancel Run", shell.MenuHeaders());
        shell.Click(shell.MenuItem("NodeMenuCancelInRun"));

        shell.WaitForCard("Implement", "Cancelled");
        f.Open("docs");
        shell.WaitForStatus("Needs attention");
    }

    [AvaloniaFact]
    public void A_task_that_runs_on_its_own_offers_Stop_and_no_run_Cancel()
    {
        using var f = new WorkflowRunFixture(Task(Plan, "Plan", 105));
        f.Answer("Plan", f.Says("never", gate: "plan"));
        var shell = f.Window();
        shell.RunOnItsOwn("Plan");
        shell.WaitForCard("Plan", "Running");
        shell.Click(shell.InCard<ToggleButton>("Plan", "CardExpand"));
        Select(shell, "Plan");

        Assert.True(Shows(shell, "CardStop"));
        Assert.False(Shows(shell, "CardCancel"));
        Assert.False(Shows(shell, "CancelRunTask"));
        f.Open("plan");
    }
}
