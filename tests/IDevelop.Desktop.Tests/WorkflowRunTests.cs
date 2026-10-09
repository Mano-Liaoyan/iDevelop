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

    private static readonly TaskId C = TestTasks.Review;

    private static WorkflowRunFixture Chain() => new(Task(A, "A", 105), Task(B, "B", 405), Dependency(A, B));

    /// <summary>Whether the client of each task titled in <paramref name="titles"/> has read its first turn, which counts its launch.</summary>
    private static bool EachReadItsTurn(WorkflowRunFixture f, params string[] titles)
    {
        try
        {
            return titles.All(title => f.Launches(title) == 1);
        }
        catch (Exception error) when (error is IOException or FormatException)
        {
            return false;
        }
    }

    /// <summary>Three tasks that depend on nothing, so the run starts all three at once.</summary>
    private static WorkflowRunFixture Three() => new(Task(A, "A", 105), Task(B, "B", 405), Task(C, "C", 705));

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
        // A title stays on one line with the word before it.
        Assert.Equal(("Running", "0 of 2 done", "Running\u00A0\"A\""), (shell.RunStatus, shell.Text("RunProgress"), shell.Text("RunActivity")));
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
        Assert.False(shell.Find<TextBlock>("StartProblem").IsEffectivelyVisible);
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
    public void A_planner_that_ran_on_its_own_can_be_included_and_the_run_starts_only_what_it_planned()
    {
        const string proposal = """
            Here is the plan.

            ```idevelop
            {"status": "proposal",
             "add": [{"id": "api", "type": "type-1", "title": "Export API", "fields": {"instructions": "Add the CSV endpoint."}}],
             "connect": [{"from": "planner", "to": "api"}]}
            ```
            """;
        var plan = new WorkflowEdit.PlaceNode(A, BuiltInBlueprints.Plan, new CanvasPoint(105, 90))
        {
            Title = "Plan",
            Fields = System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("goal", "Add CSV export."),
            Settings = new NodeSettings(WorkflowRunFixture.Codex, ConversationMode.Chat),
        };
        using var f = new WorkflowRunFixture(plan);
        f.Answer("Plan", f.Says(proposal)).Answer("Export API", f.Says("API ready."));
        var shell = f.Window();
        shell.Click(shell.Header(shell.Node("Plan")));
        shell.Click(shell.InView<Button>("RunTask"));
        shell.WaitUntil(() => shell.Has<Button>("AcceptProposal") && shell.Find<Button>("AcceptProposal").IsEffectivelyEnabled, "the plan's proposal shows");
        // The planned task takes the planner's agent, as Generate's planner gives it.
        shell.Click(shell.InView<CheckBox>("ProposalUsePlannerAgent"));
        shell.Click(shell.InView<Button>("AcceptProposal"));
        shell.WaitUntil(() => shell.Nodes().Count() == 2, "the planned task is added");

        var preflight = shell.OpenPreflight();

        var row = Assert.Single(preflight.Inclusions);
        Assert.Equal(("Include \"Plan\"'s plan instead of running it again", "It waits for you. Including it marks it done when the run starts."),
            (row.Label, row.Note));
        Assert.Equal("Include \"Plan\"'s plan instead of running it again", shell.Find<CheckBox>("PreflightInclude").Content);
        Assert.Equal("Here is the plan.", shell.Text("PreflightIncludedReport").Split('\n')[0]);
        shell.Click(shell.Find<CheckBox>("PreflightInclude"));
        Assert.True(row.IsIncluded);
        shell.Click(shell.Find<Button>("PreflightStart"));
        shell.WaitUntil(() => shell.WorkflowRun is not null, "the run starts",
            () => $"Notice: {preflight.Notice}, starting {preflight.IsStarting}, can start {preflight.CanStart}, gaps [{string.Join("; ", preflight.Gaps.Select(gap => gap.Text))}]");
        shell.WaitForStatus("Completed");

        Assert.Equal((1, 1), (f.Launches("Plan"), f.Launches("Export API")));
        Assert.Equal(["Succeeded", "Succeeded"], new[] { "Plan", "Export API" }.Select(title => shell.CardText(title, "CardStatus")));
    }

    [AvaloniaFact]
    public void Another_window_s_active_run_of_other_content_keeps_Start_from_approving_and_Show_opens_it()
    {
        using var f = Chain();
        f.Answer("A", f.Says("A ready.", gate: "a")).Answer("B", f.Says("B ready."));
        var second = f.Window();
        var first = f.Window();
        first.StartRun();
        first.WaitForCard("A", "Running");

        Assert.IsType<EditResult.Applied>(second.Window.ViewModel.Canvas!.Document.Apply(new WorkflowEdit.EditTitle(B, "B2")));
        var preflight = second.OpenPreflight();
        Assert.True(second.Find<Button>("PreflightShowActive").IsEffectivelyVisible);
        second.Click(second.Find<Button>("PreflightStart"));
        second.WaitUntil(() => preflight.Notice is not null, "the approval is refused");

        Assert.Equal("Another run of this workflow is still active, with other content. Show it to stop it or let it finish.", second.Text("PreflightNotice"));
        Assert.Single(f.RunFolders());
        second.Click(second.Find<Button>("PreflightShowActive"));
        second.WaitUntil(() => second.Preflight is null, "the sheet shows the active run", () => $"Notice: {preflight.Notice}");
        Assert.Equal(first.WorkflowRun!.Address, second.WorkflowRun?.Address);
        Assert.Equal("Controlled by another window", second.RunStatus);
        f.Open("a");
        first.WaitForStatus("Completed");
    }

    [AvaloniaFact]
    public void A_confirmation_that_finds_the_journal_busy_is_tried_again()
    {
        using var f = Chain();
        f.Answer("A", f.Says("A ready.")).Answer("B", f.Says("B ready."));
        var shell = f.Window();
        var preflight = shell.OpenPreflight();
        // A step of another window holds the run journals' write lock for longer than an approval waits for it.
        var held = f.HoldJournal();
        _ = System.Threading.Tasks.Task.Delay(TimeSpan.FromMilliseconds(1600)).ContinueWith(_ => held.Dispose());

        shell.Click(shell.Find<Button>("PreflightStart"));

        shell.WaitUntil(() => shell.WorkflowRun is not null, "the retried confirmation starts the run", () => $"Notice: {preflight.Notice}");
        shell.WaitForStatus("Completed");
        Assert.Single(f.RunFolders());
    }

    [AvaloniaFact]
    public void A_journal_that_stays_busy_is_shown_as_a_sentence_and_Start_tries_again()
    {
        using var f = Chain();
        f.Answer("A", f.Says("A ready.")).Answer("B", f.Says("B ready."));
        var shell = f.Window();
        var preflight = shell.OpenPreflight();
        using (f.HoldJournal())
        {
            shell.Click(shell.Find<Button>("PreflightStart"));
            shell.WaitUntil(() => preflight.Notice is not null, "the approval is refused", () => "No notice yet.");
            Assert.Equal("iDevelop could not approve the run. The run's records are busy. Try again in a moment.", shell.Text("PreflightNotice"));
            Assert.Null(shell.WorkflowRun);
            Assert.True(shell.Find<Button>("PreflightStart").IsEffectivelyEnabled);
        }

        shell.Click(shell.Find<Button>("PreflightStart"));
        shell.WaitUntil(() => shell.WorkflowRun is not null, "the run starts", () => $"Notice: {preflight.Notice}");
        shell.WaitForStatus("Completed");
        Assert.Single(f.RunFolders());
    }

    [AvaloniaFact]
    public void Stop_Workflow_is_off_while_its_stop_is_being_recorded()
    {
        using var f = Chain();
        f.Answer("A", f.Says("A ready.", gate: "a")).Answer("B", f.Says("B ready."));
        var shell = f.Window();
        shell.StartRun();
        shell.WaitForCard("A", "Running");

        using (f.HoldJournal())
        {
            shell.Click(shell.Find<Button>("StopWorkflow"));
            Assert.False(shell.Find<Button>("StopWorkflow").IsEffectivelyEnabled);
        }

        shell.WaitForStatus("Stopped");
        Assert.Equal((1, 0), (f.Launches("A"), f.Launches("B")));
    }

    [AvaloniaFact]
    public void Closing_a_project_from_the_sidebar_during_a_run_asks_first_and_Stop_records_the_stop()
    {
        using var f = Chain();
        f.Answer("A", f.Says("A ready.", gate: "a")).Answer("B", f.Says("B ready."));
        var shell = f.Window();
        var address = shell.StartRun().Address;
        shell.WaitForCard("A", "Running");

        shell.Click(Shell.ById<Button>(shell.ProjectItem("seed"), "CloseProject").Single());

        Assert.Equal(["A run of the \"Workflow\" workflow is active. Stop it and close seed?", "Stop and leave", "Keep running"], shell.DialogTexts());
        shell.Choose("StopAndLeave");

        // The project leaves the sidebar at once, and its run records the stop before the project's runner closes.
        Assert.Empty(shell.Window.ViewModel.Projects);
        RunPhase Phase() => Assert.IsType<RunRead.Loaded>(RunStore.Open(f.Project).Read(address.Workflow, address.Run)).Record.Phase;
        shell.WaitUntil(() => Phase() != RunPhase.Approved, "the run records the stop", () => $"Its phase is {Phase()}.");
        Assert.Equal(0, f.Launches("B"));
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
        // The new preview's rows lay out after the refusal that brings them, so the wait renders once more.
        shell.WaitUntil(() => shell.Preflight?.Notice is not null, "the preview is refused", () => "No notice yet.");

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

    [AvaloniaFact]
    public void Ready_tasks_run_at_once_and_the_bar_counts_those_still_running()
    {
        using var f = Three();
        f.Answer("A", f.Says("A ready.", gate: "a")).Answer("B", f.Says("B ready.", gate: "bc")).Answer("C", f.Says("C ready.", gate: "bc"));
        var shell = f.Window();

        shell.StartRun();

        foreach (var title in new[] { "A", "B", "C" })
        {
            shell.WaitForCard(title, "Running");
        }

        shell.WaitUntil(() => shell.RunStatus == "3 running", "the bar counts three running tasks", () => $"It shows {shell.RunStatus}.");
        Assert.Equal(("3 running", "0 of 3 done", "Running\u00A0\"A\", \"B\", and\u00A0\"C\""), (shell.RunStatus, shell.Text("RunProgress"), shell.Text("RunActivity")));
        Assert.Equal("Workflow run: 3 running, 0 of 3 done", shell.WorkflowRun!.Summary);
        // A card runs once its client's process starts, which is before the client reads its turn and counts its launch.
        shell.WaitUntil(() => EachReadItsTurn(f, "A", "B", "C"), "each client reads its turn");
        Assert.True(shell.WorkflowShows("seed", "Workflow", "WorkflowRunning"));

        f.Open("a");
        shell.WaitForCard("A", "Succeeded");
        shell.WaitUntil(() => shell.RunStatus == "2 running", "the bar counts the two still running", () => $"It shows {shell.RunStatus}.");
        Assert.Equal(("2 running", "1 of 3 done", "Running\u00A0\"B\" and\u00A0\"C\""), (shell.RunStatus, shell.Text("RunProgress"), shell.Text("RunActivity")));
        Assert.Equal(["Running", "Running"], new[] { "B", "C" }.Select(title => shell.CardText(title, "CardStatus")));

        f.Open("bc");
        shell.WaitForStatus("Completed");
        Assert.Equal(["Succeeded", "Succeeded", "Succeeded"], new[] { "A", "B", "C" }.Select(title => shell.CardText(title, "CardStatus")));
        Assert.Equal((1, 1, 1), (f.Launches("A"), f.Launches("B"), f.Launches("C")));
    }

    [AvaloniaFact]
    public void Stop_Workflow_stops_every_running_task()
    {
        using var f = Three();
        f.Answer("A", f.Says("A ready.", gate: "never")).Answer("B", f.Says("B ready.", gate: "never")).Answer("C", f.Says("C ready.", gate: "never"));
        var shell = f.Window();
        shell.StartRun();
        shell.WaitUntil(() => shell.RunStatus == "3 running" && new[] { "A", "B", "C" }.All(title => shell.CardText(title, "CardStatus") == "Running"),
            "all three run", () => $"It shows {shell.RunStatus}.");
        shell.WaitUntil(() => EachReadItsTurn(f, "A", "B", "C"), "each client reads its turn");

        shell.Click(shell.Find<Button>("StopWorkflow"));

        shell.WaitForStatus("Stopped");
        Assert.Equal(["Cancelled", "Cancelled", "Cancelled"], new[] { "A", "B", "C" }.Select(title => shell.CardText(title, "CardStatus")));
        Assert.Equal("Stopped. Finished work stays.", shell.Text("RunActivity"));
        Assert.Equal((1, 1, 1), (f.Launches("A"), f.Launches("B"), f.Launches("C")));
        Assert.False(shell.WorkflowShows("seed", "Workflow", "WorkflowRunning"));
    }
}
