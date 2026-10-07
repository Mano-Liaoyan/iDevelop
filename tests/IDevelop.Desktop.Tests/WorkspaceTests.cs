using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Controls.Presenters;
using Avalonia.VisualTree;
using Avalonia.Media;
using IDevelop.Execution;
using IDevelop.Projects;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.AppTempFolder;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.Desktop.Tests;

/// <summary>Several projects, each with several workflows, open in one window through the sidebar's tree.</summary>
[Collection(ProcessCollection.Name)]
public sealed class WorkspaceTests : IDisposable
{
    private static readonly ExecutionSettings Codex = new(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "high" };

    private readonly TempFolder _temp = AppTempFolder.New();
    private readonly FakeClients _fakes;
    private readonly string _gate;

    public WorkspaceTests()
    {
        _fakes = new FakeClients(_temp.Create("bin"));
        _gate = Path.Combine(_temp.Create("evidence"), "go");
    }

    // A fake client that a failed test left waiting at the gate ends here.
    public void Dispose()
    {
        File.WriteAllText(_gate, "");
        _temp.Dispose();
    }

    /// <summary>
    /// A project with a Build workflow, whose Design task runs on Codex, and a Release workflow. Every project made here
    /// has the same workflow names and task titles.
    /// </summary>
    private string Project(string name)
    {
        var folder = _temp.Create(name);
        Save(folder, "Build", TaskAt(TaskId.New(), "Design", 105, 90, Codex, "Say hi."), TaskAt(TaskId.New(), "Implement", 405, 90));
        Save(folder, "Release", TaskAt(TaskId.New(), "Ship", 105, 90));
        return folder;
    }

    private static void Save(string folder, string name, params WorkflowEdit[] edits)
    {
        var document = WorkflowDocument.Create(folder, name);
        foreach (var edit in edits)
        {
            Assert.IsType<EditResult.Applied>(document.Apply(edit));
        }

        document.Save();
    }

    private static string WorkflowFile(string folder, string name) =>
        WorkflowDocument.OpenProject(folder).Single(document => document.Current.Name == name).FilePath;

    private static string[] SavedTitles(string folder, string name) =>
        [.. WorkflowDocument.OpenProject(folder).Single(document => document.Current.Name == name).Current.Tasks.Values.Select(task => task.Title).Order()];

    /// <summary>The window title, then the shown cards' titles in order.</summary>
    private static string Shown(Shell shell) =>
        $"{shell.Window.Title}: {string.Join(", ", shell.Nodes().Select(node => ((Canvas.TaskNodeViewModel)node.DataContext!).Title).Order())}";

    private (Shell Shell, string Alpha, string Beta) OpenBoth(ClientDirectory? clients = null)
    {
        var (alpha, beta) = (Project("alpha"), Project("beta"));
        var shell = Shell.Open(alpha, clients);
        shell.Window.ViewModel.Open(beta);
        shell.Render();
        return (shell, alpha, beta);
    }

    private static Button ProjectButton(Shell shell, string project, string automationId) =>
        Shell.ById<Button>(shell.ProjectItem(project), automationId).Single();

    [AvaloniaFact]
    public void Two_projects_list_their_workflows_collapsed_and_choosing_a_workflow_shows_it_without_expanding_it()
    {
        var (shell, _, _) = OpenBoth();

        Assert.Equal([("alpha", ["Build", "Release"]), ("beta", ["Build", "Release"])], shell.Tree());
        Assert.Equal(("beta", "Build"), shell.Breadcrumb());
        Assert.Equal("beta - iDevelop", shell.Window.Title);
        Assert.Equal(
            ["2", "1", "2", "1"],
            new[] { ("alpha", "Build"), ("alpha", "Release"), ("beta", "Build"), ("beta", "Release") }.Select(row => shell.WorkflowText(row.Item1, row.Item2, "TaskCount")));
        Assert.DoesNotContain(new[] { "alpha", "beta" }.SelectMany(project => new[] { "Build", "Release" }.Select(workflow => (project, workflow))),
            row => shell.WorkflowTasks(row.project, row.workflow).IsEffectivelyVisible);

        shell.Click(shell.WorkflowRow("alpha", "Release"));

        Assert.Equal(("alpha", "Release"), shell.Breadcrumb());
        Assert.Equal("alpha - iDevelop: Ship", Shown(shell));
        Assert.False(shell.WorkflowTasks("alpha", "Release").IsEffectivelyVisible);

        shell.Click(shell.WorkflowExpand("alpha", "Release"));

        Assert.Equal(["Ship"], Shell.Texts(shell.WorkflowTasks("alpha", "Release")));
        Assert.False(shell.WorkflowTasks("beta", "Release").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Choosing_a_task_row_of_another_workflow_shows_that_workflow_and_selects_the_task()
    {
        var (shell, _, _) = OpenBoth();
        shell.Click(shell.WorkflowExpand("alpha", "Build"));

        shell.Click(shell.TaskRow("alpha", "Build", "Implement"));

        Assert.Equal(("alpha", "Build"), shell.Breadcrumb());
        Assert.True(shell.Node("Implement").IsSelected);
        Assert.Equal("Implement", shell.Find<TextBox>("TaskTitle").Text);
    }

    [AvaloniaFact]
    public void A_workflow_that_is_not_shown_keeps_its_task_selection_without_the_selected_look()
    {
        var (shell, _, _) = OpenBoth();
        shell.Click(shell.WorkflowExpand("alpha", "Build"));
        shell.Click(shell.TaskRow("alpha", "Build", "Implement"));
        Color? RowFill() => (shell.TaskRow("alpha", "Build", "Implement").GetVisualDescendants().OfType<ContentPresenter>().First().Background as ISolidColorBrush)?.Color;
        var selected = RowFill();

        shell.Click(shell.WorkflowRow("beta", "Build"));

        Assert.True(shell.TaskRow("alpha", "Build", "Implement").IsSelected);
        Assert.Equal(shell.Resource("StateSelectedSurfaceBrush"), selected);
        Assert.Equal(shell.Resource("SurfaceMuted60Brush"), RowFill());
        shell.Click(shell.WorkflowRow("alpha", "Build"));
        Assert.Equal(shell.Resource("StateSelectedSurfaceBrush"), RowFill());
    }

    [AvaloniaFact]
    public void Each_workflow_keeps_its_unsaved_edits_and_its_own_undo_history_across_switches_and_save_writes_only_the_shown_one()
    {
        var (shell, alpha, _) = OpenBoth();
        var release = File.ReadAllBytes(WorkflowFile(alpha, "Release"));
        shell.Click(shell.WorkflowRow("alpha", "Build"));
        shell.AddNode();
        Assert.Equal("alpha* - iDevelop", shell.Window.Title);
        shell.Click(shell.WorkflowRow("alpha", "Release"));
        Assert.Equal("alpha - iDevelop", shell.Window.Title);
        shell.AddNode();

        shell.Click(shell.WorkflowRow("beta", "Build"));
        Assert.Equal("beta - iDevelop: Design, Implement", Shown(shell));
        Assert.False(shell.Find<Button>("Undo").IsEffectivelyEnabled);
        shell.Click(shell.WorkflowRow("alpha", "Build"));

        Assert.Equal("alpha* - iDevelop: Design, Implement, New task", Shown(shell));
        Assert.Equal([true, true, false, false], new[] { ("alpha", "Build"), ("alpha", "Release"), ("beta", "Build"), ("beta", "Release") }
            .Select(row => shell.WorkflowShows(row.Item1, row.Item2, "WorkflowUnsaved")));
        shell.Click(shell.Find<Button>("Undo"));
        Assert.Equal("alpha - iDevelop: Design, Implement", Shown(shell));
        shell.Click(shell.Find<Button>("Redo"));
        shell.Click(shell.Find<Button>("Save"));

        Assert.Equal(["Design", "Implement", "New task"], SavedTitles(alpha, "Build"));
        Assert.Equal(release, File.ReadAllBytes(WorkflowFile(alpha, "Release")));
        Assert.Equal([false, true], new[] { "Build", "Release" }.Select(workflow => shell.WorkflowShows("alpha", workflow, "WorkflowUnsaved")));
        shell.Click(shell.WorkflowRow("alpha", "Release"));
        Assert.Equal("alpha* - iDevelop: New task, Ship", Shown(shell));
        shell.Click(shell.Find<Button>("Undo"));
        Assert.Equal("alpha - iDevelop: Ship", Shown(shell));
    }

    [AvaloniaFact]
    public void Each_workflow_keeps_its_zoom_pan_selection_and_conversation_draft_across_switches()
    {
        var (shell, _, _) = OpenBoth();
        shell.Click(shell.WorkflowRow("alpha", "Build"));
        shell.Click(shell.Header(shell.Node("Design")));
        shell.Click(shell.InView<TextBox>("Composer"));
        shell.Type("Draft for later");
        shell.Pan(shell.Editor.TranslatePoint(new Point(700, 400), shell.Window)!.Value, new Vector(-600, -300));
        Assert.Equal(new Point(600, 300), shell.Editor.ViewportLocation);
        shell.Click(shell.Find<Button>("ZoomIn"));
        var build = (Math.Round(shell.Editor.ViewportZoom, 4), Shell.Rounded(shell.Editor.ViewportLocation));
        Assert.Equal((1.2599, new Point(674.06, 382.52)), build);

        shell.Click(shell.WorkflowRow("alpha", "Release"));

        Assert.Equal((1.0, new Point(0, 0)), (shell.Editor.ViewportZoom, shell.Editor.ViewportLocation));
        Assert.Null(shell.Window.ViewModel.Canvas!.SelectedNode);
        shell.Click(shell.WorkflowRow("alpha", "Build"));

        Assert.Equal(build, (Math.Round(shell.Editor.ViewportZoom, 4), Shell.Rounded(shell.Editor.ViewportLocation)));
        Assert.True(shell.Node("Design").IsSelected);
        Assert.Equal(("Design", "Draft for later"), (shell.Find<TextBox>("TaskTitle").Text, shell.InView<TextBox>("Composer").Text));
    }

    /// <summary>The client waits at the gate until it is stopped or the test opens the gate.</summary>
    private FakeRule Waits() => On("exec", "--json")
        .Print("""{"type":"thread.started","thread_id":"01a104d5-d442-71a1-9b08-8938c119e5ae"}""")
        .WaitForFile(_gate);

    [AvaloniaFact]
    public void A_running_task_runs_on_across_switches_and_another_projects_close_and_stays_in_its_own_workflow()
    {
        FakeAgents.Install(_fakes, ClientId.Codex, Waits());
        var clients = _fakes.DiscoverAsync().Result;
        var (shell, alpha, _) = OpenBoth(clients);
        shell.Click(shell.WorkflowRow("alpha", "Build"));
        shell.Click(shell.Header(shell.Node("Design")));
        shell.Click(shell.InView<Button>("RunTask"));
        Assert.Equal(("Running", "Design"), (shell.CardText("Design", "CardStatus"), shell.Find<TextBlock>("RunBarTask").Text));

        shell.Click(shell.WorkflowRow("alpha", "Release"));

        Assert.False(shell.Find<Control>("RunBar").IsEffectivelyVisible);
        Assert.Equal([true, false], new[] { "Build", "Release" }.Select(workflow => shell.WorkflowShows("alpha", workflow, "WorkflowRunning")));
        Assert.Equal([true, false], new[] { "alpha", "beta" }.Select(project => Shell.ById<Control>(shell.ProjectItem(project), "ProjectRunning").Single().IsEffectivelyVisible));
        shell.Click(shell.WorkflowRow("beta", "Build"));
        Assert.False(shell.Find<Control>("RunBar").IsEffectivelyVisible);

        shell.Click(ProjectButton(shell, "beta", "CloseProject"));

        Assert.Null(shell.Dialog);
        Assert.Equal([("alpha", ["Build", "Release"])], shell.Tree());
        Assert.Equal(("alpha", "Build"), shell.Breadcrumb());
        Assert.Equal(("Running", "Design"), (shell.CardText("Design", "CardStatus"), shell.Find<TextBlock>("RunBarTask").Text));

        shell.Click(ProjectButton(shell, "alpha", "CloseProject"));
        Assert.Equal(["\"Design\" is running. Stop it and close alpha?", "Stop and leave", "Keep running"], shell.DialogTexts());
        shell.Choose("KeepRunning");

        Assert.Equal([("alpha", ["Build", "Release"])], shell.Tree());
        Assert.Equal("Running", shell.CardText("Design", "CardStatus"));

        var runs = shell.Window.ViewModel.Projects.Single().Runs;
        shell.Click(ProjectButton(shell, "alpha", "CloseProject"));
        shell.Choose("StopAndLeave");
        shell.WaitUntil(() => shell.Tree().Length == 0 && runs.Active.IsEmpty, "alpha closes and its run stops");

        Assert.Equal("iDevelop", shell.Window.Title);
        shell.Window.ViewModel.Open(alpha);
        shell.Render();
        Assert.Equal("Interrupted", shell.CardText("Design", "CardStatus"));
    }

    [AvaloniaFact]
    public void Opening_a_folder_that_is_already_open_shows_it_and_opens_nothing_new()
    {
        var (shell, alpha, _) = OpenBoth();

        shell.Window.ViewModel.Open(alpha + Path.DirectorySeparatorChar);
        shell.Render();

        Assert.Equal([("alpha", ["Build", "Release"]), ("beta", ["Build", "Release"])], shell.Tree());
        Assert.Equal(("alpha", "Build"), shell.Breadcrumb());
    }

    [AvaloniaFact]
    public void New_workflow_takes_the_next_free_number_stays_unsaved_and_renames_inline_as_an_undoable_edit()
    {
        var alpha = Project("alpha");
        var shell = Shell.Open(alpha);

        shell.Click(ProjectButton(shell, "alpha", "NewWorkflow"));

        Assert.Equal([("alpha", ["Build", "Release", "Workflow 1"])], shell.Tree());
        Assert.Equal((("alpha", "Workflow 1"), "alpha* - iDevelop"), (shell.Breadcrumb(), shell.Window.Title));
        Assert.Empty(shell.Nodes());
        Assert.Equal(2, Directory.GetFiles(Path.Combine(alpha, ".idp", "workflows")).Length);

        shell.WorkflowRow("alpha", "Workflow 1").Focus();
        shell.Press(Key.F2);
        Assert.True(shell.Shown<TextBox>("WorkflowNameBox").IsKeyboardFocusWithin);
        shell.Type("Checks");
        shell.Press(Key.Enter);

        Assert.Equal([("alpha", ["Build", "Release", "Checks"])], shell.Tree());
        Assert.Equal(("alpha", "Checks"), shell.Breadcrumb());
        Assert.Same(shell.WorkflowRow("alpha", "Checks"), shell.Window.FocusManager!.GetFocusedElement());

        shell.RightClick(shell.Center(shell.WorkflowRow("alpha", "Checks")));
        shell.Click(shell.MenuItem("WorkflowRename"));
        shell.Type("Discarded");
        var box = shell.Shown<TextBox>("WorkflowNameBox");
        Assert.Equal((true, "Discarded"), (box.IsKeyboardFocusWithin, box.Text));
        shell.Press(Key.Escape);
        Assert.Equal([("alpha", ["Build", "Release", "Checks"])], shell.Tree());

        shell.Click(ProjectButton(shell, "alpha", "NewWorkflow"));
        Assert.Equal([("alpha", ["Build", "Release", "Checks", "Workflow 1"])], shell.Tree());

        shell.Click(shell.WorkflowRow("alpha", "Checks"));
        shell.Click(shell.Find<Button>("Undo"));
        Assert.Equal([("alpha", ["Build", "Release", "Workflow 1", "Workflow 1"])], shell.Tree());
        shell.Click(shell.Find<Button>("Redo"));
        shell.Click(shell.Find<Button>("Save"));

        Assert.Equal(
            ["Build", "Checks", "Release"],
            WorkflowDocument.OpenProject(alpha).Select(document => document.Current.Name));
    }

    [AvaloniaFact]
    public void Close_project_with_unsaved_workflows_asks_once_and_save_writes_each_of_them()
    {
        var (shell, alpha, _) = OpenBoth();
        shell.Click(shell.WorkflowRow("alpha", "Build"));
        shell.AddNode();
        shell.Click(shell.WorkflowRow("alpha", "Release"));
        shell.AddNode();
        shell.Click(shell.WorkflowRow("beta", "Release"));

        shell.Click(ProjectButton(shell, "alpha", "CloseProject"));
        Assert.Equal(["Save changes to alpha?", "Save", "Don't save", "Cancel"], shell.DialogTexts());
        shell.Choose("SaveChanges");

        Assert.Equal([("beta", ["Build", "Release"])], shell.Tree());
        Assert.Equal(("beta", "Release"), shell.Breadcrumb());
        Assert.Equal(["Design", "Implement", "New task"], SavedTitles(alpha, "Build"));
        Assert.Equal(["New task", "Ship"], SavedTitles(alpha, "Release"));
    }

    [AvaloniaFact]
    public void Closing_the_window_asks_about_each_projects_unsaved_workflows_before_it_saves_any()
    {
        var (shell, alpha, beta) = OpenBoth();
        shell.Click(shell.WorkflowRow("alpha", "Build"));
        shell.AddNode();
        shell.Click(shell.WorkflowRow("beta", "Release"));
        shell.AddNode();

        shell.Window.Close();
        shell.Render();
        Assert.Equal(["Save changes to alpha?", "Save", "Don't save", "Cancel"], shell.DialogTexts());
        shell.Choose("SaveChanges");
        Assert.Equal(["Save changes to beta?", "Save", "Don't save", "Cancel"], shell.DialogTexts());
        shell.Choose("CancelChanges");

        Assert.True(shell.Window.IsVisible);
        Assert.Equal(["Design", "Implement"], SavedTitles(alpha, "Build"));

        shell.Window.Close();
        shell.Render();
        shell.Choose("SaveChanges");
        shell.Choose("DiscardChanges");

        Assert.False(shell.Window.IsVisible);
        Assert.Equal(["Design", "Implement", "New task"], SavedTitles(alpha, "Build"));
        Assert.Equal(["Ship"], SavedTitles(beta, "Release"));
    }

    [AvaloniaFact]
    public void The_next_start_reopens_the_open_projects_with_the_shown_workflow_and_the_expanded_rows()
    {
        var (shell, alpha, beta) = OpenBoth();
        shell.Click(shell.WorkflowExpand("alpha", "Release"));
        shell.Click(shell.WorkflowRow("alpha", "Release"));
        shell.Window.Close();
        shell.Render();
        Assert.False(shell.Window.IsVisible);

        var session = JsonNode.Parse(File.ReadAllText(((App)Application.Current!).SessionFile!))!;
        Assert.Equal([alpha, beta], session["projects"]!.AsArray().Select(folder => (string)folder!));
        Assert.Equal(alpha, (string)session["selected"]!["folder"]!);

        var reopened = Shell.Show();
        reopened.Window.ViewModel.Restore(null);
        reopened.Render();

        Assert.Equal([("alpha", ["Build", "Release"]), ("beta", ["Build", "Release"])], reopened.Tree());
        Assert.Equal(("alpha", "Release"), reopened.Breadcrumb());
        Assert.Equal(["Ship"], Shell.Texts(reopened.WorkflowTasks("alpha", "Release")));
        Assert.Equal([false, false, false], new[] { ("alpha", "Build"), ("beta", "Build"), ("beta", "Release") }
            .Select(row => reopened.WorkflowTasks(row.Item1, row.Item2).IsEffectivelyVisible));
        Assert.Null(reopened.Window.ViewModel.Status);
    }

    [AvaloniaFact]
    public void The_next_start_skips_a_closed_project_and_a_missing_folder_and_then_shows_the_folder_it_was_given()
    {
        var (shell, _, _) = OpenBoth();
        var gamma = Project("gamma");
        shell.Window.ViewModel.Open(gamma);
        shell.Render();
        shell.Click(ProjectButton(shell, "alpha", "CloseProject"));
        shell.Window.Close();
        shell.Render();
        Directory.Delete(gamma, recursive: true);
        var delta = Project("delta");

        var reopened = Shell.Show();
        reopened.Window.ViewModel.Restore(delta);
        reopened.Render();

        Assert.Equal([("beta", ["Build", "Release"]), ("delta", ["Build", "Release"])], reopened.Tree());
        Assert.Equal(("delta", "Build"), reopened.Breadcrumb());
        Assert.Equal($"The folder {gamma} does not exist.", reopened.Status);
    }

    [AvaloniaFact]
    public void A_workflow_shows_a_blueprint_saved_in_a_sibling_workflow_once_it_is_shown_again()
    {
        var alpha = Project("alpha");
        var shell = Shell.Open(alpha);
        shell.Click(shell.WorkflowRow("alpha", "Release"));
        shell.Click(shell.WorkflowRow("alpha", "Build"));
        var implement = BuiltInBlueprints.Implement;
        BlueprintLibrary.Project(alpha).Save(
            new Blueprint(BlueprintLibrary.NewKey("Bug fix"), "Bug fix", implement.Work, implement.Fields, implement.Defaults) { DerivedFrom = implement.Key });
        var release = shell.Window.ViewModel.Projects.Single().Workflows.Single(canvas => canvas.Name == "Release");
        Assert.Empty(ProjectBlueprints(release));

        shell.Click(shell.WorkflowRow("alpha", "Release"));

        Assert.Equal(["Bug fix"], ProjectBlueprints(release));
    }

    private static string[] ProjectBlueprints(Canvas.WorkflowCanvasViewModel canvas) =>
        [.. canvas.Blueprints.Groups.Single(group => group.Heading == "PROJECT").Entries.Select(entry => entry.Name)];

    [AvaloniaFact]
    public void A_single_format_3_workflow_and_a_format_2_workflow_open_side_by_side()
    {
        var current = _temp.Seed(TaskAt(TestTasks.Design, "Design", 105, 90));
        var legacy = _temp.Create("legacy");
        File.Copy(Fixture.Path("storage-change-format2.json"), Path.Combine(Directory.CreateDirectory(Path.Combine(legacy, ".idp", "workflows")).FullName, "019a9d2e-4c10-7a3b-8e21-5f0c9b7d1a01.json"));

        var shell = Shell.Open(current);
        shell.Window.ViewModel.Open(legacy);
        shell.Render();

        Assert.Equal([("seed", ["Workflow"]), ("legacy", ["Workflow"])], shell.Tree());
        Assert.Equal(
            "iDevelop converted this workflow from format 2. Its 3 tasks became Implement nodes, and its review connection became a dependency. Save to keep it in format 3.",
            shell.Status);
        Assert.Equal(["1", "3"], new[] { "seed", "legacy" }.Select(project => shell.WorkflowText(project, "Workflow", "TaskCount")));
        Assert.Equal("legacy* - iDevelop", shell.Window.Title);
    }

    private static void Rename(Shell shell, string project, string workflow, string name)
    {
        shell.WorkflowRow(project, workflow).Focus();
        shell.Press(Key.F2);
        shell.Type(name);
        shell.Press(Key.Enter);
    }

    [AvaloniaFact]
    public void Two_renames_of_a_workflow_undo_one_at_a_time()
    {
        var shell = Shell.Open(Project("alpha"));
        Rename(shell, "alpha", "Build", "Checks");
        Rename(shell, "alpha", "Checks", "Gates");

        shell.Click(shell.Find<Button>("Undo"));

        Assert.Equal([("alpha", ["Checks", "Release"])], shell.Tree());
        Assert.Equal(("alpha", "Checks"), shell.Breadcrumb());
    }

    [AvaloniaFact]
    public void Close_project_shows_the_neighbour_at_once_while_its_running_task_stops()
    {
        FakeAgents.Install(_fakes, ClientId.Codex, On("exec", "--json")
            .Print("""{"type":"thread.started","thread_id":"01a104d5-d442-71a1-9b08-8938c119e5ae"}""")
            .Hang());
        var (shell, alpha, _) = OpenBoth(_fakes.DiscoverAsync().Result);
        shell.Click(shell.WorkflowRow("alpha", "Build"));
        shell.Click(shell.Header(shell.Node("Design")));
        shell.Click(shell.InView<Button>("RunTask"));
        var window = shell.Window.ViewModel;
        var closing = window.Projects[0];
        // What the window lists and shows at the moment the runner reports the task stopped.
        (bool Listed, string? Shown)? whenStopped = null;
        closing.Runs.Changed += (_, _) =>
        {
            if (closing.Runs.Active.IsEmpty)
            {
                whenStopped ??= (window.Projects.Contains(closing), window.Canvas?.Project.Name);
            }
        };

        shell.Click(ProjectButton(shell, "alpha", "CloseProject"));
        shell.Choose("StopAndLeave");

        Assert.Equal([("beta", ["Build", "Release"])], shell.Tree());
        Assert.Equal(("beta - iDevelop: Design, Implement", ("beta", "Build")), (Shown(shell), shell.Breadcrumb()));
        shell.WaitUntil(() => whenStopped is not null, "alpha's task stops");
        Assert.Equal((false, "beta"), whenStopped);
        shell.Window.ViewModel.Open(alpha);
        shell.Render();
        Assert.Equal("Interrupted", shell.CardText("Design", "CardStatus"));
    }

    [AvaloniaFact]
    public void Closing_the_window_takes_no_input_while_its_running_task_stops()
    {
        FakeAgents.Install(_fakes, ClientId.Codex, On("exec", "--json")
            .Print("""{"type":"thread.started","thread_id":"01a104d5-d442-71a1-9b08-8938c119e5ae"}""")
            .Hang());
        var (shell, _, _) = OpenBoth(_fakes.DiscoverAsync().Result);
        shell.Click(shell.WorkflowRow("alpha", "Build"));
        shell.Click(shell.Header(shell.Node("Design")));
        shell.Click(shell.InView<Button>("RunTask"));
        Control[] inputs = [shell.WorkflowRow("beta", "Build"), shell.Editor, shell.Find<Button>("RunTask")];

        shell.Window.Close();
        shell.Render();
        shell.Choose("StopAndLeave");

        Assert.Equal([false, false, false], inputs.Select(input => input.IsEffectivelyEnabled));
        shell.WaitUntil(() => !shell.Window.IsVisible, "the window closes once the task stops");
    }

    [AvaloniaFact]
    public void Closing_a_project_or_the_window_closes_the_generate_sheets_of_its_workflows()
    {
        var (shell, _, _) = OpenBoth();
        shell.Click(shell.WorkflowRow("alpha", "Release"));
        shell.Click(shell.Find<Button>("GenerateWorkflow"));
        var (alpha, beta) = (shell.Window.ViewModel.Projects[0], shell.Window.ViewModel.Projects[1]);
        Assert.NotNull(alpha.Workflows[1].Sheet);

        _ = shell.Window.ViewModel.Close(alpha);
        shell.Render();

        Assert.Equal([("beta", ["Build", "Release"])], shell.Tree());
        Assert.Null(alpha.Workflows[1].Sheet);
        shell.Click(shell.Find<Button>("GenerateWorkflow"));
        Assert.NotNull(beta.Workflows[0].Sheet);

        shell.Window.Close();
        shell.Render();

        Assert.False(shell.Window.IsVisible);
        Assert.Null(beta.Workflows[0].Sheet);
    }

    [AvaloniaFact]
    public void Closing_a_project_shows_the_neighbour_with_a_blueprint_saved_in_its_library_meanwhile()
    {
        var (shell, _, beta) = OpenBoth();
        shell.Click(shell.WorkflowRow("alpha", "Build"));
        var implement = BuiltInBlueprints.Implement;
        BlueprintLibrary.Project(beta).Save(
            new Blueprint(BlueprintLibrary.NewKey("Bug fix"), "Bug fix", implement.Work, implement.Fields, implement.Defaults) { DerivedFrom = implement.Key });
        var neighbour = shell.Window.ViewModel.Projects[1].Workflows[0];
        Assert.Empty(ProjectBlueprints(neighbour));

        shell.Click(ProjectButton(shell, "alpha", "CloseProject"));

        Assert.Equal(("beta", "Build"), shell.Breadcrumb());
        Assert.Equal(["Bug fix"], ProjectBlueprints(neighbour));
    }

    [AvaloniaFact]
    public void Each_workflow_keeps_its_selection_when_the_window_switches_twice_before_it_lays_out()
    {
        var (shell, _, _) = OpenBoth();
        shell.Click(shell.WorkflowRow("alpha", "Release"));
        shell.Click(shell.Header(shell.Node("Ship")));
        shell.Click(shell.WorkflowRow("alpha", "Build"));
        shell.Click(shell.Header(shell.Node("Design")));
        var workflows = shell.Window.ViewModel.Projects[0].Workflows;

        shell.Window.ViewModel.Select(workflows[1]);
        shell.Window.ViewModel.Select(workflows[0]);
        shell.Render();

        Assert.Equal(("Build", "Design"), (shell.Breadcrumb().Item2, shell.Find<TextBox>("TaskTitle").Text));
        shell.Click(shell.WorkflowRow("alpha", "Release"));
        Assert.Equal(("Release", "Ship"), (shell.Breadcrumb().Item2, shell.Find<TextBox>("TaskTitle").Text));
    }

    [AvaloniaFact]
    public void Sidebar_buttons_name_their_project_or_workflow_and_the_shown_workflow_reads_as_shown()
    {
        var (shell, _, _) = OpenBoth();
        static AutomationPeer Peer(Control control) => ControlAutomationPeer.CreatePeerForElement(control);
        var expand = shell.WorkflowExpand("beta", "Release");

        Assert.Equal(
            ["New workflow in alpha", "Close alpha", "Show tasks of Release"],
            new[] { ProjectButton(shell, "alpha", "NewWorkflow"), ProjectButton(shell, "alpha", "CloseProject"), expand }.Select(control => Peer(control).GetName()));
        shell.Click(expand);
        Assert.Equal("Hide tasks of Release", Peer(expand).GetName());
        Assert.Equal(
            ["", "", "Shown", ""],
            new[] { ("alpha", "Build"), ("alpha", "Release"), ("beta", "Build"), ("beta", "Release") }
                .Select(row => Peer(shell.WorkflowRow(row.Item1, row.Item2)).GetItemStatus() ?? ""));
    }

    private static string[] RememberedProjects() =>
        [.. JsonNode.Parse(File.ReadAllText(((App)Application.Current!).SessionFile!))!["projects"]!.AsArray().Select(folder => (string)folder!)];

    [AvaloniaFact]
    public void A_remembered_folder_that_is_missing_stays_remembered_and_opens_at_the_start_after_it_is_back()
    {
        var (shell, alpha, beta) = OpenBoth();
        shell.Window.Close();
        shell.Render();
        Directory.Delete(beta, recursive: true);

        var missing = Shell.Show();
        missing.Window.ViewModel.Restore(null);
        missing.Render();

        Assert.Equal([("alpha", ["Build", "Release"])], missing.Tree());
        Assert.Equal($"The folder {beta} does not exist.", missing.Status);
        Assert.Equal([alpha, beta], RememberedProjects());
        missing.Window.Close();
        missing.Render();
        Assert.Equal([alpha, beta], RememberedProjects());

        Assert.Equal(beta, Project("beta"));
        var back = Shell.Show();
        back.Window.ViewModel.Restore(null);
        back.Render();

        Assert.Equal([("alpha", ["Build", "Release"]), ("beta", ["Build", "Release"])], back.Tree());
        Assert.Null(back.Window.ViewModel.Status);
    }

    [AvaloniaFact]
    public void A_window_without_a_session_file_reopens_nothing_and_remembers_nothing()
    {
        var (shell, _, _) = OpenBoth();
        shell.Window.Close();
        shell.Render();
        var sessionFile = ((App)Application.Current!).SessionFile!;
        var remembered = File.ReadAllText(sessionFile);
        var gamma = Project("gamma");

        var window = new MainWindowViewModel(new ClientDirectory(CommandResolver.Create([], [])), _ => Task.CompletedTask);
        window.Restore(gamma);
        window.NewWorkflow(window.Projects.Single());
        _ = window.Close(window.Projects.Single());

        Assert.Equal((0, remembered), (window.Projects.Count, File.ReadAllText(sessionFile)));
        Assert.Null(new App().SessionFile);
    }

    [AvaloniaFact]
    public void The_next_start_leaves_out_an_unsaved_new_workflow_and_shows_its_projects_first_workflow()
    {
        var shell = Shell.Open(Project("alpha"));
        shell.Click(ProjectButton(shell, "alpha", "NewWorkflow"));
        Assert.Equal(("alpha", "Workflow 1"), shell.Breadcrumb());
        shell.Window.Close();
        shell.Render();
        shell.Choose("DiscardChanges");
        Assert.False(shell.Window.IsVisible);

        var reopened = Shell.Show();
        reopened.Window.ViewModel.Restore(null);
        reopened.Render();

        Assert.Equal([("alpha", ["Build", "Release"])], reopened.Tree());
        Assert.Equal(("alpha", "Build"), reopened.Breadcrumb());
    }

    [AvaloniaFact]
    public void Cancel_at_close_projects_unsaved_question_leaves_its_task_running_and_the_project_open()
    {
        FakeAgents.Install(_fakes, ClientId.Codex, Waits());
        var (shell, _, _) = OpenBoth(_fakes.DiscoverAsync().Result);
        shell.Click(shell.WorkflowRow("alpha", "Build"));
        shell.Click(shell.Header(shell.Node("Design")));
        shell.Click(shell.InView<Button>("RunTask"));
        shell.Click(shell.WorkflowRow("alpha", "Release"));
        shell.AddNode();

        shell.Click(ProjectButton(shell, "alpha", "CloseProject"));
        Assert.Equal(["\"Design\" is running. Stop it and close alpha?", "Stop and leave", "Keep running"], shell.DialogTexts());
        shell.Choose("StopAndLeave");
        Assert.Equal(["Save changes to alpha?", "Save", "Don't save", "Cancel"], shell.DialogTexts());
        shell.Choose("CancelChanges");

        Assert.Equal([("alpha", ["Build", "Release"]), ("beta", ["Build", "Release"])], shell.Tree());
        Assert.Equal("alpha* - iDevelop: New task, Ship", Shown(shell));
        shell.Click(shell.WorkflowRow("alpha", "Build"));
        Assert.Equal(("Running", "Design"), (shell.CardText("Design", "CardStatus"), shell.Find<TextBlock>("RunBarTask").Text));
        File.WriteAllText(_gate, "");
        shell.WaitUntil(() => shell.Window.ViewModel.ActiveRuns.IsEmpty, "the run ends before its folder goes");
    }

    [AvaloniaTheory]
    [InlineData("SaveChanges", false, "alpha - iDevelop", "Checks")]
    [InlineData("DiscardChanges", false, "alpha* - iDevelop", "Build")]
    [InlineData("CancelChanges", true, "alpha* - iDevelop", "Build")]
    public void Closing_the_window_during_a_rename_asks_about_the_typed_name(string answer, bool open, string title, string saved)
    {
        var alpha = Project("alpha");
        var shell = Shell.Open(alpha);
        shell.WorkflowRow("alpha", "Build").Focus();
        shell.Press(Key.F2);
        shell.Type("Checks");

        shell.Window.Close();
        shell.Render();
        Assert.Equal(["Save changes to alpha?", "Save", "Don't save", "Cancel"], shell.DialogTexts());
        shell.Choose(answer);

        Assert.Equal((open, title), (shell.Window.IsVisible, shell.Window.Title));
        Assert.Equal([saved, "Release"], WorkflowDocument.OpenProject(alpha).Select(document => document.Current.Name).Order());
    }

    [AvaloniaFact]
    public void New_workflow_beside_an_unnamed_workflow_is_its_second()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(TestTasks.Design, "Design", 105, 90)));

        shell.Click(ProjectButton(shell, "seed", "NewWorkflow"));

        Assert.Equal([("seed", ["Workflow", "Workflow 2"])], shell.Tree());
        Assert.Equal(("seed", "Workflow 2"), shell.Breadcrumb());
    }
}
