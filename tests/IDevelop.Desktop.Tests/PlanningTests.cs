using System.Collections.Immutable;
using Avalonia.Automation;
using Avalonia.Controls.Primitives;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Avalonia;
using IDevelop.Clients;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Theme;
using IDevelop.Projects;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.AppTempFolder;
using static IDevelop.TestSupport.FakeAgents;

namespace IDevelop.Desktop.Tests;

/// <summary>A planner's proposal as ghost cards and an inspector section, through the real main window and the fake agent.</summary>
[Collection(ProcessCollection.Name)]
public sealed class PlanningTests : IDisposable
{
    private const string Session = "01a108d7-464d-77a3-8908-a36f38ce6c14";
    private static readonly TaskId Architect = TestTasks.Design;
    private static readonly TaskId Backend = TestTasks.Build;
    private static readonly TaskId Frontend = TestTasks.Review;
    private static readonly ExecutionSettings Codex = new(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "high" };

    private readonly TempFolder _temp = AppTempFolder.New();
    private readonly FakeClients _fakes;

    public PlanningTests() => _fakes = new FakeClients(_temp.Create("bin"));

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData(3, 0, "Added 3 tasks.")]
    [InlineData(0, 1, "Filled 1 task.")]
    [InlineData(1, 2, "Added 1 task and filled 2 tasks.")]
    public void The_accept_notice_names_only_what_accepting_did(int added, int filled, string notice)
    {
        Assert.Equal(notice, IDevelop.Desktop.Canvas.ProposalViewModel.AcceptedNotice(added, filled));
    }

    [AvaloniaFact]
    public void A_Chat_Architect_shows_ghost_cards_after_its_first_turn_and_Accept_applies_the_chosen_ones()
    {
        var shell = RunArchitect("""
            Here is the design.

            ```idevelop
            {"status": "proposal",
             "fill": [{"slot": "slot-1", "title": "Backend API", "fields": {"instructions": "Add the endpoint."}},
                      {"slot": "slot-2", "fields": {"instructions": "Add the button."}}],
             "add": [{"id": "wire", "type": "type-1", "title": "Wire export", "fields": {"instructions": "Connect them."}}],
             "connect": [{"from": "slot-1", "to": "wire"}, {"from": "slot-2", "to": "wire"}]}
            ```
            """);

        Assert.Equal("Proposal · 3 tasks", shell.CardText("Design export", "CardStatus"));
        Assert.Equal(["Fills Backend API", "Fills Frontend", "New Implement Wire export"], Ghosts(shell));
        Assert.Equal(["Fill \"Backend\" as \"Backend API\"", "Fill \"Frontend\"", "Add Implement \"Wire export\""], Items(shell).Select(AutomationProperties.GetName));
        Assert.Equal(["Backend API Fills Backend", "Frontend Fills Frontend", "Wire export"], Items(shell).Select(Shell.TextOf));
        shell.Click(shell.InView<ToggleButton>("ProposalConnectionsToggle"));
        Assert.Equal(["Backend API → Wire export", "Frontend → Wire export"], Shell.Texts(shell.Find<ItemsControl>("ProposalConnections")));

        var frontend = Items(shell).Single(box => AutomationProperties.GetName(box) == "Fill \"Frontend\"");
        frontend.BringIntoView();
        shell.Render();
        shell.Click(frontend);
        Assert.Equal(["Fills Backend API", "New Implement Wire export"], Ghosts(shell));
        Assert.Equal(["Backend API → Wire export"], Shell.Texts(shell.Find<ItemsControl>("ProposalConnections")));
        shell.Click(shell.InView<Button>("AcceptProposal"));

        Assert.Empty(Ghosts(shell));
        Assert.False(shell.Has<StackPanel>("Proposal"));
        Assert.Equal("Added 1 task and filled 1 task.", shell.Status);
        Assert.True(shell.ShowsUnsavedChanges);
        var workflow = shell.Window.ViewModel.Canvas!.Workflow;
        Assert.Equal(("Backend API", "Add the endpoint.", ""), (workflow.Tasks[Backend].Title, workflow.Tasks[Backend].Field("instructions"), workflow.Tasks[Frontend].Field("instructions")));
        var wire = Assert.Single(workflow.Tasks.Values, task => task.Title == "Wire export");
        Assert.Equal([(Architect, Backend), (Architect, Frontend), (Backend, wire.Id)], workflow.Connections.Keys.Select(key => (key.From, key.To)).Order());
    }

    [AvaloniaFact]
    public void A_proposals_ghost_cards_and_wires_follow_the_cards_where_they_show_while_the_planner_is_expanded()
    {
        var shell = RunArchitect("""
            ```idevelop
            {"status": "proposal",
             "fill": [{"slot": "slot-1", "title": "Backend API", "fields": {"instructions": "Add the endpoint."}}],
             "add": [{"id": "wire", "type": "type-1", "title": "Wire export", "fields": {"instructions": "Connect them."}}],
             "connect": [{"from": "slot-1", "to": "wire"}]}
            ```
            """);
        var canvas = shell.Window.ViewModel.Canvas!;
        GhostCardViewModel Card(string title) => canvas.Ghosts.OfType<GhostCardViewModel>().Single(ghost => ghost.Title == title);
        var newCard = Card("Wire export").Location;
        var wireStart = canvas.Ghosts.OfType<GhostWireViewModel>().Select(wire => wire.Location + (Vector)wire.Source).Single();

        shell.Click(shell.InCard<ToggleButton>("Design export", "CardExpand"));

        // The planner unfolds 80 px wider, so its slot and the column of new tasks right of it show 80 px further right.
        var backend = canvas.Nodes.Single(node => node.Title == "Backend");
        var extra = WorkflowCanvasViewModel.ExpandedCardWidth - WorkflowCanvasViewModel.TaskCardWidth;
        Assert.Equal(new Vector(extra, 0), backend.ViewOffset);
        Assert.Equal(backend.DisplayLocation, Card("Backend API").Location);
        Assert.Equal(newCard + new Vector(extra, 0), Card("Wire export").Location);
        var wire = canvas.Ghosts.OfType<GhostWireViewModel>().Single();
        Assert.Equal((wireStart + new Vector(extra, 0), Card("Wire export").Location + (Vector)WorkflowCanvasViewModel.InputPortCenter),
            (wire.Location + (Vector)wire.Source, wire.Location + (Vector)wire.Target));
    }

    [AvaloniaFact]
    public void A_proposal_that_would_close_a_cycle_says_so_and_cannot_be_accepted()
    {
        var shell = RunArchitect("""
            ```idevelop
            {"status": "proposal", "add": [{"id": "check", "type": "type-1", "title": "Check"}],
             "connect": [{"from": "slot-1", "to": "check"}, {"from": "check", "to": "planner"}]}
            ```
            """);

        Assert.Equal(["New Implement Check"], Ghosts(shell));
        Assert.Equal(
            "Accept would be refused. That would create a cycle: Check → Design export → Backend → Check.",
            shell.InView<TextBlock>("ProposalProblem").Text);
        Assert.False(shell.Find<Button>("AcceptProposal").IsEffectivelyEnabled);

        shell.Click(shell.InView<Button>("DismissProposal"));

        Assert.Empty(Ghosts(shell));
        Assert.Equal(3, WorkflowDocument.OpenProject(shell.Window.ViewModel.Canvas!.Document.ProjectFolder).Single().Current.Tasks.Count);
    }

    [AvaloniaFact]
    public void A_continuation_that_proposes_nothing_keeps_the_proposal_before_it()
    {
        var shell = RunArchitect(
            """
            ```idevelop
            {"status": "proposal", "add": [{"id": "wire", "type": "type-1", "title": "Wire export"}]}
            ```
            """,
            ConversationMode.Autonomous,
            Resuming(ClientId.Codex, Session).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, "The wire task calls the endpoint.")));
        shell.WaitUntil(() => shell.InView<TextBlock>("RunTaskStatus").Text == "Completed", "the first run succeeds");

        shell.Click(shell.InView<TextBox>("Composer"));
        shell.Type("Why the wire task?");
        shell.Click(shell.InView<Button>("SendMessage"));

        shell.WaitUntil(() => shell.InView<TextBlock>("RunTaskStatus").Text == "Completed" && shell.Find<TextBox>("LastReply").Text == "The wire task calls the endpoint.", "the continuation succeeds");
        Assert.Equal(["New Implement Wire export"], Ghosts(shell));
        Assert.Equal(["Add Implement \"Wire export\""], Items(shell).Select(AutomationProperties.GetName));
    }

    [AvaloniaFact]
    public void A_fill_of_a_task_that_has_started_cannot_be_ticked_and_Accept_leaves_the_task_as_it_is()
    {
        var shell = RunArchitect("""
            ```idevelop
            {"status": "proposal",
             "fill": [{"slot": "slot-1", "title": "Backend API", "fields": {"instructions": "Add the endpoint."}}],
             "add": [{"id": "wire", "type": "type-1", "title": "Wire export"}],
             "connect": [{"from": "slot-1", "to": "wire"}]}
            ```
            """);
        var canvas = shell.Window.ViewModel.Canvas!;
        // Backend runs on its own, so it waits for no planner.
        canvas.Edit(new WorkflowEdit.Batch([new WorkflowEdit.SetExecution(Backend, Codex), new WorkflowEdit.SetField(Backend, "instructions", "Say hi."),
            new WorkflowEdit.Delete([], [new ConnectionKey(Architect, Backend)])]));
        shell.Click(shell.Header(shell.Node("Backend")));
        shell.RunNode();
        shell.WaitUntil(() => shell.CardText("Backend", "CardStatus") == "Completed", "the drawn task runs");
        shell.Click(shell.Header(shell.Node("Design export")));

        var backend = Items(shell).First();
        Assert.Equal(("Fill \"Backend\" as \"Backend API\". It has started, so it stays as it is", false, false), (AutomationProperties.GetName(backend), backend.IsChecked, backend.IsEffectivelyEnabled));
        Assert.Equal(["New Implement Wire export"], Ghosts(shell));
        canvas.Edit(new WorkflowEdit.SetField(Backend, "instructions", ""));
        Assert.Equal([Frontend], canvas.PlanningFor(Architect).Slots.Select(slot => slot.Id));
        canvas.Edit(new WorkflowEdit.SetField(Backend, "instructions", "Say hi."));
        shell.Click(shell.InView<Button>("AcceptProposal"));

        var workflow = canvas.Workflow;
        Assert.Equal(("Backend", "Say hi."), (workflow.Tasks[Backend].Title, workflow.Tasks[Backend].Field("instructions")));
        var wire = Assert.Single(workflow.Tasks.Values, task => task.Title == "Wire export");
        Assert.DoesNotContain(new ConnectionKey(Backend, wire.Id), workflow.Connections.Keys);
    }

    [AvaloniaFact]
    public void A_fill_of_a_task_without_an_agent_shows_the_planners_choice_and_Accept_gives_it_to_the_task()
    {
        var shell = RunArchitect("""
            ```idevelop
            {"status": "proposal",
             "fill": [{"slot": "slot-1", "title": "Backend API", "fields": {"instructions": "Add the endpoint."},
                       "agent": {"client": "codex", "model": "gpt-6.1-sol", "reasoning": "low", "reason": "A small endpoint."}},
                      {"slot": "slot-2", "fields": {"instructions": "Add the button."},
                       "agent": {"client": "pi", "model": "deepseek/deepseek-flash", "reasoning": "low", "reason": "Fast and cheap."}}]}
            ```
            """);

        Assert.Equal(
            [
                ("Backend API", "Codex · GPT-6.1-Sol · low", "A small endpoint.", null),
                ("Frontend", "No agent", null, "The planner chose Pi, which isn't installed. Choose the task's agent before it runs."),
            ],
            GenerateTests.Review(shell));
        Assert.Equal(["Fills · GPT-6.1-Sol · low Backend API", "Fills Frontend"], GenerateTests.Ghosts(shell));
        Assert.Equal(new string?[] { null, "Pi isn't installed" }, GenerateTests.GhostNotes(shell));
        // Frontend has no usable choice, so the box offers it the planner's agent, which a planner the person placed leaves off.
        Assert.True(shell.InView<CheckBox>("ProposalUsePlannerAgent").IsEffectivelyVisible);
        Assert.False(shell.InView<CheckBox>("ProposalUsePlannerAgent").IsChecked);

        shell.Click(shell.InView<Button>("AcceptProposal"));

        var workflow = shell.Window.ViewModel.Canvas!.Workflow;
        Assert.Equal(new ExecutionSettings(ClientId.Codex) { Model = "gpt-6.1-sol", Reasoning = "low" }, workflow.Tasks[Backend].Execution);
        Assert.Null(workflow.Tasks[Frontend].Execution);
        shell.ShowTasks();
        Assert.Equal("Codex · GPT-6.1-Sol · low", shell.CardAgent("Backend API"));
    }

    [AvaloniaFact]
    public void A_fill_keeps_the_agent_its_task_has_and_the_person_changes_a_fills_agent_before_accepting()
    {
        var shell = RunArchitect("""
            ```idevelop
            {"status": "proposal",
             "fill": [{"slot": "slot-1", "title": "Backend API", "fields": {"instructions": "Add the endpoint."},
                       "agent": {"client": "codex", "model": "gpt-6.1-sol", "reasoning": "low", "reason": "A small endpoint."}},
                      {"slot": "slot-2", "fields": {"instructions": "Add the button."},
                       "agent": {"client": "codex", "model": "gpt-6.1-sol", "reasoning": "low", "reason": "A small button."}}]}
            ```
            """);
        var canvas = shell.Window.ViewModel.Canvas!;

        // The person gives Frontend an agent of its own while the proposal waits, and the fill keeps it.
        canvas.Edit(new WorkflowEdit.SetExecution(Frontend, Codex));
        shell.Render();
        Assert.Equal(("Frontend", "Codex · GPT-5.5 · high", "Set on the task", null), GenerateTests.Review(shell)[1]);
        Assert.Equal("Fills · GPT-5.5 · high Frontend", GenerateTests.Ghosts(shell)[1]);
        shell.Click(GenerateTests.EditButton(shell, "Backend API"));
        shell.Pick("ProposalAgentModel", "GPT-5.5");
        shell.Pick("ProposalAgentReasoning", "medium");
        Assert.Equal(("Backend API", "Codex · GPT-5.5 · medium", "Your choice. The planner chose Codex · GPT-6.1-Sol · low.", null),
            GenerateTests.Review(shell)[0]);
        shell.Click(GenerateTests.EditButton(shell, "Frontend"));
        shell.Pick("ProposalAgentReasoning", "low");
        Assert.Equal(("Frontend", "Codex · GPT-5.5 · low", "Your choice. The task has Codex · GPT-5.5 · high.", null), GenerateTests.Review(shell)[1]);

        shell.Click(shell.InView<Button>("AcceptProposal"));

        Assert.Equal(
            (new ExecutionSettings(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "medium" }, Codex with { Reasoning = "low" }),
            (canvas.Workflow.Tasks[Backend].Execution, canvas.Workflow.Tasks[Frontend].Execution));
    }

    [AvaloniaFact]
    public void A_ticked_box_gives_the_planners_agent_to_a_new_task_and_a_fill_without_a_usable_choice()
    {
        var shell = RunArchitect("""
            ```idevelop
            {"status": "proposal",
             "fill": [{"slot": "slot-2", "fields": {"instructions": "Add the button."},
                       "agent": {"client": "pi", "model": "deepseek/deepseek-flash", "reasoning": "low", "reason": "Fast and cheap."}}],
             "add": [{"id": "wire", "type": "type-1", "title": "Wire export", "fields": {"instructions": "Connect them."}}],
             "connect": [{"from": "slot-2", "to": "wire"}]}
            ```
            """);
        var canvas = shell.Window.ViewModel.Canvas!;

        shell.Click(shell.InView<CheckBox>("ProposalUsePlannerAgent"));

        Assert.Equal(
            [
                ("Frontend", "Codex · GPT-5.5 · high", null, "The planner chose Pi, which isn't installed. The task takes the planner's agent instead."),
                ("Wire export", "Codex · GPT-5.5 · high", null, null),
            ],
            GenerateTests.Review(shell));
        Assert.Equal(["Fills · GPT-5.5 · high Frontend", "Codex · GPT-5.5 · high Wire export"], GenerateTests.Ghosts(shell));
        shell.Click(shell.InView<Button>("AcceptProposal"));
        Assert.Equal(Codex, canvas.Workflow.Tasks[Frontend].Execution);
        Assert.Equal(Codex, Assert.Single(canvas.Workflow.Tasks.Values, task => task.Title == "Wire export").Execution);
    }

    [AvaloniaFact]
    public void A_fill_of_an_Approval_shows_no_agent()
    {
        var signed = new TaskId(Guid.Parse("019a9d2e-5d10-7a99-8c11-0b9f2e7d4a44"));
        var shell = RunArchitect("""
            ```idevelop
            {"status": "proposal",
             "fill": [{"slot": "slot-3", "fields": {"checklist": "The export opens."},
                       "agent": {"client": "codex", "model": "gpt-6.1-sol", "reasoning": "low", "reason": "Quick."}}]}
            ```
            """, ConversationMode.Chat, [],
            [
                new WorkflowEdit.PlaceNode(signed, BuiltInBlueprints.Approval, new CanvasPoint(465, 510)) { Title = "Sign off" },
                new WorkflowEdit.Connect(new ConnectionKey(Architect, signed), ConnectionKind.Dependency),
            ]);

        Assert.Equal([("Sign off", null, null, null)], GenerateTests.Review(shell));
        Assert.Equal(["Fills Sign off"], GenerateTests.Ghosts(shell));
        Assert.False(shell.Find<Button>("ProposalAgentEdit").IsEffectivelyVisible);
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void A_fill_s_ghost_card_hides_the_empty_card_under_it(string theme)
    {
        var shell = RunArchitect("""
            ```idevelop
            {"status": "proposal",
             "fill": [{"slot": "slot-2", "fields": {"instructions": "Add the button."}}]}
            ```
            """);
        shell.Window.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        shell.Render();
        var ghost = shell.Window.GetVisualDescendants().OfType<Panel>().Single(panel => AutomationProperties.GetAutomationId(panel) == "GhostCard");
        var surface = ghost.GetVisualDescendants().OfType<Border>().Single(border => border.Classes.Contains("ghost"));
        var color = Assert.IsAssignableFrom<ISolidColorBrush>(surface.Background).Color;

        // The ghost's surface, inside its dashed edge and its rounded corners, less what the ghost draws on it: its kind
        // tile and its text. Every other pixel is the surface's own color, so the empty card's subtitle and its warning
        // glyph under it do not show through (#85).
        var inside = Drawn(surface).Deflate(new Thickness(14, 4));
        Rect[] drawn =
        [
            Drawn(ghost.GetVisualDescendants().OfType<KindTile>().Single()).Inflate(2),
            .. ghost.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible && !string.IsNullOrEmpty(text.Text)).Select(text =>
            {
                var bounds = Drawn(text);
                return new Rect(bounds.X, bounds.Y, text.TextLayout.Width * bounds.Width / text.Bounds.Width, bounds.Height).Inflate(2);
            }),
        ];
        var area = new Rect(Math.Ceiling(inside.X), Math.Ceiling(inside.Y), Math.Floor(inside.Width), Math.Floor(inside.Height));
        var rows = shell.PixelRows(area);
        var through = Enumerable.Range(0, rows.Length).SelectMany(y => Enumerable.Range(0, rows[y].Length).Select(x => (Point: new Point(area.X + x + 0.5, area.Y + y + 0.5), Pixel: rows[y][x])))
            .Where(sample => !drawn.Any(rect => rect.Contains(sample.Point)))
            .Select(sample => (sample.Point, Off: Math.Max(Math.Abs(sample.Pixel.R - color.R), Math.Max(Math.Abs(sample.Pixel.G - color.G), Math.Abs(sample.Pixel.B - color.B)))))
            .MaxBy(sample => sample.Off);

        Assert.Equal(255, color.A);
        Assert.True(through.Off <= 2, $"The card under the ghost shows through at {through.Point}, {through.Off} off the ghost's surface.");

        // Where the visual draws in the window, at the canvas's zoom.
        Rect Drawn(Visual visual) => new(visual.TranslatePoint(default, shell.Window)!.Value, visual.TranslatePoint(new Point(visual.Bounds.Width, visual.Bounds.Height), shell.Window)!.Value);
    }

    [AvaloniaFact]
    public void A_planner_may_place_every_blueprint_the_palette_offers()
    {
        var project = _temp.Seed(new WorkflowEdit.PlaceNode(Architect, BuiltInBlueprints.Architect, new CanvasPoint(105, 90)) { Title = "Design export" });
        var bugFix = new Blueprint(
            new BlueprintKey("bug-fix-0a1b2c3d", 1), "Bug fix",
            new WorkSpec.Agent(AgentAccess.Edit, Proposes: false, PromptTemplate.Parse("Fix {{bug}}.")),
            [new FieldSpec("bug", "Bug", FieldShape.Text, Required: true, "")],
            new NodeSettings(null, ConversationMode.Autonomous));
        BlueprintLibrary.Project(project).Save(bugFix);

        var canvas = Shell.Open(project).Window.ViewModel.Canvas!;

        Assert.Equal(
            [.. BuiltInBlueprints.All.Select(blueprint => blueprint.Key), bugFix.Key],
            canvas.PlanningFor(Architect).Types.Select(type => type.Key));
        Assert.Equal(bugFix, canvas.FindBlueprint(bugFix.Key));
    }

    /// <summary>Opens an Architect with two empty tasks after it, runs it, and waits for its first reply.</summary>
    private Shell RunArchitect(string reply, ConversationMode mode = ConversationMode.Chat, params FakeRule[] later) =>
        RunArchitect(reply, mode, later, []);

    /// <summary>Opens an Architect with two empty tasks after it and <paramref name="extra"/>, runs it, and waits for its first reply.</summary>
    private Shell RunArchitect(string reply, ConversationMode mode, FakeRule[] later, WorkflowEdit[] extra)
    {
        Install(_fakes, ClientId.Codex, [.. later, Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, reply))]);
        var project = _temp.Seed(
        [
            new WorkflowEdit.PlaceNode(Architect, BuiltInBlueprints.Architect, new CanvasPoint(105, 90))
            {
                Title = "Design export",
                Fields = ImmutableDictionary<string, string>.Empty.Add("brief", "Export the report as CSV."),
                Settings = new NodeSettings(Codex, mode),
            },
            TaskAt(Backend, "Backend", 465, 90),
            TaskAt(Frontend, "Frontend", 465, 300),
            new WorkflowEdit.Connect(new ConnectionKey(Architect, Backend), ConnectionKind.Dependency),
            new WorkflowEdit.Connect(new ConnectionKey(Architect, Frontend), ConnectionKind.Dependency),
            .. extra,
        ]);
        var shell = Shell.Open(project, _fakes.DiscoverAsync().Result);
        shell.Click(shell.Header(shell.Node("Design export")));
        shell.RunNode();
        shell.WaitUntil(() => shell.Has<StackPanel>("Proposal"), "the proposal shows");
        return shell;
    }

    private static string[] Ghosts(Shell shell) =>
        [.. shell.Window.GetVisualDescendants().OfType<Panel>()
            .Where(panel => AutomationProperties.GetAutomationId(panel) == "GhostCard")
            .Select(panel => $"{Shell.TextOf(panel.GetVisualDescendants().OfType<TextBlock>().Single(text => AutomationProperties.GetAutomationId(text) == "GhostLabel"))} {AutomationProperties.GetName(panel)}")];

    private static IEnumerable<CheckBox> Items(Shell shell) =>
        shell.Window.GetVisualDescendants().OfType<CheckBox>().Where(box => AutomationProperties.GetAutomationId(box) == "ProposalItem");
}
