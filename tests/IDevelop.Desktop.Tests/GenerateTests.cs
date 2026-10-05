using System.Collections.Immutable;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using IDevelop.Desktop.Canvas;
using IDevelop.Execution;
using IDevelop.Nodes;
using IDevelop.Projects;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.AppTempFolder;
using static IDevelop.TestSupport.FakeAgents;

namespace IDevelop.Desktop.Tests;

/// <summary>Generate Workflow through the real main window: the sheet, the planner it places and runs, and its proposal.</summary>
[Collection(ProcessCollection.Name)]
public sealed class GenerateTests : IDisposable
{
    private const string Session = "01a108d7-464d-77a3-8908-a36f38ce6c14";
    private const string Prompt = "Add CSV export to the reports page.";

    // The fake Codex's first model without a problem, at that model's default level, as the sheet picks it.
    private static readonly ExecutionSettings Codex = new(ClientId.Codex) { Model = "gpt-6.1-sol", Reasoning = "low" };

    private static readonly string Reply = """
        Here is the plan.

        ```idevelop
        {"status": "proposal",
         "add": [{"id": "api", "type": "type-1", "title": "Export API", "fields": {"instructions": "Add the CSV endpoint."}},
                 {"id": "button", "type": "type-1", "title": "Export button", "fields": {"instructions": "Add the button."}},
                 {"id": "tests", "type": "type-1", "title": "Export tests", "fields": {"instructions": "Test the export."}}],
         "connect": [{"from": "planner", "to": "api"}, {"from": "api", "to": "button"}, {"from": "api", "to": "tests"}]}
        ```
        """;

    private readonly TempFolder _temp = AppTempFolder.New();
    private readonly FakeClients _fakes;

    public GenerateTests() => _fakes = new FakeClients(_temp.Create("bin"));

    public void Dispose() => _temp.Dispose();

    [AvaloniaFact]
    public void An_empty_workflow_offers_to_start_from_a_description_until_it_has_a_task()
    {
        var shell = Shell.Open(_temp.Create("seed"));
        var canvas = shell.Window.ViewModel.Canvas!;
        Assert.True(shell.Find<UserControl>("GenerateEmptyState").IsEffectivelyVisible);

        canvas.PlaceInView(BuiltInBlueprints.Implement);
        shell.Render();
        Assert.False(shell.Find<UserControl>("GenerateEmptyState").IsEffectivelyVisible);

        canvas.Edit(new WorkflowEdit.Delete([.. canvas.Workflow.Tasks.Keys], []));
        shell.Render();
        Assert.True(shell.Find<UserControl>("GenerateEmptyState").IsEffectivelyVisible);

        shell.Click(shell.Find<Button>("GenerateStart"));
        Assert.True(shell.Has<GenerateSheet>("GenerateSheet"));
    }

    [AvaloniaFact]
    public void The_empty_state_card_lets_the_canvas_pan_under_it()
    {
        var shell = Shell.Open(_temp.Create("seed"));
        var title = shell.Find<UserControl>("GenerateEmptyState").GetVisualDescendants().OfType<TextBlock>().First();
        var before = shell.Editor.ViewportLocation;

        shell.Pan(shell.Center(title), new Vector(-40, 0));

        Assert.Equal(before + new Vector(40, 0), shell.Editor.ViewportLocation);
    }

    [AvaloniaFact]
    public void The_sheet_offers_the_clients_that_can_plan_and_picks_the_first_ready_one()
    {
        var shell = OpenEmpty();
        OpenSheet(shell);

        Assert.Equal(("Codex", "GPT-6.1-Sol", "low"), (shell.Picked("GenerateClient"), shell.Picked("GenerateModel"), shell.Picked("GenerateReasoning")));
        Assert.Equal(["Claude Code · not installed", "Codex", "Antigravity CLI · not installed"], shell.Pick("GenerateClient", "Codex"));
        Assert.Equal("", shell.Find<TextBlock>("GenerateProblem").Text ?? "");
    }

    [AvaloniaFact]
    public void Generate_is_off_while_the_description_is_blank()
    {
        var shell = OpenEmpty();
        OpenSheet(shell);
        var submit = shell.Find<Button>("GenerateSubmit");
        Assert.False(submit.IsEffectivelyEnabled);

        shell.Type("  ");
        shell.Press(Key.Enter);
        Assert.False(submit.IsEffectivelyEnabled);

        shell.Type("Export");
        Assert.True(submit.IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void Without_a_ready_client_that_can_plan_Generate_is_off_and_says_why()
    {
        var shell = Shell.Open(_temp.Create("seed"), _fakes.DiscoverAsync().Result);
        OpenSheet(shell);
        shell.Type(Prompt);

        Assert.Equal(
            "None of Claude Code, Codex, or Antigravity CLI is ready to plan. Agents in the sidebar says why.",
            shell.Find<TextBlock>("GenerateProblem").Text);
        Assert.False(shell.Find<Button>("GenerateSubmit").IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void Submit_places_a_Chat_planner_with_the_description_as_its_goal_selects_it_and_runs_it()
    {
        var shell = OpenEmpty(Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, Reply)));
        var canvas = shell.Window.ViewModel.Canvas!;
        OpenSheet(shell);
        shell.Type("Add CSV export to the reports page.");
        shell.Press(Key.Enter);
        shell.Type("Include tests.");
        var viewport = (shell.Editor.ViewportLocation, shell.Editor.ViewportSize);

        shell.Click(shell.Find<Button>("GenerateSubmit"));

        Assert.False(shell.Has<GenerateSheet>("GenerateSheet"));
        var planner = Assert.Single(canvas.Workflow.Tasks.Values);
        Assert.Equal(
            (BuiltInBlueprints.Plan.Key, "Add CSV export to the reports page.", "Add CSV export to the reports page.\nInclude tests.", ConversationMode.Chat, Codex),
            (planner.Blueprint.Key, planner.Title, planner.Field("goal"), planner.Conversation, planner.Execution));
        Assert.Equal(
            new CanvasPoint(viewport.ViewportLocation.X + 60, viewport.ViewportLocation.Y + viewport.ViewportSize.Height / 2 - WorkflowCanvasViewModel.TaskCardHeight / 2),
            canvas.Workflow.Positions[planner.Id]);
        Assert.Equal(planner.Id, canvas.SelectedNode?.Id);
        Assert.True(canvas.HasStarted(planner.Id));
        Assert.True(canvas.Generated(planner.Id));
    }

    [AvaloniaFact]
    public void Ctrl_Enter_submits_the_sheet_and_Escape_closes_it_without_an_edit()
    {
        var shell = OpenEmpty(Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, Reply)));
        var canvas = shell.Window.ViewModel.Canvas!;
        OpenSheet(shell);
        shell.Type(Prompt);
        shell.Press(Key.Escape);

        Assert.False(shell.Has<GenerateSheet>("GenerateSheet"));
        Assert.Empty(canvas.Workflow.Tasks);

        OpenSheet(shell);
        shell.Type(Prompt);
        shell.Press(Key.Enter, RawInputModifiers.Control);

        Assert.False(shell.Has<GenerateSheet>("GenerateSheet"));
        Assert.Equal([Prompt], canvas.Workflow.Tasks.Values.Select(task => task.Field("goal")));
    }

    [AvaloniaFact]
    public void The_proposal_draws_ghosts_and_the_planner_proposes_until_it_is_accepted()
    {
        var shell = Generated();
        var planner = shell.Window.ViewModel.Canvas!.SelectedNode!;

        Assert.Equal(["New Implement Export API", "New Implement Export button", "New Implement Export tests"], Ghosts(shell));
        Assert.Equal(NodeRole.Proposing, planner.Role);
        Assert.Equal("Review Proposal", ButtonLabel(shell));
        Assert.True(shell.InView<CheckBox>("ProposalUsePlannerAgent").IsChecked);
    }

    [AvaloniaFact]
    public void Accept_places_the_tasks_in_layered_columns_with_the_planners_agent()
    {
        var shell = Generated();
        var canvas = shell.Window.ViewModel.Canvas!;
        var planner = canvas.SelectedNode!.Id;
        var origin = canvas.Workflow.Positions[planner];

        shell.Click(shell.InView<Button>("AcceptProposal"));

        var workflow = canvas.Workflow;
        var added = workflow.Tasks.Values.Where(task => task.Id != planner).ToDictionary(task => task.Title);
        Assert.Equal(
            [
                ("Export API", new CanvasPoint(origin.X + Proposal.ColumnStep, origin.Y)),
                ("Export button", new CanvasPoint(origin.X + 2 * Proposal.ColumnStep, origin.Y)),
                ("Export tests", new CanvasPoint(origin.X + 2 * Proposal.ColumnStep, origin.Y + Proposal.RowStep)),
            ],
            added.Keys.Order().Select(title => (title, workflow.Positions[added[title].Id])));
        Assert.All(added.Values, task => Assert.Equal(Codex, task.Execution));
        (TaskId, TaskId)[] dependencies = [(planner, added["Export API"].Id), (added["Export API"].Id, added["Export button"].Id), (added["Export API"].Id, added["Export tests"].Id)];
        Assert.Equal(
            dependencies.Order(),
            workflow.Connections.Where(connection => connection.Value == ConnectionKind.Dependency).Select(connection => (connection.Key.From, connection.Key.To)).Order());
    }

    [AvaloniaFact]
    public void Unticked_the_new_tasks_keep_their_types_default_agent()
    {
        var shell = Generated();
        var canvas = shell.Window.ViewModel.Canvas!;
        var planner = canvas.SelectedNode!.Id;

        shell.Click(shell.InView<CheckBox>("ProposalUsePlannerAgent"));
        shell.Click(shell.InView<Button>("AcceptProposal"));

        var added = canvas.Workflow.Tasks.Values.Where(task => task.Id != planner).ToList();
        Assert.Equal(3, added.Count);
        Assert.All(added, task => Assert.Null(task.Execution));
    }

    [AvaloniaFact]
    public void A_planner_the_person_placed_keeps_the_types_default_agent_unless_they_tick_the_box()
    {
        var plan = TestTasks.Design;
        Install(_fakes, ClientId.Codex, Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, Reply)));
        var project = _temp.Seed(new WorkflowEdit.PlaceNode(plan, BuiltInBlueprints.Plan, new CanvasPoint(105, 90))
        {
            Title = "Plan export",
            Fields = ImmutableDictionary<string, string>.Empty.Add("goal", Prompt),
            Settings = new NodeSettings(Codex, ConversationMode.Chat),
        });
        var shell = Shell.Open(project, _fakes.DiscoverAsync().Result);
        shell.Click(shell.Header(shell.Node("Plan export")));
        shell.Click(shell.InView<Button>("RunTask"));
        shell.WaitUntil(() => shell.Has<StackPanel>("Proposal"), "the proposal shows");

        Assert.False(shell.InView<CheckBox>("ProposalUsePlannerAgent").IsChecked);
        shell.Click(shell.InView<Button>("AcceptProposal"));

        var workflow = shell.Window.ViewModel.Canvas!.Workflow;
        Assert.All(workflow.Tasks.Values.Where(task => task.Id != plan), task => Assert.Null(task.Execution));
    }

    [AvaloniaFact]
    public void The_Generate_button_follows_the_planner_from_planning_to_review_and_back()
    {
        var release = Path.Combine(_temp.Create("release"), "go");
        var shell = OpenEmpty(Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).WaitForFile(release).Print(ReplyLines(ClientId.Codex, Reply)));
        var canvas = shell.Window.ViewModel.Canvas!;
        Assert.Equal("Generate", ButtonLabel(shell));
        Submit(shell, Prompt);
        var planner = canvas.SelectedNode!;

        Assert.Equal("Planning…", ButtonLabel(shell));
        canvas.SelectedNodes.Clear();
        shell.Click(shell.Find<Button>("GenerateWorkflow"));
        Assert.Same(planner, canvas.SelectedNode);
        Assert.False(shell.Has<GenerateSheet>("GenerateSheet"));

        File.WriteAllText(release, "");
        shell.WaitUntil(() => shell.Has<StackPanel>("Proposal"), "the proposal shows");
        Assert.Equal("Review Proposal", ButtonLabel(shell));

        shell.Click(shell.InView<Button>("AcceptProposal"));
        shell.Click(shell.InView<Button>("MarkDone"));
        shell.WaitUntil(() => planner.State == NodeState.Succeeded, "the planner is done");
        Assert.Equal("Generate", ButtonLabel(shell));
        shell.Click(shell.Find<Button>("GenerateWorkflow"));
        Assert.True(shell.Has<GenerateSheet>("GenerateSheet"));
    }

    [AvaloniaFact]
    public void A_refused_run_keeps_the_planner_and_says_why()
    {
        Install(_fakes, ClientId.Codex);
        var project = _temp.Create("seed");
        var attempts = DataFolder.Attempts(project);
        Directory.CreateDirectory(Path.GetDirectoryName(attempts)!);
        File.WriteAllText(attempts, "");
        var shell = Shell.Open(project, _fakes.DiscoverAsync().Result);
        var canvas = shell.Window.ViewModel.Canvas!;

        Submit(shell, Prompt);

        var planner = Assert.Single(canvas.Workflow.Tasks.Values);
        Assert.Equal((Prompt, false), (planner.Title, canvas.HasStarted(planner.Id)));
        Assert.StartsWith($"iDevelop could not write {attempts}.", shell.Status);
        Assert.Equal("Generate", ButtonLabel(shell));
    }

    [AvaloniaFact]
    public void While_the_sheet_is_open_Save_takes_no_click()
    {
        var shell = OpenEmpty();
        var canvas = shell.Window.ViewModel.Canvas!;
        canvas.PlaceInView(BuiltInBlueprints.Implement);
        OpenSheet(shell);

        shell.Click(shell.Center(shell.Find<Button>("Save")));

        Assert.True(shell.Has<GenerateSheet>("GenerateSheet"));
        Assert.True(canvas.Document.HasUnsavedChanges);
        Assert.False(Directory.Exists(DataFolder.Workflows(canvas.Document.ProjectFolder)));
    }

    [Theory]
    [InlineData("Add CSV export.", "Add CSV export.")]
    [InlineData("\n   \n  Add CSV export.  \r\nWith tests.", "Add CSV export.")]
    [InlineData(" \n ", "Plan")]
    [InlineData("Export every report in the workspace as CSV, Excel, and PDF from one menu", "Export every report in the workspace as CSV, Excel, and PDF…")]
    public void A_planner_is_titled_by_the_descriptions_first_line(string prompt, string title)
    {
        Assert.Equal(title, WorkflowCanvasViewModel.PlannerTitle(prompt));
        Assert.True(title.Length <= 60);
    }

    /// <summary>An empty project with the fake Codex, which runs <paramref name="runs"/>.</summary>
    private Shell OpenEmpty(params FakeRule[] runs)
    {
        Install(_fakes, ClientId.Codex, runs);
        return Shell.Open(_temp.Create("seed"), _fakes.DiscoverAsync().Result);
    }

    /// <summary>Generates from an empty project and waits for the planner's proposal.</summary>
    private Shell Generated()
    {
        var shell = OpenEmpty(Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, Reply)));
        Submit(shell, Prompt);
        shell.WaitUntil(() => shell.Has<StackPanel>("Proposal"), "the proposal shows");
        return shell;
    }

    private static void OpenSheet(Shell shell)
    {
        shell.Click(shell.Find<Button>("GenerateWorkflow"));
        Assert.True(shell.Find<TextBox>("GeneratePrompt").IsFocused);
    }

    private static void Submit(Shell shell, string prompt)
    {
        OpenSheet(shell);
        shell.Type(prompt);
        shell.Click(shell.Find<Button>("GenerateSubmit"));
    }

    private static string? ButtonLabel(Shell shell) => AutomationProperties.GetName(shell.Find<Button>("GenerateWorkflow"));

    private static string[] Ghosts(Shell shell) =>
        [.. shell.Window.GetVisualDescendants().OfType<Panel>()
            .Where(panel => AutomationProperties.GetAutomationId(panel) == "GhostCard")
            .Select(panel => $"{Shell.TextOf(panel.GetVisualDescendants().OfType<TextBlock>().Single(text => AutomationProperties.GetAutomationId(text) == "GhostLabel"))} {AutomationProperties.GetName(panel)}")];
}
