using System.Collections.Immutable;
using System.Text.Json;
using IDevelop.Execution;
using IDevelop.Nodes;
using IDevelop.Projects;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.TestSupport.FakeAgents;
using static IDevelop.Workflows.WorkflowEdit;

namespace IDevelop.Core.Tests;

/// <summary>Planners propose graph edits through scripted replies of the fake agent, and the person accepts them.</summary>
[Collection(ProcessCollection.Name)]
public sealed class PlanningTests : IDisposable
{
    private const string Session = "01a108d7-464d-77a3-8908-a36f38ce6c14";
    private static readonly TaskId Architect = TestTasks.Design;
    private static readonly TaskId Backend = TestTasks.Build;
    private static readonly TaskId Frontend = TestTasks.Review;
    private static readonly ExecutionSettings Sol = new(ClientId.Codex) { Model = "gpt-6-sol", Reasoning = "high" };
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);
    private static readonly Guid Plan = Guid.Parse("019a9d2e-6100-7000-8000-000000000088");

    private const string FillsBothAndAddsOne = """
        The design splits into a backend and a frontend, which a third task wires together.

        ```idevelop
        {"status": "proposal",
         "fill": [
           {"slot": "slot-1", "title": "Backend API", "fields": {"instructions": "Add the /export endpoint."}},
           {"slot": "slot-2", "fields": {"instructions": "Add the export button.", "acceptanceCriteria": "The button downloads a file."}}
         ],
         "add": [{"id": "wire", "type": "type-1", "title": "Wire export", "fields": {"instructions": "Call the endpoint from the button."}}],
         "connect": [{"from": "slot-1", "to": "wire"}, {"from": "slot-2", "to": "wire"}, {"from": "planner", "to": "slot-1"}]}
        ```
        """;

    private readonly TempFolder _temp = new();
    private readonly FakeClients _fakes;
    private readonly string _project;
    private readonly string _evidence;

    public PlanningTests()
    {
        _fakes = new FakeClients(_temp.Create("bin"));
        _project = _temp.Create("project");
        _evidence = _temp.Create("evidence");
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task An_Architect_with_two_drawn_empty_nodes_fills_both_and_adds_one()
    {
        Install(_fakes, ClientId.Codex,
            Fresh(ClientId.Codex).CaptureStdin(Evidence("prompt.txt")).RecordArguments(Evidence("arguments.json"))
                .Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, FillsBothAndAddsOne)));
        var clients = await _fakes.DiscoverAsync();
        var workflow = ArchitectWithTwoSlots(ConversationMode.Autonomous);
        AttemptRecord record;
        await using (var runs = ProjectRuns.Open(_project, clients))
        {
            record = await Settles(runs, () => runs.Start(workflow.Tasks[Architect], PlanningContext.For(workflow, Architect, BuiltInBlueprints.All, _ => false)));
        }

        var prompt = File.ReadAllText(Evidence("prompt.txt"));
        Assert.StartsWith("# Design export\n\nDesign how to build what the brief below asks for.", prompt);
        Assert.Contains("- slot-1: \"Backend\", type Implement. Fields: instructions (Instructions, required), acceptanceCriteria (Acceptance criteria).\n", prompt);
        Assert.Contains("- slot-2: \"Frontend\", type Implement.", prompt);
        Assert.Contains("- type-1: Implement. Carries out its instructions", prompt);
        Assert.Contains("- type-3: Architect.", prompt);
        Assert.Contains("--sandbox read-only", string.Join(" ", JsonSerializer.Deserialize<string[]>(File.ReadAllText(Evidence("arguments.json")))!));
        Assert.Equal(AttemptStatus.Succeeded, record.Status);

        var proposal = Ready(record);
        var wire = Assert.Single(proposal.Nodes).Id;
        var accepted = workflow.Must(proposal.Accept(workflow, proposal.Items.ToHashSet(), _ => false));

        Assert.Equal(
            [
                ("Design export", "Export the report as CSV.", ""),
                ("Backend API", "Add the /export endpoint.", ""),
                ("Frontend", "Add the export button.", "The button downloads a file."),
                ("Wire export", "Call the endpoint from the button.", ""),
            ],
            new[] { Architect, Backend, Frontend, wire }.Select(id => accepted.Tasks[id]).Select(Summary));
        Assert.Equal(BuiltInBlueprints.Implement, accepted.Tasks[wire].Blueprint);
        Assert.Equal(new CanvasPoint(640, 0), accepted.Positions[wire]);
        Assert.Equal(
            [(Architect, Backend), (Architect, Frontend), (Backend, wire), (Frontend, wire)],
            accepted.Connections.Keys.Select(key => (key.From, key.To)).Order());

        await using var reopened = ProjectRuns.Open(_project, clients);
        Assert.Equal(new PlanningHandles(record.Planning!.Plan, [Backend, Frontend], [.. BuiltInBlueprints.All.Select(blueprint => blueprint.Key)]), reopened.Latest[Architect].Planning);
        Assert.Equal(wire, Assert.Single(Ready(reopened.Latest[Architect]).Nodes).Id);
        Assert.Same(accepted, accepted.Must(proposal.Accept(accepted, proposal.Items.ToHashSet(), _ => false)));
    }

    [Fact]
    public void Accepting_is_one_edit_and_one_change_of_the_document()
    {
        var document = Seeded(ArchitectWithTwoSlots(ConversationMode.Autonomous));
        var before = document.Current;
        var proposal = Ready(Proposed(FillsBothAndAddsOne));
        var changes = 0;
        document.Changed += (_, _) => changes++;

        Assert.IsType<EditResult.Applied>(document.Apply(proposal.Accept(document.Current, proposal.Items.ToHashSet(), _ => false)));

        Assert.Equal(1, changes);
        Assert.Equal(4, document.Current.Tasks.Count);
        Assert.Equal(3, before.Tasks.Count);
        Assert.Equal("", before.Tasks[Backend].Field("instructions"));
    }

    [Fact]
    public void Accepting_a_subset_leaves_out_the_unchosen_tasks_and_their_connections()
    {
        var workflow = ArchitectWithTwoSlots(ConversationMode.Autonomous);
        var proposal = Ready(Proposed(FillsBothAndAddsOne));

        var accepted = workflow.Must(proposal.Accept(workflow, new HashSet<TaskId> { Backend, proposal.Nodes[0].Id }, _ => false));

        Assert.Equal("Add the /export endpoint.", accepted.Tasks[Backend].Field("instructions"));
        Assert.Equal("", accepted.Tasks[Frontend].Field("instructions"));
        Assert.Equal(
            [(Architect, Backend), (Architect, Frontend), (Backend, proposal.Nodes[0].Id)],
            accepted.Connections.Keys.Select(key => (key.From, key.To)).Order());
    }

    [Fact]
    public void A_proposal_that_would_close_a_cycle_names_it_and_applies_nothing()
    {
        var document = Seeded(ArchitectWithTwoSlots(ConversationMode.Autonomous));
        var before = document.Current;
        var proposal = Ready(Proposed("""
            ```idevelop
            {"status": "proposal",
             "fill": [{"slot": "slot-1", "fields": {"instructions": "Build it."}}],
             "add": [{"id": "check", "type": "type-1", "title": "Check"}],
             "connect": [{"from": "slot-1", "to": "check"}, {"from": "check", "to": "planner"}]}
            ```
            """));
        var check = proposal.Nodes[0].Id;

        var result = document.Apply(proposal.Accept(document.Current, proposal.Items.ToHashSet(), _ => false));

        var cycle = Assert.IsType<EditRejection.OrderingCycle>(Assert.IsType<EditResult.Rejected>(result).Reason);
        Assert.Equal([check, Architect, Backend, check], cycle.Path.ToArray());
        Assert.Equal("Check", proposal.TitleOf(check));
        Assert.Same(before, document.Current);
        Assert.False(document.HasUnsavedChanges);
    }

    [Fact]
    public async Task A_Chat_planner_proposes_after_its_first_turn_and_a_reply_proposes_an_accepted_node_again_under_its_id()
    {
        const string first = """
            A first cut.

            ```idevelop
            {"status": "proposal", "add": [{"id": "new-1", "type": "type-1", "title": "Write the parser"}]}
            ```
            """;
        const string second = """
            Split as you asked.

            ```idevelop
            {"status": "proposal", "add": [{"id": "new-2", "type": "type-1", "title": "Write the lexer"}, {"id": "new-1", "type": "type-1", "title": "Write the parser"}],
             "connect": [{"from": "new-2", "to": "new-1"}]}
            ```
            """;
        Install(_fakes, ClientId.Codex,
            Resuming(ClientId.Codex, Session).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, second)),
            Fresh(ClientId.Codex).CaptureStdin(Evidence("prompt.txt")).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, first)));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var workflow = ArchitectWithTwoSlots(ConversationMode.Chat);
        var planner = workflow.Tasks[Architect];

        var afterFirst = await Settles(runs, () => runs.Start(planner, PlanningContext.For(workflow, Architect, BuiltInBlueprints.All, _ => false)));
        var firstProposal = Ready(afterFirst);
        var acceptedFirst = workflow.Must(firstProposal.Accept(workflow, firstProposal.Items.ToHashSet(), _ => false));
        var afterSecond = await Settles(runs, () => runs.Send(planner, "Split it in two.", stopTurn: false));
        var secondProposal = Ready(afterSecond);
        var acceptedSecond = acceptedFirst.Must(secondProposal.Accept(acceptedFirst, secondProposal.Items.ToHashSet(), _ => false));

        Assert.Contains("End every message with a proposal block that holds your whole plan as it stands", File.ReadAllText(Evidence("prompt.txt")));
        Assert.Equal((AttemptStatus.WaitingForInput, new Pending.Reply()), (afterFirst.Status, afterFirst.Pending));
        Assert.Equal((1, "Write the parser"), (firstProposal.Turn, string.Join(", ", firstProposal.Nodes.Select(node => node.Title))));
        Assert.Equal((2, "Write the lexer, Write the parser"), (secondProposal.Turn, string.Join(", ", secondProposal.Nodes.Select(node => node.Title))));
        Assert.Equal(firstProposal.Nodes[0].Id, secondProposal.Nodes[1].Id);
        Assert.Equal(
            ["Backend", "Design export", "Frontend", "Write the lexer", "Write the parser"],
            acceptedSecond.Tasks.Values.Select(task => task.Title).Order());
        Assert.Contains(new ConnectionKey(secondProposal.Nodes[0].Id, firstProposal.Nodes[0].Id), acceptedSecond.Connections.Keys);
    }

    [Fact]
    public void A_May_ask_planner_learns_to_end_with_its_proposal_instead_of_without_a_block()
    {
        var workflow = ArchitectWithTwoSlots(ConversationMode.MayAsk);

        var prompt = AgentWork.Prompt(new NodeContext(workflow.Tasks[Architect], "") { Planning = PlanningContext.For(workflow, Architect, [BuiltInBlueprints.Implement], _ => false) });

        Assert.Contains("```idevelop\n{\"status\": \"asking\", \"question\": \"...\"}\n```\n\nWhen you have finished, end with the proposal block below instead.\n\nWhen your plan is ready,", prompt);
        Assert.EndsWith("Types you may add:\n- type-1: Implement. Carries out its instructions with the agent you choose, and may edit the project. Fields: instructions (Instructions, required), acceptanceCriteria (Acceptance criteria).\n", prompt);
    }

    [Fact]
    public void The_slots_are_the_empty_nodes_the_planners_dependencies_reach()
    {
        var filled = new TaskId(Guid.Parse("019a9d2e-5e00-7000-8000-000000000055"));
        var beside = new TaskId(Guid.Parse("019a9d2e-5f00-7000-8000-000000000066"));
        var workflow = ArchitectWithTwoSlots(ConversationMode.Autonomous)
            .Must(TestNodes.Place(TestNodes.Implement(filled, "Done already", "Keep it."), new CanvasPoint(0, 400)))
            .Must(TestNodes.Place(TestNodes.Implement(beside, "Beside"), new CanvasPoint(0, 600)))
            .Must(new Connect(new ConnectionKey(Backend, filled), ConnectionKind.Dependency))
            .Must(new Connect(new ConnectionKey(Architect, beside), ConnectionKind.Context));

        Assert.Equal([Backend, Frontend], PlanningContext.For(workflow, Architect, [], _ => false).Slots.Select(slot => slot.Id).ToArray());
    }

    [Fact]
    public void A_task_that_has_started_is_no_slot_and_a_proposal_never_fills_it()
    {
        var workflow = ArchitectWithTwoSlots(ConversationMode.Autonomous);
        var proposal = Ready(Proposed(FillsBothAndAddsOne));
        var wire = proposal.Nodes[0].Id;

        var accepted = workflow.Must(proposal.Accept(workflow, proposal.Items.ToHashSet(), id => id == Backend));

        Assert.Equal([Frontend], PlanningContext.For(workflow, Architect, [], id => id == Backend).Slots.Select(slot => slot.Id).ToArray());
        Assert.Equal(("Backend", ""), (accepted.Tasks[Backend].Title, accepted.Tasks[Backend].Field("instructions")));
        Assert.Equal("Add the export button.", accepted.Tasks[Frontend].Field("instructions"));
        Assert.Equal(
            [(Architect, Backend), (Architect, Frontend), (Frontend, wire)],
            accepted.Connections.Keys.Select(key => (key.From, key.To)).Order());
    }

    [Theory]
    [InlineData("{\"status\": \"proposal\"}", "The proposal fills and adds no task.")]
    [InlineData("{\"status\": \"proposal\", \"fill\": [{\"slot\": \"slot-3\"}]}", "The proposal fills slot-3, which is not one of the empty tasks it was given.")]
    [InlineData("{\"status\": \"proposal\", \"fill\": [{\"slot\": \"slot-1\", \"title\": \"A\"}, {\"slot\": \"slot-1\", \"title\": \"B\"}]}", "The proposal fills slot-1 twice.")]
    [InlineData("{\"status\": \"proposal\", \"add\": [{\"id\": \"a\", \"type\": \"type-9\"}]}", "The new task a has type type-9, which is not one of the types it was given.")]
    [InlineData("{\"status\": \"proposal\", \"add\": [{\"id\": \"a\", \"type\": \"type-1\"}, {\"id\": \"a\", \"type\": \"type-1\"}]}", "The proposal uses the id a twice.")]
    [InlineData("{\"status\": \"proposal\", \"add\": [{\"id\": \"a\", \"type\": \"type-1\"}], \"connect\": [{\"from\": \"a\", \"to\": \"b\"}]}", "A connection names b, which is not the planner, a slot, or a new task.")]
    [InlineData("{\"status\": \"proposal\", \"add\": [{\"id\": \"a\", \"type\": \"type-1\", \"fields\": {\"instructions\": 3}}]}", "The field instructions of a is not text.")]
    [InlineData("{\"status\": \"proposal\", \"add\": {\"id\": \"a\"}}", "The proposal's \"add\" is not a list.")]
    [InlineData("{\"status\": \"proposal\", \"fill\": [{\"slot\": \"slot-1\", \"title\": \" \"}]}", "The proposal fills slot-1 with nothing.")]
    [InlineData("{\"status\": \"proposal\", \"fill\": [{\"slot\": \"slot-1\", \"title\": \"A\"}, {\"slot\": \"slot-01\", \"title\": \"B\"}]}", "The proposal fills slot-1 twice.")]
    [InlineData("{\"status\": \"proposal\", \"add\": [{\"id\": \"a\", \"type\": \"type-1\", \"fields\": {\"acceptance_criteria\": \"x\"}}]}", "The new task a has a field acceptance_criteria, which its type Implement does not have.")]
    [InlineData("{\"status\": \"proposal\", \"add\": [{\"id\": \"a\", \"type\": \"type-1\"}], \"connect\": [{\"from\": \"a\", \"to\": \"a\"}]}", "A connection goes from a task to itself.")]
    [InlineData("{\"status\": \"proposal\", \"add\": [{\"id\": \"slot-01\", \"type\": \"type-1\"}]}", "The new task slot-01 has the id of the planner or a slot.")]
    [InlineData("{\"status\": \"proposal\", \"add\": [{\"id\": \"planner\", \"type\": \"type-1\"}]}", "The new task planner has the id of the planner or a slot.")]
    public void A_proposal_that_names_what_it_was_not_given_is_a_problem(string json, string problem)
    {
        Assert.Equal(new ProposalRead.Problem(problem), Proposal.Read(Proposed($"```idevelop\n{json}\n```"), Find));
    }

    [Fact]
    public void A_connection_the_proposal_repeats_or_the_workflow_holds_is_added_once()
    {
        var workflow = ArchitectWithTwoSlots(ConversationMode.Autonomous);
        var proposal = Ready(Proposed("""
            ```idevelop
            {"status": "proposal", "add": [{"id": "a", "type": "type-1"}],
             "connect": [{"from": "slot-01", "to": "a"}, {"from": "slot-1", "to": "a"}, {"from": "planner", "to": "slot-1"}]}
            ```
            """));

        var accepted = workflow.Must(proposal.Accept(workflow, proposal.Items.ToHashSet(), _ => false));

        Assert.Equal(("Implement", 2), (proposal.Nodes[0].Title, proposal.Connections.Length));
        Assert.Equal(3, accepted.Connections.Count);
    }

    [Fact]
    public void A_chain_of_new_tasks_goes_one_column_further_right_per_dependency()
    {
        var workflow = ArchitectAt(new CanvasPoint(100, 50));
        var proposal = Ready(Proposed("""
            ```idevelop
            {"status": "proposal",
             "add": [{"id": "c", "type": "type-1"}, {"id": "b", "type": "type-1"}, {"id": "a", "type": "type-1"}],
             "connect": [{"from": "planner", "to": "a"}, {"from": "a", "to": "b"}, {"from": "b", "to": "c"}]}
            ```
            """));

        var accepted = workflow.Must(proposal.Accept(workflow, proposal.Items.ToHashSet(), _ => false));

        Assert.Equal(
            [new CanvasPoint(100 + Proposal.ColumnStep, 50), new CanvasPoint(100 + 2 * Proposal.ColumnStep, 50), new CanvasPoint(100 + 3 * Proposal.ColumnStep, 50)],
            Places(accepted, proposal, "a", "b", "c"));
    }

    [Fact]
    public void Siblings_stack_a_row_apart_and_the_task_after_them_starts_its_column_at_the_planners_height()
    {
        var workflow = ArchitectAt(new CanvasPoint(100, 50));
        var proposal = Ready(Proposed("""
            ```idevelop
            {"status": "proposal",
             "add": [{"id": "a", "type": "type-1"}, {"id": "b", "type": "type-1"}, {"id": "join", "type": "type-1"}],
             "connect": [{"from": "planner", "to": "a"}, {"from": "planner", "to": "b"}, {"from": "a", "to": "join"}, {"from": "b", "to": "join"}]}
            ```
            """));

        var accepted = workflow.Must(proposal.Accept(workflow, proposal.Items.ToHashSet(), _ => false));

        Assert.Equal(
            [new CanvasPoint(100 + Proposal.ColumnStep, 50), new CanvasPoint(100 + Proposal.ColumnStep, 50 + Proposal.RowStep), new CanvasPoint(100 + 2 * Proposal.ColumnStep, 50)],
            Places(accepted, proposal, "a", "b", "join"));
    }

    [Fact]
    public void A_context_cycle_adds_no_depth()
    {
        var workflow = ArchitectAt(new CanvasPoint(100, 50));
        var proposal = Ready(Proposed("""
            ```idevelop
            {"status": "proposal",
             "add": [{"id": "a", "type": "type-1"}, {"id": "b", "type": "type-1"}],
             "connect": [{"from": "planner", "to": "a"}, {"from": "a", "to": "b", "kind": "context"}, {"from": "b", "to": "a", "kind": "context"}]}
            ```
            """));

        var accepted = workflow.Must(proposal.Accept(workflow, proposal.Items.ToHashSet(), _ => false));

        Assert.Equal(
            [new CanvasPoint(100 + Proposal.ColumnStep, 50), new CanvasPoint(100 + Proposal.ColumnStep, 50 + Proposal.RowStep)],
            Places(accepted, proposal, "a", "b"));
    }

    [Fact]
    public void A_dependency_cycle_among_new_tasks_is_cut_where_it_closes_and_accepting_it_names_the_cycle()
    {
        var workflow = ArchitectAt(new CanvasPoint(100, 50));
        var proposal = Ready(Proposed("""
            ```idevelop
            {"status": "proposal",
             "add": [{"id": "a", "type": "type-1"}, {"id": "b", "type": "type-1"}],
             "connect": [{"from": "a", "to": "b"}, {"from": "b", "to": "a"}]}
            ```
            """));
        var (a, b) = (proposal.Nodes[0].Id, proposal.Nodes[1].Id);

        var layout = proposal.Layout(workflow);

        Assert.Equal((new CanvasPoint(100 + 2 * Proposal.ColumnStep, 50), new CanvasPoint(100 + Proposal.ColumnStep, 50)), (layout[a], layout[b]));
        Assert.IsType<EditRejection.OrderingCycle>(Assert.IsType<EditResult.Rejected>(workflow.Apply(proposal.Accept(workflow, proposal.Items.ToHashSet(), _ => false))).Reason);
    }

    [Fact]
    public void A_task_an_earlier_Accept_placed_keeps_its_place_and_takes_no_new_one()
    {
        var workflow = ArchitectAt(new CanvasPoint(100, 50));
        var first = Ready(Proposed("""
            ```idevelop
            {"status": "proposal", "add": [{"id": "a", "type": "type-1"}, {"id": "b", "type": "type-1"}]}
            ```
            """));
        var acceptedFirst = workflow.Must(first.Accept(workflow, first.Items.ToHashSet(), _ => false));
        var second = Ready(Proposed("""
            ```idevelop
            {"status": "proposal", "add": [{"id": "a", "type": "type-1"}, {"id": "b", "type": "type-1"}, {"id": "c", "type": "type-1"}]}
            ```
            """));

        var acceptedSecond = acceptedFirst.Must(second.Accept(acceptedFirst, second.Items.ToHashSet(), _ => false));

        Assert.Equal(
            [
                new CanvasPoint(100 + Proposal.ColumnStep, 50),
                new CanvasPoint(100 + Proposal.ColumnStep, 50 + Proposal.RowStep),
                new CanvasPoint(100 + Proposal.ColumnStep, 50 + 2 * Proposal.RowStep),
            ],
            Places(acceptedSecond, second, "a", "b", "c"));
    }

    [Fact]
    public void A_node_that_proposes_nothing_or_a_reply_without_a_proposal_has_none()
    {
        var implement = AttemptReducer.Replay(
        [
            Requested(null), new AttemptEvent.Agent(AttemptEvents.T0, new AgentEvent.Succeeded(FillsBothAndAddsOne)), new AttemptEvent.Exited(AttemptEvents.T0, 0, ""),
        ])!;

        Assert.IsType<ProposalRead.None>(Proposal.Read(implement, Find));
        Assert.IsType<ProposalRead.None>(Proposal.Read(Proposed("Here is my plan in prose."), Find));
    }

    [Fact]
    public void A_workflow_with_Plan_and_Architect_nodes_saves_and_reopens()
    {
        var plan = new TaskId(Guid.Parse("019a9d2e-5e00-7000-8000-000000000055"));
        var document = Seeded(ArchitectWithTwoSlots(ConversationMode.Chat)
            .Must(new PlaceNode(plan, BuiltInBlueprints.Plan, new CanvasPoint(0, 400)) { Title = "Plan" }));

        var reopened = WorkflowDocument.Open(_project).Current;

        Assert.Equal(document.Current.Tasks.Values, reopened.Tasks.Values);
        Assert.Equal(["idevelop.architect@1", "idevelop.implement@1", "idevelop.plan@1"], reopened.Blueprints.Keys.Select(key => key.ToString()));
        Assert.Equal(ConversationMode.MayAsk, reopened.Tasks[plan].Conversation);
    }

    /// <summary>An Architect at the origin with a dependency on each of two empty Implement nodes the person drew.</summary>
    private static Workflow ArchitectWithTwoSlots(ConversationMode mode) => ArchitectAt(new CanvasPoint(0, 0), mode)
        .Must(TestNodes.Place(TestNodes.Implement(Backend, "Backend"), new CanvasPoint(320, 0)))
        .Must(TestNodes.Place(TestNodes.Implement(Frontend, "Frontend"), new CanvasPoint(320, 190)))
        .Must(new Connect(new ConnectionKey(Architect, Backend), ConnectionKind.Dependency))
        .Must(new Connect(new ConnectionKey(Architect, Frontend), ConnectionKind.Dependency));

    /// <summary>A workflow that holds only the Architect, at <paramref name="position"/>.</summary>
    private static Workflow ArchitectAt(CanvasPoint position, ConversationMode mode = ConversationMode.Autonomous) =>
        Workflow.Empty(new WorkflowId(Guid.Parse("019a9d2e-4c10-7a3b-8e21-5f0c9b7d1a01")))
            .Must(new PlaceNode(Architect, BuiltInBlueprints.Architect, position)
            {
                Title = "Design export",
                Fields = ImmutableDictionary<string, string>.Empty.Add("brief", "Export the report as CSV."),
                Settings = new NodeSettings(Sol, mode),
            });

    /// <summary>Where <paramref name="accepted"/> holds each of the proposal's new tasks, by the agent's own id for it.</summary>
    private static CanvasPoint[] Places(Workflow accepted, Proposal proposal, params string[] names) =>
        [.. names.Select(name => accepted.Positions[proposal.Nodes.Single(node => node.Name == name).Id])];

    private static (string Title, string Instructions, string Criteria) Summary(TaskDefinition task) =>
        task.Blueprint == BuiltInBlueprints.Architect
            ? (task.Title, task.Field("brief"), "")
            : (task.Title, task.Field("instructions"), task.Field("acceptanceCriteria"));

    private static Blueprint? Find(BlueprintKey key) => BuiltInBlueprints.Find(key);

    private static Proposal Ready(AttemptRecord record) => Assert.IsType<ProposalRead.Ready>(Proposal.Read(record, Find)).Proposal;

    /// <summary>An Architect attempt whose one turn ended with <paramref name="reply"/>.</summary>
    private static AttemptRecord Proposed(string reply) => AttemptReducer.Replay(
    [
        Requested(new PlanningHandles(Plan, [Backend, Frontend], [.. BuiltInBlueprints.All.Select(blueprint => blueprint.Key)])),
        new AttemptEvent.Agent(AttemptEvents.T0, new AgentEvent.SessionStarted(Session)),
        new AttemptEvent.Agent(AttemptEvents.T0, new AgentEvent.Succeeded(reply)),
        new AttemptEvent.Exited(AttemptEvents.T0, 0, ""),
    ])!;

    private static AttemptEvent.Requested Requested(PlanningHandles? planning) =>
        new(AttemptEvents.T0, new AttemptId(Guid.Parse("019a9d2e-6000-7000-8000-000000000077")), Architect, "Design export", Sol, "p", "codex", [])
        {
            Planning = planning,
        };

    private WorkflowDocument Seeded(Workflow workflow)
    {
        var document = WorkflowDocument.Open(_project);
        foreach (var task in workflow.Tasks.Values)
        {
            document.Apply(new PlaceNode(task.Id, task.Blueprint, workflow.Positions[task.Id])
            {
                Title = task.Title,
                Fields = task.Fields.ToImmutableDictionary(),
                Settings = new NodeSettings(task.Execution, task.Conversation),
            });
        }

        foreach (var (key, kind) in workflow.Connections)
        {
            document.Apply(new Connect(key, kind));
        }

        document.Save();
        return document;
    }

    /// <summary>Does <paramref name="act"/>, then waits until the planner's attempt neither runs nor is about to.</summary>
    private static async Task<AttemptRecord> Settles(ProjectRuns runs, Func<object> act)
    {
        var settled = new TaskCompletionSource<AttemptRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, EventArgs e)
        {
            if (runs.Latest.GetValueOrDefault(Architect) is { Status: not AttemptStatus.Running } record && runs.Active.IsEmpty)
            {
                settled.TrySetResult(record);
            }
        }

        runs.Changed += OnChanged;
        try
        {
            var result = act();
            Assert.False(result is StartResult.Refused or SendResult.Refused, $"refused: {result}");
            return await settled.Task.WaitAsync(Patience);
        }
        finally
        {
            runs.Changed -= OnChanged;
        }
    }

    private string Evidence(string name) => Path.Combine(_evidence, name);
}
