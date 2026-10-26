using System.Collections.Immutable;
using IDevelop.Nodes;
using IDevelop.Projects;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Workflows.WorkflowEdit;

namespace IDevelop.Core.Tests;

/// <summary>Planners propose graph edits through scripted replies of the fake agent, and the person accepts them.</summary>
public sealed class PlanningTests : IDisposable
{
    private static readonly TaskId Architect = TestTasks.Design;
    private static readonly TaskId Backend = TestTasks.Build;
    private static readonly TaskId Frontend = TestTasks.Review;
    private static readonly ExecutionSettings Sol = new(ClientId.Codex) { Model = "gpt-6-sol", Reasoning = "high" };
    private static readonly Guid Plan = Guid.Parse("019a9d2e-6100-7000-8000-000000000088");
    private static readonly ExecutionSettings Haiku = new(ClientId.ClaudeCode) { Model = "claude-haiku-4-5" };

    /// <summary>A library blueprint with a default agent of its own.</summary>
    private static readonly Blueprint Careful = new(
        new BlueprintKey("team.careful", 1), "Careful implement", BuiltInBlueprints.Implement.Work, BuiltInBlueprints.Implement.Fields,
        new NodeSettings(Haiku, ConversationMode.MayAsk));

    /// <summary>The types a scripted proposal may add, from <c>type-1</c>: the built-ins, then <see cref="Careful"/> as <c>type-7</c>.</summary>
    private static readonly ImmutableArray<Blueprint> Types = [.. BuiltInBlueprints.All, Careful];

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
    private readonly string _project;

    public PlanningTests()
    {
        _project = _temp.Create("project");
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Accepting_is_one_edit_and_one_change_of_the_document()
    {
        var document = Seeded(ArchitectWithTwoSlots(ConversationMode.Autonomous));
        var before = document.Current;
        var proposal = Ready(FillsBothAndAddsOne);
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
        var proposal = Ready(FillsBothAndAddsOne);

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
        var proposal = Ready("""
            ```idevelop
            {"status": "proposal",
             "fill": [{"slot": "slot-1", "fields": {"instructions": "Build it."}}],
             "add": [{"id": "check", "type": "type-1", "title": "Check"}],
             "connect": [{"from": "slot-1", "to": "check"}, {"from": "check", "to": "planner"}]}
            ```
            """);
        var check = proposal.Nodes[0].Id;

        var result = document.Apply(proposal.Accept(document.Current, proposal.Items.ToHashSet(), _ => false));

        var cycle = Assert.IsType<EditRejection.OrderingCycle>(Assert.IsType<EditResult.Rejected>(result).Reason);
        Assert.Equal([check, Architect, Backend, check], cycle.Path.ToArray());
        Assert.Equal("Check", proposal.TitleOf(check));
        Assert.Same(before, document.Current);
        Assert.False(document.HasUnsavedChanges);
    }

    [Fact]
    public void A_May_ask_planner_learns_to_end_with_its_proposal_instead_of_without_a_block()
    {
        var workflow = ArchitectWithTwoSlots(ConversationMode.MayAsk);

        var prompt = AgentPrompt.For(workflow.Tasks[Architect], "", PlanningContext.For(workflow, Architect, [BuiltInBlueprints.Implement], _ => false));

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
        var proposal = Ready(FillsBothAndAddsOne);
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
        Assert.Equal(new ProposalRead.Problem(problem), Read($"```idevelop\n{json}\n```"));
    }

    [Fact]
    public void A_connection_the_proposal_repeats_or_the_workflow_holds_is_added_once()
    {
        var workflow = ArchitectWithTwoSlots(ConversationMode.Autonomous);
        var proposal = Ready("""
            ```idevelop
            {"status": "proposal", "add": [{"id": "a", "type": "type-1"}],
             "connect": [{"from": "slot-01", "to": "a"}, {"from": "slot-1", "to": "a"}, {"from": "planner", "to": "slot-1"}]}
            ```
            """);

        var accepted = workflow.Must(proposal.Accept(workflow, proposal.Items.ToHashSet(), _ => false));

        Assert.Equal(("Implement", 2), (proposal.Nodes[0].Title, proposal.Connections.Length));
        Assert.Equal(3, accepted.Connections.Count);
    }

    [Fact]
    public void A_chain_of_new_tasks_goes_one_column_further_right_per_dependency()
    {
        var workflow = ArchitectAt(new CanvasPoint(100, 50));
        var proposal = Ready("""
            ```idevelop
            {"status": "proposal",
             "add": [{"id": "c", "type": "type-1"}, {"id": "b", "type": "type-1"}, {"id": "a", "type": "type-1"}],
             "connect": [{"from": "planner", "to": "a"}, {"from": "a", "to": "b"}, {"from": "b", "to": "c"}]}
            ```
            """);

        var accepted = workflow.Must(proposal.Accept(workflow, proposal.Items.ToHashSet(), _ => false));

        Assert.Equal(
            [new CanvasPoint(100 + Proposal.ColumnStep, 50), new CanvasPoint(100 + 2 * Proposal.ColumnStep, 50), new CanvasPoint(100 + 3 * Proposal.ColumnStep, 50)],
            Places(accepted, proposal, "a", "b", "c"));
    }

    [Fact]
    public void Siblings_stack_a_row_apart_and_the_task_after_them_starts_its_column_at_the_planners_height()
    {
        var workflow = ArchitectAt(new CanvasPoint(100, 50));
        var proposal = Ready("""
            ```idevelop
            {"status": "proposal",
             "add": [{"id": "a", "type": "type-1"}, {"id": "b", "type": "type-1"}, {"id": "join", "type": "type-1"}],
             "connect": [{"from": "planner", "to": "a"}, {"from": "planner", "to": "b"}, {"from": "a", "to": "join"}, {"from": "b", "to": "join"}]}
            ```
            """);

        var accepted = workflow.Must(proposal.Accept(workflow, proposal.Items.ToHashSet(), _ => false));

        Assert.Equal(
            [new CanvasPoint(100 + Proposal.ColumnStep, 50), new CanvasPoint(100 + Proposal.ColumnStep, 50 + Proposal.RowStep), new CanvasPoint(100 + 2 * Proposal.ColumnStep, 50)],
            Places(accepted, proposal, "a", "b", "join"));
    }

    [Fact]
    public void A_context_cycle_adds_no_depth()
    {
        var workflow = ArchitectAt(new CanvasPoint(100, 50));
        var proposal = Ready("""
            ```idevelop
            {"status": "proposal",
             "add": [{"id": "a", "type": "type-1"}, {"id": "b", "type": "type-1"}],
             "connect": [{"from": "planner", "to": "a"}, {"from": "a", "to": "b", "kind": "context"}, {"from": "b", "to": "a", "kind": "context"}]}
            ```
            """);

        var accepted = workflow.Must(proposal.Accept(workflow, proposal.Items.ToHashSet(), _ => false));

        Assert.Equal(
            [new CanvasPoint(100 + Proposal.ColumnStep, 50), new CanvasPoint(100 + Proposal.ColumnStep, 50 + Proposal.RowStep)],
            Places(accepted, proposal, "a", "b"));
    }

    [Fact]
    public void A_dependency_cycle_among_new_tasks_is_cut_where_it_closes_and_accepting_it_names_the_cycle()
    {
        var workflow = ArchitectAt(new CanvasPoint(100, 50));
        var proposal = Ready("""
            ```idevelop
            {"status": "proposal",
             "add": [{"id": "a", "type": "type-1"}, {"id": "b", "type": "type-1"}],
             "connect": [{"from": "a", "to": "b"}, {"from": "b", "to": "a"}]}
            ```
            """);
        var (a, b) = (proposal.Nodes[0].Id, proposal.Nodes[1].Id);

        var layout = proposal.Layout(workflow);

        Assert.Equal((new CanvasPoint(100 + 2 * Proposal.ColumnStep, 50), new CanvasPoint(100 + Proposal.ColumnStep, 50)), (layout[a], layout[b]));
        Assert.IsType<EditRejection.OrderingCycle>(Assert.IsType<EditResult.Rejected>(workflow.Apply(proposal.Accept(workflow, proposal.Items.ToHashSet(), _ => false))).Reason);
    }

    [Fact]
    public void A_task_an_earlier_Accept_placed_keeps_its_place_and_takes_no_new_one()
    {
        var workflow = ArchitectAt(new CanvasPoint(100, 50));
        var first = Ready("""
            ```idevelop
            {"status": "proposal", "add": [{"id": "a", "type": "type-1"}, {"id": "b", "type": "type-1"}]}
            ```
            """);
        var acceptedFirst = workflow.Must(first.Accept(workflow, first.Items.ToHashSet(), _ => false));
        var second = Ready("""
            ```idevelop
            {"status": "proposal", "add": [{"id": "a", "type": "type-1"}, {"id": "b", "type": "type-1"}, {"id": "c", "type": "type-1"}]}
            ```
            """);

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
    public void With_a_fallback_a_new_agent_or_review_whose_type_has_no_default_agent_takes_it_and_so_does_a_fill_whose_task_has_none()
    {
        Assert.Equal(
            [
                ("build", Sol, ConversationMode.Autonomous),
                ("check", Sol, ConversationMode.Autonomous),
                ("sign", null, ConversationMode.Autonomous),
                ("careful", Haiku, ConversationMode.MayAsk),
                ("slot-1", Sol, ConversationMode.Autonomous),
            ],
            AcceptedAgents(Sol));
    }

    [Fact]
    public void Without_a_fallback_every_new_task_keeps_its_types_default_agent()
    {
        Assert.Equal(
            [
                ("build", null, ConversationMode.Autonomous),
                ("check", null, ConversationMode.Autonomous),
                ("sign", null, ConversationMode.Autonomous),
                ("careful", Haiku, ConversationMode.MayAsk),
                ("slot-1", null, ConversationMode.Autonomous),
            ],
            AcceptedAgents(null));
    }

    [Fact]
    public void A_reply_without_a_proposal_has_none()
    {
        Assert.IsType<ProposalRead.None>(Read("Here is my plan in prose."));
    }

    [Fact]
    public void A_workflow_with_Plan_and_Architect_nodes_saves_and_reopens()
    {
        var plan = new TaskId(Guid.Parse("019a9d2e-5e00-7000-8000-000000000055"));
        var document = Seeded(ArchitectWithTwoSlots(ConversationMode.Chat)
            .Must(new PlaceNode(plan, BuiltInBlueprints.Plan, new CanvasPoint(0, 400)) { Title = "Plan" }));

        var reopened = WorkflowDocument.OpenProject(_project).Single().Current;

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

    /// <summary>
    /// The agent and conversation mode of each task after accepting a proposal that fills slot-1 and adds an Implement, a
    /// Review, an Approval, and a <see cref="Careful"/> node, with <paramref name="fallback"/>.
    /// </summary>
    private static (string Task, ExecutionSettings? Execution, ConversationMode Conversation)[] AcceptedAgents(ExecutionSettings? fallback)
    {
        var workflow = ArchitectWithTwoSlots(ConversationMode.Autonomous);
        var proposal = Ready("""
            ```idevelop
            {"status": "proposal",
             "fill": [{"slot": "slot-1", "fields": {"instructions": "Build the endpoint."}}],
             "add": [{"id": "build", "type": "type-1"}, {"id": "check", "type": "type-4"}, {"id": "sign", "type": "type-5"}, {"id": "careful", "type": "type-7"}]}
            ```
            """);

        var accepted = workflow.Must(proposal.Accept(workflow, proposal.Items.ToHashSet(), _ => false, fallback));

        return [.. proposal.Nodes.Select(node => (node.Name, node.Id)).Append((Name: "slot-1", Id: Backend))
            .Select(task => (task.Name, accepted.Tasks[task.Id].Execution, accepted.Tasks[task.Id].Conversation))];
    }

    private static Blueprint? Find(BlueprintKey key) => Types.FirstOrDefault(blueprint => blueprint.Key == key);

    private static readonly PlanningHandles Handles = new(Plan, [Backend, Frontend], [.. Types.Select(blueprint => blueprint.Key)]);

    /// <summary>What an Architect's reply proposes, with the empty Backend and Frontend after it as its slots.</summary>
    private static ProposalRead Read(string reply) => Proposal.Read(Architect, 1, reply, Handles, Find);

    private static Proposal Ready(string reply) => Assert.IsType<ProposalRead.Ready>(Read(reply)).Proposal;

    private WorkflowDocument Seeded(Workflow workflow)
    {
        var document = WorkflowDocument.OpenProject(_project).Single();
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
}
