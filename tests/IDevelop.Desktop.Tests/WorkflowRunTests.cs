using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.AppTempFolder;
using static IDevelop.Desktop.Tests.WorkflowRunFixture;

namespace IDevelop.Desktop.Tests;

/// <summary>Run Workflow from the toolbar: the preflight, the run's bar, the cards it owns, Stop Workflow, and Resume.</summary>
[Collection(ProcessCollection.Name)]
public sealed class WorkflowRunTests
{
    private static readonly TaskId A = TestTasks.Design;
    private static readonly TaskId B = TestTasks.Build;

    private static WorkflowRunFixture Chain() => new(Task(A, "A", 105), Task(B, "B", 405), Dependency(A, B));

    [AvaloniaFact]
    public void Run_Workflow_opens_a_preflight_of_the_whole_workflow_without_a_selection_and_Cancel_starts_nothing()
    {
        using var f = Chain();
        var shell = f.Window();
        Assert.Null(shell.Window.ViewModel.Canvas!.SelectedNode);

        var preflight = shell.OpenPreflight();

        Assert.True(shell.Find<Control>("PreflightSheet").IsEffectivelyVisible);
        Assert.Equal(["A", "B"], shell.TextsOf("PreflightTask"));
        Assert.Equal(["Codex · GPT-5.5 · high", "Codex · GPT-5.5 · high"], shell.TextsOf("PreflightAgent"));
        Assert.Equal(["After \"A\""], shell.TextsOf("PreflightInputs"));
        Assert.Equal($"HEAD, {f.Head()} on main", shell.Text("PreflightBase"));
        Assert.False(preflight.OffersSnapshot);
        Assert.Equal("Ignored files never reach a task.", shell.Text("PreflightIgnored"));
        Assert.Equal("Each task works in its own checkout under .worktrees/, on a branch under idp/. Your branch, index, and files stay as they are.",
            shell.Text("PreflightIsolation"));
        Assert.True(shell.Find<Button>("PreflightStart").IsEffectivelyEnabled);

        shell.Click(shell.Find<Button>("PreflightCancel"));

        Assert.Null(shell.Preflight);
        Assert.False(shell.Has<Control>("PreflightSheet"));
        Assert.Empty(f.RunFolders());
        Assert.Equal((0, 0), (f.Launches("A"), f.Launches("B")));
    }

    [AvaloniaFact]
    public void A_task_the_run_cannot_start_keeps_Start_off_and_Show_selects_it()
    {
        using var f = new WorkflowRunFixture(Task(A, "A", 105), TaskAt(B, "B", 405, 90, null, "Build B."), Dependency(A, B));
        var shell = f.Window();

        shell.OpenPreflight();

        Assert.Equal(["\"B\": Choose an agent for this task first."], shell.TextsOf("PreflightGap"));
        Assert.False(shell.Find<Button>("PreflightStart").IsEffectivelyEnabled);
        shell.Click(shell.Find<Button>("PreflightShowNode"));
        Assert.Null(shell.Preflight);
        Assert.Equal("B", shell.Window.ViewModel.Canvas!.SelectedNode?.Title);
    }

    [AvaloniaFact]
    public void An_empty_workflow_says_why_it_cannot_run()
    {
        using var f = new WorkflowRunFixture();
        var shell = f.Window();

        shell.OpenPreflight();

        Assert.Equal(["The workflow has no tasks yet. Add a node first."], shell.TextsOf("PreflightGap"));
        Assert.False(shell.Find<Button>("PreflightStart").IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void Uncommitted_work_offers_HEAD_or_a_snapshot_and_the_run_starts_from_the_snapshot_it_chose()
    {
        using var f = Chain();
        f.Answer("A", f.Says("A ready.")).Answer("B", f.Says("B ready."));
        File.WriteAllText(Path.Combine(f.Project, "base.txt"), "changed\n");
        var shell = f.Window();

        var preflight = shell.OpenPreflight();

        Assert.True(preflight.OffersSnapshot);
        Assert.Equal($"HEAD, {f.Head()} on main", ((RadioButton)shell.Find<RadioButton>("PreflightHead")).Content);
        Assert.Equal("1 path differs from HEAD: base.txt. A snapshot takes them along without changing your branch, index, or files.",
            shell.Text("PreflightChanged"));
        shell.Click(shell.Find<RadioButton>("PreflightSnapshot"));
        shell.Click(shell.Find<Button>("PreflightStart"));
        shell.WaitUntil(() => shell.WorkflowRun is not null, "the run starts");
        shell.WaitForStatus("Completed");

        var run = shell.WorkflowRun!;
        var record = Assert.IsType<RunRead.Loaded>(RunStore.Open(f.Project).Read(run.Workflow, run.Address.Run)).Record;
        Assert.Equal(BaseChoice.Snapshot, record.Base.Choice);
        Assert.Equal("changed\n", File.ReadAllText(Path.Combine(f.Project, "base.txt")));
        Assert.Equal(" M base.txt\n", f.Run("status", "--porcelain", "--untracked-files=no"));
    }

    [AvaloniaFact]
    public void A_started_run_shows_on_its_bar_and_cards_until_it_completes()
    {
        using var f = Chain();
        f.Answer("A", f.Says("A ready.", gate: "a")).Answer("B", f.Says("B ready."));
        var shell = f.Window();

        shell.StartRun();

        shell.WaitForCard("A", "Running");
        Assert.True(shell.Find<Control>("WorkflowRunBar").IsEffectivelyVisible);
        Assert.Equal(("Running", "0 of 2 done", "Running \"A\""), (shell.RunStatus, shell.Text("RunProgress"), shell.Text("RunActivity")));
        Assert.Equal("Waits for \"A\"", shell.CardText("B", "CardStatus"));
        Assert.False(shell.Find<Button>("RunWorkflow").IsEffectivelyVisible);
        Assert.True(shell.Find<Button>("StopWorkflow").IsEffectivelyVisible);
        Assert.True(shell.WorkflowShows("seed", "Workflow", "WorkflowRunning"));

        f.Open("a");
        shell.WaitForStatus("Completed");

        Assert.Equal(["Succeeded", "Succeeded"], new[] { "A", "B" }.Select(title => shell.CardText(title, "CardStatus")));
        Assert.Equal(("Completed", "2 of 2 done"), (shell.RunStatus, shell.Text("RunProgress")));
        Assert.Equal((1, 1), (f.Launches("A"), f.Launches("B")));
        Assert.True(shell.Find<Button>("RunWorkflow").IsEffectivelyVisible);
        Assert.False(shell.Find<Button>("StopWorkflow").IsEffectivelyVisible);
        Assert.False(shell.WorkflowShows("seed", "Workflow", "WorkflowRunning"));

        shell.Click(shell.Find<Button>("DismissRun"));
        Assert.False(shell.Find<Control>("WorkflowRunBar").IsEffectivelyVisible);
        Assert.Equal(["Not run", "Not run"], new[] { "A", "B" }.Select(title => shell.CardText(title, "CardStatus")));
    }

    [AvaloniaFact]
    public void While_a_run_owns_a_task_its_own_Run_stands_aside_and_after_the_run_its_own_run_shows_again()
    {
        using var f = new WorkflowRunFixture(Task(A, "A", 105), Task(B, "B", 405), Dependency(A, B));
        f.Answer("A", f.Says("A ready.", gate: "a"), f.Says("A again.")).Answer("B", f.Says("B ready."));
        var shell = f.Window();
        shell.StartRun();
        shell.WaitForCard("A", "Running");
        shell.Click(shell.Header(shell.Node("A")));

        Assert.Equal("Running", shell.InView<TextBlock>("RunTaskStatus").Text);
        Assert.Equal("A run of the \"Workflow\" workflow owns this task. Talk to it through its conversation, or stop the run.", shell.Text("RunOwner"));
        Assert.False(shell.Find<Button>("RunTask").IsEffectivelyVisible);
        Assert.False(shell.Find<Button>("CancelRun").IsEffectivelyVisible);
        Assert.False(shell.Has<TextBlock>("LastRunStatus") && shell.Find<TextBlock>("LastRunStatus").IsEffectivelyVisible);

        f.Open("a");
        shell.WaitForStatus("Completed");
        Assert.Equal("A run of the \"Workflow\" workflow ran this task last. Run it on its own to show its own result again.", shell.Text("RunOwner"));
        Assert.True(shell.InView<Button>("RunTask").IsEffectivelyEnabled);
        shell.Click(shell.InView<Button>("RunTask"));

        shell.WaitUntil(() => shell.Has<TextBlock>("LastRunStatus") && shell.Find<TextBlock>("LastRunStatus").Text == "Succeeded", "A's own run succeeds");
        Assert.False(shell.Find<TextBlock>("RunTaskStatus").IsEffectivelyVisible);
        Assert.Equal(("Succeeded", "Succeeded"), (shell.CardText("A", "CardStatus"), shell.CardText("B", "CardStatus")));
        Assert.Equal(2, f.Launches("A"));
    }

    [AvaloniaFact]
    public void Starting_twice_approves_one_run()
    {
        using var f = Chain();
        f.Answer("A", f.Says("A ready.", gate: "a")).Answer("B", f.Says("B ready."));
        var shell = f.Window();
        var preflight = shell.OpenPreflight();

        preflight.StartCommand.Execute(null);
        Assert.False(preflight.StartCommand.CanExecute(null));
        preflight.StartCommand.Execute(null);
        shell.WaitUntil(() => shell.WorkflowRun is not null, "the run starts");
        f.Open("a");
        shell.WaitForStatus("Completed");

        Assert.Single(f.RunFolders());
        Assert.Equal((1, 1), (f.Launches("A"), f.Launches("B")));
    }

    [AvaloniaFact]
    public void A_confirmation_that_finds_the_approval_lock_busy_is_tried_again()
    {
        using var f = Chain();
        f.Answer("A", f.Says("A ready.")).Answer("B", f.Says("B ready."));
        var shell = f.Window();
        var preflight = shell.OpenPreflight();
        // Another confirmation holds the workflow's approval lock past the ten seconds an approval waits for it.
        var held = new ApprovalIntents(f.Project, shell.Window.ViewModel.Canvas!.Workflow.Id).Lock()!;
        _ = System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(10.5)).ContinueWith(_ => held.Dispose());

        shell.Click(shell.Find<Button>("PreflightStart"));

        shell.WaitUntil(() => shell.WorkflowRun is not null, "the retried confirmation starts the run", () => $"Notice: {preflight.Notice}");
        shell.WaitForStatus("Completed");
        Assert.Single(f.RunFolders());
    }

    [AvaloniaFact]
    public void A_workflow_that_changed_after_the_preview_shows_a_new_preview_and_starts_nothing()
    {
        using var f = Chain();
        f.Answer("A", f.Says("A ready.")).Answer("B", f.Says("B ready."));
        var shell = f.Window();
        shell.OpenPreflight();
        var canvas = shell.Window.ViewModel.Canvas!;

        Assert.IsType<EditResult.Applied>(canvas.Document.Apply(new WorkflowEdit.EditTitle(B, "B2")));
        shell.Click(shell.Find<Button>("PreflightStart"));
        shell.WaitUntil(() => shell.Preflight?.Notice is not null, "the preview is refused");

        Assert.Equal("The workflow or the project changed since this preview. Check it again, then start.", shell.Text("PreflightNotice"));
        Assert.Equal(["A", "B2"], shell.TextsOf("PreflightTask"));
        Assert.Empty(f.RunFolders());
        Assert.Null(shell.WorkflowRun);

        shell.Click(shell.Find<Button>("PreflightStart"));
        shell.WaitUntil(() => shell.WorkflowRun is not null, "the run starts");
        Assert.Single(f.RunFolders());
    }

    [AvaloniaFact]
    public void Stop_Workflow_stops_the_running_task_and_starts_no_other()
    {
        using var f = Chain();
        f.Answer("A", f.Says("A ready.", gate: "a")).Answer("B", f.Says("B ready."));
        var shell = f.Window();
        shell.StartRun();
        shell.WaitForCard("A", "Running");

        shell.Click(shell.Find<Button>("StopWorkflow"));

        shell.WaitForStatus("Stopped");
        Assert.Equal(("Cancelled", "Not started"), (shell.CardText("A", "CardStatus"), shell.CardText("B", "CardStatus")));
        Assert.Equal("Stopped. Finished work stays.", shell.Text("RunActivity"));
        Assert.Equal((1, 0), (f.Launches("A"), f.Launches("B")));
        Assert.True(shell.Find<Button>("RunWorkflow").IsEffectivelyVisible);
    }
}
