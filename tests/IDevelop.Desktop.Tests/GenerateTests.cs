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
using Nodify;
using static IDevelop.Desktop.Tests.AppTempFolder;
using static IDevelop.TestSupport.FakeAgents;

namespace IDevelop.Desktop.Tests;

/// <summary>Generate Workflow through the real main window: the sheet, the planner it places and runs, and its proposal.</summary>
[Collection(ProcessCollection.Name)]
public sealed class GenerateTests : IDisposable
{
    private const string Session = "01a108d7-464d-77a3-8908-a36f38ce6c14";
    private const string Prompt = "Add CSV export to the reports page.";

    // The fake Codex's first model without a problem, at the high level the sheet picks for planning. The model's own
    // default level is low.
    private static readonly ExecutionSettings Codex = new(ClientId.Codex) { Model = "gpt-6.1-sol", Reasoning = "high" };

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

    // The planner chose an agent for each task: Codex and Claude Code, which are ready, and Pi, which is not installed.
    private static readonly string ChoosingReply = """
        Here is the plan.

        ```idevelop
        {"status": "proposal",
         "add": [{"id": "api", "type": "type-1", "title": "Export API", "fields": {"instructions": "Add the CSV endpoint."},
                  "agent": {"client": "codex", "model": "gpt-6.1-sol", "reasoning": "xhigh", "reason": "The endpoint shapes the export's data."}},
                 {"id": "button", "type": "type-1", "title": "Export button", "fields": {"instructions": "Add the button."},
                  "agent": {"client": "claude-code", "model": "claude-haiku-4-5", "reasoning": "low", "reason": "A small view change."}},
                 {"id": "tests", "type": "type-1", "title": "Export tests", "fields": {"instructions": "Test the export."},
                  "agent": {"client": "pi", "model": "deepseek/deepseek-flash", "reasoning": "low", "reason": "Fast and cheap."}}],
         "connect": [{"from": "planner", "to": "api"}, {"from": "api", "to": "button"}, {"from": "api", "to": "tests"}]}
        ```
        """;

    private static readonly ExecutionSettings Haiku = new(ClientId.ClaudeCode) { Model = "claude-haiku-4-5", Reasoning = "low" };

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

        Assert.Equal(("Codex", "GPT-6.1-Sol", "high"), (shell.Picked("GenerateClient"), shell.Picked("GenerateModel"), shell.Picked("GenerateReasoning")));
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
        shell.WaitUntil(() => shell.Window.ViewModel.ActiveRuns.IsEmpty, "the planner's turn ends before its folder goes");
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
        shell.WaitUntil(() => shell.Window.ViewModel.ActiveRuns.IsEmpty, "the planner's turn ends before its folder goes");
    }

    [AvaloniaFact]
    public void The_proposal_draws_ghosts_and_the_planner_proposes_until_it_is_accepted()
    {
        var shell = Generated();
        var planner = shell.Window.ViewModel.Canvas!.SelectedNode!;

        Assert.Equal(["Codex · GPT-6.1-Sol · high Export API", "Codex · GPT-6.1-Sol · high Export button", "Codex · GPT-6.1-Sol · high Export tests"], Ghosts(shell));
        Assert.Equal(NodeRole.Proposing, planner.Role);
        Assert.Equal("Review Proposal", ButtonLabel(shell));
        Assert.True(shell.InView<CheckBox>("ProposalUsePlannerAgent").IsChecked);
    }

    [AvaloniaFact]
    public void The_proposal_draws_its_connections_as_ghost_wires_from_port_to_port_until_it_is_accepted()
    {
        var shell = Generated();
        var canvas = shell.Window.ViewModel.Canvas!;
        var origin = WorkflowCanvasViewModel.ToPoint(canvas.Workflow.Positions[canvas.SelectedNode!.Id]);
        var api = origin + new Point(Proposal.ColumnStep, 0);
        var button = origin + new Point(2 * Proposal.ColumnStep, 0);
        var tests = origin + new Point(2 * Proposal.ColumnStep, Proposal.RowStep);
        var output = WorkflowCanvasViewModel.OutputPortCenter;
        var input = WorkflowCanvasViewModel.InputPortCenter;

        Assert.Equal(
            [(origin + output, api + input), (api + output, button + input), (api + output, tests + input)],
            GhostWires(shell));
        Assert.Equal(
            ["kind-implement", "kind-implement", "kind-plan"],
            GhostWireLines(shell).Select(wire => wire.Classes.Single(name => name.StartsWith("kind-", StringComparison.Ordinal))).Order());
        Assert.All(GhostWireLines(shell), wire => Assert.Equal([4.0, 3.0], wire.StrokeDashArray!));

        shell.Click(shell.InView<Button>("ProposalAcceptFinish"));

        Assert.Empty(GhostWires(shell));
        Assert.Equal(3, canvas.Workflow.Connections.Count);
    }

    [AvaloniaFact]
    public void The_planners_reply_shows_its_prose_and_the_Proposal_section_stands_for_its_block()
    {
        var shell = Generated();

        Assert.Equal("Here is the plan.", shell.InView<TextBox>("LastRunResult").Text);
        Assert.Equal(3, shell.InView<StackPanel>("Proposal").GetVisualDescendants().OfType<CheckBox>().Count(box => box.IsChecked == true && AutomationProperties.GetAutomationId(box) != "ProposalUsePlannerAgent"));
    }

    [AvaloniaFact]
    public void Accept_and_Finish_places_the_tasks_in_layered_columns_with_the_planners_agent_and_ends_the_planner()
    {
        var shell = Generated();
        var canvas = shell.Window.ViewModel.Canvas!;
        var node = canvas.SelectedNode!;
        var planner = node.Id;
        var origin = canvas.Workflow.Positions[planner];

        shell.Click(shell.InView<Button>("ProposalAcceptFinish"));
        shell.WaitUntil(() => node.State == NodeState.Succeeded, "the planner is done");
        Assert.Equal("Added 3 tasks.", shell.Status);
        shell.ShowTasks();
        Assert.Equal([Prompt, "Export API", "Export button", "Export tests"], Shell.Texts(shell.Find<ListBox>("SidebarTasks")));

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
        InspectorLayoutTests.AssertLaidOut(shell);

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

        shell.Click(shell.InView<Button>("ProposalAcceptFinish"));
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

    [AvaloniaFact]
    public void While_the_sheet_is_open_the_save_undo_and_redo_keys_leave_the_workflow_and_its_file_alone()
    {
        var shell = OpenEmpty();
        var canvas = shell.Window.ViewModel.Canvas!;
        canvas.PlaceInView(BuiltInBlueprints.Implement);
        canvas.PlaceInView(BuiltInBlueprints.Review);
        shell.Press(Key.Z, RawInputModifiers.Control);
        var workflow = canvas.Workflow;
        OpenSheet(shell);
        shell.Find<ComboBox>("GenerateClient").Focus();

        shell.Press(Key.S, RawInputModifiers.Control);
        shell.Press(Key.Z, RawInputModifiers.Control);
        shell.Press(Key.Z, RawInputModifiers.Control | RawInputModifiers.Shift);
        shell.Press(Key.Y, RawInputModifiers.Control);

        Assert.True(shell.Has<GenerateSheet>("GenerateSheet"));
        Assert.Same(workflow, canvas.Workflow);
        Assert.True(shell.ShowsUnsavedChanges);
        Assert.False(Directory.Exists(DataFolder.Workflows(canvas.Document.ProjectFolder)));
        Assert.Equal([false, false, false], new[] { "Save", "Undo", "Redo" }.Select(id => shell.Find<Button>(id).IsEffectivelyEnabled));

        shell.Press(Key.Escape);
        shell.Press(Key.S, RawInputModifiers.Control);
        Assert.False(shell.ShowsUnsavedChanges);
        Assert.True(Directory.Exists(DataFolder.Workflows(canvas.Document.ProjectFolder)));
    }

    [AvaloniaFact]
    public void The_Add_popover_on_empty_canvas_offers_Generate_Workflow_which_opens_the_sheet()
    {
        var shell = OpenEmpty();

        shell.RightClick(shell.InEditor(200, 150));
        Assert.Equal("Generate Workflow…", shell.AddRows().SkipWhile(row => row.StartsWith("Add ", StringComparison.Ordinal)).First());
        shell.Click(shell.Window.GetVisualDescendants().OfType<Button>()
            .Single(row => AutomationProperties.GetAutomationId(row) == "AddNodeAction" && AutomationProperties.GetName(row) == "Generate Workflow…"));

        Assert.False(shell.AddPopoverIsOpen);
        Assert.True(shell.Find<TextBox>("GeneratePrompt").IsFocused);
        shell.Press(Key.Escape);
        Assert.False(shell.Has<GenerateSheet>("GenerateSheet"));

        shell.RightClick(shell.InEditor(200, 150));
        shell.Type("gen");
        Assert.Equal("Generate Workflow…", shell.Highlighted());
        shell.Press(Key.Enter);

        Assert.True(shell.Has<GenerateSheet>("GenerateSheet"));
        Assert.Empty(shell.Window.ViewModel.Canvas!.Nodes);
    }

    [AvaloniaFact]
    public void While_the_sheet_is_open_the_column_splitters_take_no_drag_and_the_sheet_keeps_focus()
    {
        var shell = OpenEmpty();
        OpenSheet(shell);
        var sheet = shell.Find<GenerateSheet>("GenerateSheet");
        var columns = ((Grid)shell.Window.Content!).ColumnDefinitions;
        var widths = columns.Select(column => column.ActualWidth).ToArray();
        var splitters = shell.Window.GetVisualDescendants().OfType<GridSplitter>().Where(splitter => splitter.IsEffectivelyVisible).ToArray();
        Assert.Equal(2, splitters.Length);

        foreach (var splitter in splitters)
        {
            var center = shell.Center(splitter);
            Assert.True(shell.Window.InputHitTest(center) is Visual hit && sheet.IsVisualAncestorOf(hit), "the scrim takes the splitter's point");
            shell.Drag(center, center + new Vector(-120, 0));
        }

        Assert.Equal(widths, columns.Select(column => column.ActualWidth));
        Assert.True(shell.Find<TextBox>("GeneratePrompt").IsFocused);
        shell.Press(Key.Escape);
        Assert.False(shell.Has<GenerateSheet>("GenerateSheet"));
    }

    [AvaloniaFact]
    public void The_planners_prompt_lists_the_clients_ready_here_with_their_models_and_levels()
    {
        var prompt = Path.Combine(_temp.Create("evidence"), "prompt.txt");
        Install(_fakes, ClientId.ClaudeCode);
        var shell = OpenEmpty(Fresh(ClientId.Codex).CaptureStdin(prompt).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, Reply)));

        SubmitOnCodex(shell);
        shell.WaitUntil(() => shell.Has<StackPanel>("Proposal"), "the proposal shows");

        var text = File.ReadAllText(prompt);
        Assert.Contains("\nAgents you may choose:\n- claude-code (Claude Code):\n  - claude-fable-5-1, Claude Fable 5.1. Reasoning: low, medium, high, xhigh, max.\n", text);
        Assert.Contains("\n- codex (Codex):\n  - gpt-6.1-sol, GPT-6.1-Sol. Reasoning: low, medium, high, xhigh, max, ultra.\n", text);
        Assert.DoesNotContain("- pi (Pi)", text);
        Assert.Contains("\"agent\": {\"client\": \"...\", \"model\": \"...\", \"reasoning\": \"...\", \"reason\": \"...\"}", text);
    }

    [AvaloniaFact]
    public void Each_ghost_card_shows_the_agent_its_task_takes_and_a_choice_that_falls_back_says_why()
    {
        var shell = GeneratedChoosing();

        Assert.Equal(
            ["Codex · GPT-6.1-Sol · xhigh Export API", "Claude Code · Claude Haiku 4.5 · low Export button", "Codex · GPT-6.1-Sol · high Export tests"],
            Ghosts(shell));
        Assert.Equal(new string?[] { null, null, "Pi isn't installed" }, GhostNotes(shell));
        // Every card has the client on one line and the model and level on the next, whatever their length.
        Assert.Equal([["Codex", "GPT-6.1-Sol · xhigh"], ["Claude Code", "Claude Haiku 4.5 · low"], ["Codex", "GPT-6.1-Sol · high"]], GhostLines(shell));
    }

    [AvaloniaFact]
    public void The_review_shows_each_new_tasks_agent_and_reason_and_why_a_choice_falls_back()
    {
        var shell = GeneratedChoosing();

        Assert.Equal(
            [
                ("Export API", "Codex · GPT-6.1-Sol · xhigh", "The endpoint shapes the export's data.", null),
                ("Export button", "Claude Code · Claude Haiku 4.5 · low", "A small view change.", null),
                ("Export tests", "Codex · GPT-6.1-Sol · high", null,
                    "The planner chose Pi, which isn't installed. The task takes the planner's agent instead."),
            ],
            Review(shell));
        Assert.True(shell.InView<CheckBox>("ProposalUsePlannerAgent").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Accept_writes_each_tasks_chosen_agent_and_the_planners_into_a_task_whose_choice_fell_back()
    {
        var shell = GeneratedChoosing();
        var canvas = shell.Window.ViewModel.Canvas!;
        var planner = canvas.SelectedNode!.Id;

        shell.Click(shell.InView<Button>("AcceptProposal"));

        var added = canvas.Workflow.Tasks.Values.Where(task => task.Id != planner).ToDictionary(task => task.Title, task => task.Execution);
        Assert.Equal(
            new Dictionary<string, ExecutionSettings?>
            {
                ["Export API"] = Codex with { Reasoning = "xhigh" },
                ["Export button"] = Haiku,
                ["Export tests"] = Codex,
            },
            added);
        shell.ShowTasks();
        Assert.Equal("Claude Code · Claude Haiku 4.5 · low", shell.CardAgent("Export button"));
    }

    [AvaloniaFact]
    public void Unticked_the_box_leaves_a_task_whose_choice_fell_back_without_an_agent_and_its_note_says_so()
    {
        var shell = GeneratedChoosing();
        var canvas = shell.Window.ViewModel.Canvas!;

        shell.Click(shell.InView<CheckBox>("ProposalUsePlannerAgent"));

        Assert.Equal(new string?[] { null, null, "Pi isn't installed" }, GhostNotes(shell));
        Assert.Equal("New Implement Export tests", Ghosts(shell)[2]);
        Assert.Equal(
            "The planner chose Pi, which isn't installed. Choose the task's agent before it runs.",
            Review(shell)[2].Note);
        shell.Click(shell.InView<Button>("AcceptProposal"));
        Assert.Null(canvas.Workflow.Tasks.Values.Single(task => task.Title == "Export tests").Execution);
        Assert.Equal(Haiku, canvas.Workflow.Tasks.Values.Single(task => task.Title == "Export button").Execution);
    }

    [AvaloniaFact]
    public void The_person_changes_a_tasks_agent_in_the_review_and_Accept_writes_their_choice()
    {
        var shell = GeneratedChoosing();
        var canvas = shell.Window.ViewModel.Canvas!;
        Assert.False(shell.Has<ComboBox>("ProposalAgentClient"));

        shell.Click(EditButton(shell, "Export tests"));
        Assert.Equal(["Claude Code", "Codex", "Pi · not installed", "Antigravity CLI · not installed"], shell.Pick("ProposalAgentClient", "Claude Code"));
        shell.Pick("ProposalAgentModel", "Claude Sonnet 5.5");
        shell.Pick("ProposalAgentReasoning", "medium");

        Assert.Equal("Claude Code · Claude Sonnet 5.5 · medium Export tests", Ghosts(shell)[2]);
        Assert.Equal(new string?[] { null, null, null }, GhostNotes(shell));
        Assert.Equal(
            ("Export tests", "Claude Code · Claude Sonnet 5.5 · medium", "Your choice. The planner chose Pi, which isn't installed.", null),
            Review(shell)[2]);
        Assert.False(shell.Find<CheckBox>("ProposalUsePlannerAgent").IsEffectivelyVisible);

        shell.Click(EditButton(shell, "Export tests"));
        Assert.False(shell.Has<ComboBox>("ProposalAgentClient"));
        shell.Click(shell.InView<Button>("AcceptProposal"));
        Assert.Equal(
            new ExecutionSettings(ClientId.ClaudeCode) { Model = "claude-sonnet-5-5", Reasoning = "medium" },
            canvas.Workflow.Tasks.Values.Single(task => task.Title == "Export tests").Execution);
    }

    [AvaloniaFact]
    public void A_choice_falls_back_until_its_client_is_ready_and_then_is_the_tasks_agent()
    {
        var shell = GeneratedChoosing();

        Install(_fakes, ClientId.Pi);
        shell.Click(shell.Find<Button>("RefreshAgents"));
        shell.WaitUntil(() => shell.Find<Button>("RefreshAgents").IsEffectivelyEnabled, "the check ends");
        shell.Render();

        Assert.Equal("Pi · deepseek · DeepSeek V4.1 Flash · low Export tests", Ghosts(shell)[2]);
        Assert.Equal(new string?[] { null, null, null }, GhostNotes(shell));
        Assert.Equal(("Export tests", "Pi · deepseek · DeepSeek V4.1 Flash · low", "Fast and cheap.", null), Review(shell)[2]);
        Assert.False(shell.Find<CheckBox>("ProposalUsePlannerAgent").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void A_Pi_model_keeps_its_level_whole_on_its_ghost_card_and_its_row_at_every_inspector_width()
    {
        Install(_fakes, ClientId.Pi);
        var shell = GeneratedChoosing();

        Assert.Equal(["Pi · deepseek", "DeepSeek V4.1 Flash · low"], GhostLines(shell)[2]);
        foreach (var width in new[] { 280.0, 320, 520 })
        {
            shell.SizeInspector(width);
            Assert.Equal("Pi · deepseek · DeepSeek V4.1 Flash · low", Review(shell)[2].Agent);
            var texts = GhostCards(shell).Concat<Visual>(ById<StackPanel>(shell, "ProposalAgent"))
                .SelectMany(root => root.GetVisualDescendants().OfType<TextBlock>()).Where(text => text.IsEffectivelyVisible);
            Assert.All(texts, text => Assert.False(text.TextLayout.TextLines.Any(line => line.HasCollapsed), $"\"{text.Text}\" is cut short at {width} px."));
        }
    }

    [AvaloniaFact]
    public void The_agent_each_row_shows_is_the_agent_Accept_writes_for_a_type_with_an_agent_of_its_own()
    {
        var implement = BuiltInBlueprints.Implement;
        var project = _temp.Create("seed");
        BlueprintLibrary.Project(project).Save(new Blueprint(new BlueprintKey("careful", 1), "Careful", implement.Work, implement.Fields,
            new NodeSettings(Haiku, ConversationMode.Autonomous)) { DerivedFrom = implement.Key });
        const string reply = """
            ```idevelop
            {"status": "proposal",
             "add": [{"id": "chosen", "type": "type-6", "title": "Chosen", "fields": {"instructions": "A."},
                      "agent": {"client": "codex", "model": "gpt-5.5", "reasoning": "low", "reason": "Routine."}},
                     {"id": "own", "type": "type-6", "title": "Own", "fields": {"instructions": "B."}},
                     {"id": "unusable", "type": "type-6", "title": "Unusable", "fields": {"instructions": "C."},
                      "agent": {"client": "pi", "model": "deepseek/deepseek-flash", "reasoning": "low"}},
                     {"id": "plain", "type": "type-1", "title": "Plain", "fields": {"instructions": "D."}}]}
            ```
            """;
        Install(_fakes, ClientId.ClaudeCode);
        Install(_fakes, ClientId.Codex, Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, reply)));
        var shell = Shell.Open(project, _fakes.DiscoverAsync().Result);
        SubmitOnCodex(shell);
        shell.WaitUntil(() => shell.Has<StackPanel>("Proposal"), "the proposal shows");
        var shown = Review(shell).ToDictionary(row => row.Title, row => row.Agent);
        var ghosts = Ghosts(shell);
        // The blueprint's own agent wins over the planner's choice, usable or not, and the row says so.
        Assert.Equal(
            [
                ("Chosen", "Claude Code · Claude Haiku 4.5 · low", "Set by the blueprint", null),
                ("Own", "Claude Code · Claude Haiku 4.5 · low", "Set by the blueprint", null),
                ("Unusable", "Claude Code · Claude Haiku 4.5 · low", "Set by the blueprint", null),
                ("Plain", "Codex · GPT-6.1-Sol · high", null, null),
            ],
            Review(shell));

        shell.Click(shell.InView<Button>("AcceptProposal"));

        string[] titles = ["Chosen", "Own", "Unusable", "Plain"];
        Assert.Equal(titles.Select(title => shown[title]), titles.Select(shell.CardAgent));
        Assert.Equal(ghosts.Order(), titles.Select(title => $"{shell.CardAgent(title)} {title}").Order());
    }

    [AvaloniaFact]
    public void The_persons_choice_on_a_row_wins_over_its_blueprints_own_agent_and_Accept_writes_it()
    {
        var implement = BuiltInBlueprints.Implement;
        var project = _temp.Create("seed");
        BlueprintLibrary.Project(project).Save(new Blueprint(new BlueprintKey("careful", 1), "Careful", implement.Work, implement.Fields,
            new NodeSettings(Haiku, ConversationMode.Autonomous)) { DerivedFrom = implement.Key });
        const string reply = """
            ```idevelop
            {"status": "proposal",
             "add": [{"id": "own", "type": "type-6", "title": "Own", "fields": {"instructions": "B."}}]}
            ```
            """;
        Install(_fakes, ClientId.ClaudeCode);
        Install(_fakes, ClientId.Codex, Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, reply)));
        var shell = Shell.Open(project, _fakes.DiscoverAsync().Result);
        SubmitOnCodex(shell);
        shell.WaitUntil(() => shell.Has<StackPanel>("Proposal"), "the proposal shows");
        Assert.Equal(("Own", "Claude Code · Claude Haiku 4.5 · low", "Set by the blueprint", null), Assert.Single(Review(shell)));

        shell.Click(EditButton(shell, "Own"));
        shell.Pick("ProposalAgentClient", "Codex");

        Assert.Equal(
            ("Own", "Codex · GPT-6.1-Sol · low", "Your choice. The blueprint sets Claude Code · Claude Haiku 4.5 · low.", null),
            Assert.Single(Review(shell)));
        Assert.Equal(["Codex · GPT-6.1-Sol · low Own"], Ghosts(shell));
        shell.Click(shell.InView<Button>("AcceptProposal"));
        Assert.Equal(
            new ExecutionSettings(ClientId.Codex) { Model = "gpt-6.1-sol", Reasoning = "low" },
            shell.Window.ViewModel.Canvas!.Workflow.Tasks.Values.Single(task => task.Title == "Own").Execution);
        Assert.Equal("Codex · GPT-6.1-Sol · low", shell.CardAgent("Own"));
    }

    [AvaloniaFact]
    public void A_client_still_being_checked_when_the_planner_starts_is_named_in_the_review()
    {
        var gate = Path.Combine(_temp.Create("gate"), "agy");
        _fakes.Install("agy", FakeRule.On("models").WaitForFile(gate).Replay(Fixture.Path("agy-models.txt")));
        var prompt = Path.Combine(_temp.Create("evidence"), "prompt.txt");
        Install(_fakes, ClientId.Codex, Fresh(ClientId.Codex).CaptureStdin(prompt).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, Reply)));
        var clients = new ClientDirectory(_fakes.Resolver);
        var codexReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clients.Changed += (_, _) =>
        {
            if (clients.Current[ClientId.Codex] is ClientStatus.Ready)
            {
                codexReady.TrySetResult();
            }
        };
        var refresh = clients.RefreshAsync();
        try
        {
            Assert.True(codexReady.Task.Wait(TimeSpan.FromSeconds(60)), "Codex is ready");
            var shell = Shell.Open(_temp.Create("seed"), clients);
            Submit(shell, Prompt);
            shell.WaitUntil(() => shell.Has<StackPanel>("Proposal"), "the proposal shows");

            Assert.IsType<ClientStatus.Checking>(clients.Current[ClientId.Antigravity]);
            Assert.DoesNotContain("antigravity", File.ReadAllText(prompt));
            Assert.Equal(
                "Antigravity CLI was still being checked when the planner started, so the planner didn't consider it.",
                shell.InView<TextBlock>("ProposalCheckingNote").Text);
        }
        finally
        {
            File.WriteAllText(gate, "");
            refresh.Wait(TimeSpan.FromSeconds(60));
        }
    }

    [AvaloniaFact]
    public void Without_a_client_still_being_checked_the_review_has_no_such_note()
    {
        var shell = GeneratedChoosing();

        Assert.False(shell.Find<TextBlock>("ProposalCheckingNote").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void A_click_on_a_tasks_agent_or_reason_leaves_its_box_as_it_was()
    {
        var shell = GeneratedChoosing();
        var entry = ById<StackPanel>(shell, "ProposalEntry").First();
        var box = Shell.ById<CheckBox>(entry, "ProposalItem").Single();

        var title = Shell.ById<TextBlock>(entry, "ProposalItemTitle").Single();
        var edit = Shell.ById<Button>(entry, "ProposalAgentEdit").Single();
        entry.BringIntoView();
        shell.Render();
        Assert.True(shell.Window.InputHitTest(shell.Center(title)) is Visual hit && box.IsVisualAncestorOf(hit), "the title is in view");
        shell.Click(Shell.ById<StackPanel>(entry, "ProposalAgent").Single());
        shell.Click(Shell.ById<TextBlock>(entry, "ProposalAgentReason").Single());
        // The empty end of the title's line, before the Change button, is no part of the box either.
        shell.Click(new Point(shell.Bounds(edit).Left - 24, shell.Center(title).Y));
        Assert.True(box.IsChecked);

        shell.Click(title);
        Assert.False(box.IsChecked);
    }

    [AvaloniaFact]
    public void Only_one_tasks_agent_is_open_for_change_at_a_time()
    {
        var shell = GeneratedChoosing();

        shell.Click(EditButton(shell, "Export API"));
        shell.Click(EditButton(shell, "Export button"));

        Assert.Equal("Claude Code", shell.Picked("ProposalAgentClient"));
        Assert.Equal("Claude Haiku 4.5", shell.Picked("ProposalAgentModel"));
    }

    [AvaloniaFact]
    public void A_task_whose_type_takes_no_agent_shows_none_and_the_box_waits_for_a_task_without_a_choice()
    {
        const string reply = """
            ```idevelop
            {"status": "proposal",
             "add": [{"id": "api", "type": "type-1", "title": "Export API", "fields": {"instructions": "Add the CSV endpoint."},
                      "agent": {"client": "codex", "model": "gpt-5.5", "reasoning": "low", "reason": "Routine."}},
                     {"id": "sign", "type": "type-5", "title": "Sign off", "agent": {"client": "codex", "model": "gpt-5.5", "reasoning": "low"}}],
             "connect": [{"from": "api", "to": "sign"}]}
            ```
            """;
        var shell = OpenEmpty(Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, reply)));
        Submit(shell, Prompt);
        shell.WaitUntil(() => shell.Has<StackPanel>("Proposal"), "the proposal shows");

        Assert.Equal(["Codex · GPT-5.5 · low Export API", "New Approval Sign off"], Ghosts(shell));
        Assert.Equal([("Export API", "Codex · GPT-5.5 · low", "Routine.", null), ("Sign off", null, null, null)], Review(shell));
        Assert.False(shell.Find<CheckBox>("ProposalUsePlannerAgent").IsEffectivelyVisible);
        Assert.Single(ById<Button>(shell, "ProposalAgentEdit"));
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

    /// <summary>Generates from an empty project with Codex and Claude Code ready, and waits for <see cref="ChoosingReply"/>.</summary>
    private Shell GeneratedChoosing()
    {
        Install(_fakes, ClientId.ClaudeCode);
        var shell = OpenEmpty(Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, ChoosingReply)));
        SubmitOnCodex(shell);
        shell.WaitUntil(() => shell.Has<StackPanel>("Proposal"), "the proposal shows");
        return shell;
    }

    /// <summary>Generates <see cref="Prompt"/> with Codex as the planner, which the sheet would not pick while Claude Code is ready.</summary>
    private static void SubmitOnCodex(Shell shell)
    {
        OpenSheet(shell);
        shell.Type(Prompt);
        shell.Pick("GenerateClient", "Codex");
        shell.Click(shell.Find<Button>("GenerateSubmit"));
    }

    private static IEnumerable<T> ById<T>(Shell shell, string automationId) where T : Control =>
        Shell.ById<T>(shell.Window, automationId).Where(control => control.IsEffectivelyVisible);

    /// <summary>Each proposed task in the review: its title, the agent it takes, the reason shown, and the note on its choice.</summary>
    private static (string Title, string? Agent, string? Reason, string? Note)[] Review(Shell shell) =>
        [.. ById<StackPanel>(shell, "ProposalEntry").Select(entry => (
            Shell.TextOf(Shell.ById<TextBlock>(entry, "ProposalItemTitle").Single()),
            AgentLines(entry),
            Visible(entry, "ProposalAgentReason"),
            Visible(entry, "ProposalAgentNote")))];

    /// <summary>A proposed task's agent on its row: the client's line, then the model and level's, joined as one label.</summary>
    private static string? AgentLines(Visual entry) =>
        Shell.ById<StackPanel>(entry, "ProposalAgent").SingleOrDefault(agent => agent.IsEffectivelyVisible) is { } agent
            ? string.Join(" · ", Shell.Texts(agent))
            : null;

    private static string? Visible(Visual root, string automationId) =>
        Shell.ById<TextBlock>(root, automationId).SingleOrDefault(text => text.IsEffectivelyVisible && !string.IsNullOrEmpty(text.Text))?.Text;

    /// <summary>The button that opens or closes the agent of the proposed task with this title.</summary>
    private static Button EditButton(Shell shell, string title)
    {
        var entry = ById<StackPanel>(shell, "ProposalEntry").Single(entry => Shell.TextOf(Shell.ById<TextBlock>(entry, "ProposalItemTitle").Single()) == title);
        var button = Shell.ById<Button>(entry, "ProposalAgentEdit").Single();
        button.BringIntoView();
        shell.Render();
        return button;
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

    private static IEnumerable<StepConnection> GhostWireLines(Shell shell) =>
        shell.Window.GetVisualDescendants().OfType<StepConnection>().Where(wire => AutomationProperties.GetAutomationId(wire) == "GhostWire");

    /// <summary>Each ghost wire's two ends on the canvas.</summary>
    private static (Point From, Point To)[] GhostWires(Shell shell) =>
        [.. GhostWireLines(shell)
            .Select(wire => shell.Ends(wire))
            .Select(ends => (From: Shell.Rounded(shell.CanvasPointAt(ends.Source)), To: Shell.Rounded(shell.CanvasPointAt(ends.Target))))
            .OrderBy(wire => (wire.From.X, wire.From.Y, wire.To.X, wire.To.Y))];

    private static string? ButtonLabel(Shell shell) => AutomationProperties.GetName(shell.Find<Button>("GenerateWorkflow"));

    private static IEnumerable<Panel> GhostCards(Shell shell) =>
        shell.Window.GetVisualDescendants().OfType<Panel>().Where(panel => AutomationProperties.GetAutomationId(panel) == "GhostCard")
            .OrderBy(panel => panel.TranslatePoint(default, shell.Editor)!.Value.X).ThenBy(panel => panel.TranslatePoint(default, shell.Editor)!.Value.Y);

    /// <summary>Each ghost card's subtitle, its two lines joined as one label, then its title.</summary>
    private static string[] Ghosts(Shell shell) =>
        [.. GhostCards(shell).Select(panel => $"{string.Join(" · ", new[] { Visible(panel, "GhostLabel"), Visible(panel, "GhostDetail") }.OfType<string>())} {AutomationProperties.GetName(panel)}")];

    /// <summary>Each ghost card's subtitle lines, in the order of <see cref="Ghosts"/>.</summary>
    private static string[][] GhostLines(Shell shell) =>
        [.. GhostCards(shell).Select(panel => new[] { Visible(panel, "GhostLabel"), Visible(panel, "GhostDetail") }.OfType<string>().ToArray())];

    /// <summary>The note under each ghost card, in the order of <see cref="Ghosts"/>, or null for a card without one.</summary>
    private static string?[] GhostNotes(Shell shell) => [.. GhostCards(shell).Select(panel => Visible(panel, "GhostNote"))];
}
