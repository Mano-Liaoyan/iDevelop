using System.Collections.Immutable;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
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
        shell.WaitUntil(() => shell.InView<TextBlock>("LastRunStatus").Text == "Succeeded", "the first run succeeds");

        shell.Click(shell.InView<TextBox>("Composer"));
        shell.Type("Why the wire task?");
        shell.Click(shell.InView<Button>("SendMessage"));

        shell.WaitUntil(() => shell.InView<TextBlock>("LastRunStatus").Text == "Succeeded" && shell.Find<TextBox>("LastRunResult").Text == "The wire task calls the endpoint.", "the continuation succeeds");
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
        canvas.Edit(new WorkflowEdit.Batch([new WorkflowEdit.SetExecution(Backend, Codex), new WorkflowEdit.SetField(Backend, "instructions", "Say hi.")]));
        shell.Click(shell.Header(shell.Node("Backend")));
        shell.RunOnItsOwn();
        shell.WaitUntil(() => shell.CardText("Backend", "CardStatus") == "Succeeded", "the drawn task runs");
        shell.Click(shell.Header(shell.Node("Design export")));

        var backend = Items(shell).First();
        Assert.Equal(("Fill \"Backend\" as \"Backend API\". It has started, so it stays as it is", false, false), (AutomationProperties.GetName(backend), backend.IsChecked, backend.IsEffectivelyEnabled));
        Assert.Equal(["New Implement Wire export"], Ghosts(shell));
        canvas.Edit(new WorkflowEdit.SetField(Backend, "instructions", ""));
        Assert.Equal([Frontend], canvas.Planning(Architect).Slots.Select(slot => slot.Id));
        canvas.Edit(new WorkflowEdit.SetField(Backend, "instructions", "Say hi."));
        shell.Click(shell.InView<Button>("AcceptProposal"));

        var workflow = canvas.Workflow;
        Assert.Equal(("Backend", "Say hi."), (workflow.Tasks[Backend].Title, workflow.Tasks[Backend].Field("instructions")));
        var wire = Assert.Single(workflow.Tasks.Values, task => task.Title == "Wire export");
        Assert.DoesNotContain(new ConnectionKey(Backend, wire.Id), workflow.Connections.Keys);
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
            canvas.Planning(Architect).Types.Select(type => type.Key));
        Assert.Equal(bugFix, canvas.FindBlueprint(bugFix.Key));
    }

    /// <summary>Opens an Architect with two empty tasks after it, runs it, and waits for its first reply.</summary>
    private Shell RunArchitect(string reply, ConversationMode mode = ConversationMode.Chat, params FakeRule[] later)
    {
        Install(_fakes, ClientId.Codex, [.. later, Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, reply))]);
        var project = _temp.Seed(
            new WorkflowEdit.PlaceNode(Architect, BuiltInBlueprints.Architect, new CanvasPoint(105, 90))
            {
                Title = "Design export",
                Fields = ImmutableDictionary<string, string>.Empty.Add("brief", "Export the report as CSV."),
                Settings = new NodeSettings(Codex, mode),
            },
            TaskAt(Backend, "Backend", 465, 90),
            TaskAt(Frontend, "Frontend", 465, 300),
            new WorkflowEdit.Connect(new ConnectionKey(Architect, Backend), ConnectionKind.Dependency),
            new WorkflowEdit.Connect(new ConnectionKey(Architect, Frontend), ConnectionKind.Dependency));
        var shell = Shell.Open(project, _fakes.DiscoverAsync().Result);
        shell.Click(shell.Header(shell.Node("Design export")));
        shell.RunOnItsOwn();
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
