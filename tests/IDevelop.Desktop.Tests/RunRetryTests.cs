using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using IDevelop.Desktop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.WorkflowRunFixture;

namespace IDevelop.Desktop.Tests;

/// <summary>
/// Retry of a run's task that ended without a result (#123): the card, its menu, and the inspector offer it, the
/// inspector asks first, and the confirmed retry runs the task again and the run goes on.
/// </summary>
[Collection(ProcessCollection.Name)]
public sealed class RunRetryTests
{
    private static readonly TaskId A = TestTasks.Design;
    private static readonly TaskId B = TestTasks.Build;

    private static FakeRule Fails() => FakeRule.On().Print(FakeAgents.SessionLine(ClientId.Codex, "session-1")).Write("half.txt", "half\n").Exit(1);

    /// <summary><c>A → B</c>, whose A fails its first attempt and succeeds its second.</summary>
    private static WorkflowRunFixture Chain()
    {
        var f = new WorkflowRunFixture(Task(A, "A", 105), Task(B, "B", 405), Dependency(A, B));
        f.Answer("A", Fails(), f.Writes("out-a.txt", "A\n", "A ready.")).Answer("B", f.Says("B ready."));
        return f;
    }

    private static Shell Failed(WorkflowRunFixture f)
    {
        var shell = f.Window();
        shell.StartRun();
        shell.WaitForStatus("Needs attention");
        shell.WaitForCard("A", "Failed");
        return shell;
    }

    private static void Select(Shell shell, string title) => shell.Click(shell.Header(shell.Node(title)));

    private static bool Shows(Shell shell, string automationId) => shell.ShowsAny(automationId);

    [AvaloniaFact]
    public void The_inspector_asks_before_it_retries_a_failed_task_and_the_run_completes()
    {
        using var f = Chain();
        var shell = Failed(f);
        Select(shell, "A");
        Assert.False(Shows(shell, "RetryConfirmation"));

        shell.Click(shell.InView<Button>("RetryTask"));

        Assert.Equal("Retry runs \"A\" again in a fresh session. Its unfinished work is saved first, and its checkout goes back to where its " +
            "last attempt started. The tasks after it run once it has a result.", shell.Text("RetryConfirmation"));
        Assert.False(Shows(shell, "RetryTask"));
        Assert.Equal(1, f.Launches("A"));

        shell.Click(shell.InView<Button>("ConfirmRetry"));

        shell.WaitForStatus("Completed");
        Assert.Equal((2, 1), (f.Launches("A"), f.Launches("B")));
        Assert.Equal("Succeeded", shell.CardText("A", "CardStatus"));
        Assert.False(Shows(shell, "RetryTask"));
        Assert.False(Shows(shell, "RetryConfirmation"));
    }

    [AvaloniaFact]
    public void Not_now_closes_the_confirmation_and_starts_nothing()
    {
        using var f = Chain();
        var shell = Failed(f);
        Select(shell, "A");
        shell.Click(shell.InView<Button>("RetryTask"));

        shell.Click(shell.InView<Button>("KeepRetry"));

        Assert.False(Shows(shell, "RetryConfirmation"));
        Assert.True(Shows(shell, "RetryTask"));
        Assert.Equal("Needs attention", shell.RunStatus);
        Assert.Equal(1, f.Launches("A"));
    }

    [AvaloniaFact]
    public void The_expanded_card_s_Retry_opens_the_confirmation_in_the_inspector()
    {
        using var f = Chain();
        var shell = Failed(f);
        shell.Click(shell.InCard<ToggleButton>("A", "CardExpand"));
        Assert.False(Shows(shell, "CardRun"));

        shell.Click(shell.InCard<Button>("A", "CardRetry"));

        Assert.Equal("A", shell.Window.ViewModel.Canvas!.SelectedNode?.Title);
        Assert.True(Shows(shell, "RetryConfirmation"));
        Assert.Equal(1, f.Launches("A"));
    }

    [AvaloniaFact]
    public void The_card_menu_offers_Retry_only_for_the_failed_task()
    {
        using var f = Chain();
        var shell = Failed(f);
        shell.RightClick(shell.Header(shell.Node("B")));
        Assert.DoesNotContain("Retry…", shell.MenuHeaders());
        shell.Press(Key.Escape);

        shell.RightClick(shell.Header(shell.Node("A")));
        Assert.Contains("Retry…", shell.MenuHeaders());
        shell.Click(shell.MenuItem("NodeMenuRetry"));

        Assert.Equal("A", shell.Window.ViewModel.Canvas!.SelectedNode?.Title);
        Assert.True(Shows(shell, "RetryConfirmation"));
    }

    [AvaloniaFact]
    public void A_task_that_failed_on_its_own_offers_no_Retry()
    {
        using var f = new WorkflowRunFixture(Task(A, "A", 105));
        f.Answer("A", Fails());
        var shell = f.Window();
        shell.RunOnItsOwn("A");
        shell.WaitForCard("A", "Failed");
        Select(shell, "A");

        Assert.False(Shows(shell, "RetryTask"));
        Assert.False(Shows(shell, "CardRetry"));
    }
}
