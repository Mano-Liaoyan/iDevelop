using System.Collections.Immutable;
using IDevelop.Workflows;
using static IDevelop.TestSupport.TestNodes;
using static IDevelop.TestSupport.TestTasks;
using static IDevelop.Workflows.WorkflowEdit;

namespace IDevelop.Core.Tests;

public class WorkflowTests
{
    private static readonly TaskId Missing = new(Guid.Parse("019a9d2e-5d00-7000-8000-000000000044"));

    private static Workflow ThreeTasks() => Workflow.Empty(new WorkflowId(Guid.Parse("019a9d2e-4c10-7a3b-8e21-5f0c9b7d1a01")))
        .Must(Place(Implement(Design, "Design"), new CanvasPoint(120, 90)))
        .Must(Place(Implement(Build, "Build"), new CanvasPoint(420, 90)))
        .Must(Place(Implement(Review, "Review"), new CanvasPoint(720, 247.5)));

    private static Workflow DesignBuildReview() => ThreeTasks()
        .Must(new Connect(new ConnectionKey(Design, Build), ConnectionKind.Dependency))
        .Must(new Connect(new ConnectionKey(Build, Review), ConnectionKind.Dependency));

    /// <summary>A user blueprint with one field, as a library would hold it.</summary>
    private static Blueprint Spec(int version = 1, string label = "Goal") => new(
        new BlueprintKey("team.spec", version),
        "Spec",
        new WorkSpec.Agent(AgentAccess.ReadOnly, Proposes: false, PromptTemplate.Parse("Write a spec for {{goal}}.")),
        [new FieldSpec("goal", label, FieldShape.Line, Required: true, "the feature")],
        new NodeSettings(new ExecutionSettings(ClientId.Codex) { Model = "gpt-6-sol" }, ConversationMode.MayAsk));

    [Fact]
    public void Placing_a_node_seeds_its_blueprints_defaults_and_embeds_one_copy_of_the_blueprint()
    {
        var filled = new TaskId(Guid.Parse("019a9d2e-5e00-7000-8000-000000000055"));

        var workflow = ThreeTasks()
            .Must(new PlaceNode(Missing, Spec(), new CanvasPoint(0, 400)) { Title = "Spec" })
            .Must(new PlaceNode(filled, Spec(), new CanvasPoint(0, 600)) { Fields = ImmutableDictionary<string, string>.Empty.Add("goal", "export") });

        var placed = workflow.Tasks[Missing];
        Assert.Equal("Spec", placed.Title);
        Assert.Equal("the feature", placed.Field("goal"));
        Assert.Equal(new ExecutionSettings(ClientId.Codex) { Model = "gpt-6-sol" }, placed.Execution);
        Assert.Equal(ConversationMode.MayAsk, placed.Conversation);
        Assert.Equal("export", workflow.Tasks[filled].Field("goal"));
        Assert.Same(placed.Blueprint, workflow.Tasks[filled].Blueprint);
        Assert.Equal(["idevelop.implement@1", "team.spec@1"], workflow.Blueprints.Keys.Select(key => key.ToString()));
    }

    [Fact]
    public void A_batch_applies_every_edit_or_none()
    {
        var workflow = ThreeTasks();

        var applied = workflow.Must(new Batch([new EditTitle(Design, "Plan"), new Connect(new ConnectionKey(Design, Build), ConnectionKind.Dependency)]));
        var rejection = applied.Rejection(new Batch([new EditTitle(Build, "Changed"), new Connect(new ConnectionKey(Build, Design), ConnectionKind.Dependency)]));

        Assert.Equal(("Plan", 1), (applied.Tasks[Design].Title, applied.Connections.Count));
        Assert.Equal([Build, Design, Build], Assert.IsType<EditRejection.OrderingCycle>(rejection).Path.ToArray());
        Assert.Same(workflow, workflow.Must(new Batch([])));
    }

    [Fact]
    public void Deleting_the_last_node_of_a_blueprint_drops_its_copy()
    {
        var workflow = ThreeTasks().Must(new PlaceNode(Missing, Spec(), new CanvasPoint(0, 400)));

        var after = workflow.Must(new Delete([Missing], []));

        Assert.Equal([BuiltInBlueprints.Implement.Key], after.Blueprints.Keys);
    }

    [Fact]
    public void A_different_blueprint_under_a_key_the_workflow_holds_is_rejected()
    {
        var workflow = ThreeTasks().Must(new PlaceNode(Missing, Spec(), new CanvasPoint(0, 400)));

        var rejection = workflow.Rejection(new PlaceNode(new TaskId(Guid.NewGuid()), Spec(label: "Changed"), new CanvasPoint(0, 600)));

        Assert.Equal(new EditRejection.BlueprintConflict(new BlueprintKey("team.spec", 1)), rejection);
    }

    [Fact]
    public void Two_versions_of_one_blueprint_live_side_by_side()
    {
        var workflow = ThreeTasks()
            .Must(new PlaceNode(Missing, Spec(), new CanvasPoint(0, 400)))
            .Must(new PlaceNode(new TaskId(Guid.NewGuid()), Spec(version: 2, label: "Changed"), new CanvasPoint(0, 600)));

        Assert.Equal(["idevelop.implement@1", "team.spec@1", "team.spec@2"], workflow.Blueprints.Keys.Select(key => key.ToString()));
        Assert.Equal("Goal", workflow.Tasks[Missing].Blueprint.Fields[0].Label);
    }

    [Fact]
    public void A_field_the_blueprint_lacks_is_rejected_when_placing_and_when_editing()
    {
        Assert.Equal(
            new EditRejection.UnknownField(Missing, "goal"),
            ThreeTasks().Rejection(new PlaceNode(Missing, BuiltInBlueprints.Implement, new CanvasPoint(0, 0))
            {
                Fields = ImmutableDictionary<string, string>.Empty.Add("goal", "x"),
            }));
        Assert.Equal(new EditRejection.UnknownField(Design, "goal"), ThreeTasks().Rejection(new SetField(Design, "goal", "x")));
    }

    [Fact]
    public void Setting_a_field_and_the_conversation_mode_changes_only_that_node()
    {
        var workflow = ThreeTasks();

        var edited = workflow.Must(new SetField(Build, "instructions", "Write it")).Must(new SetConversation(Build, ConversationMode.Chat));

        Assert.Equal("Write it", edited.Tasks[Build].Field("instructions"));
        Assert.Equal(ConversationMode.Chat, edited.Tasks[Build].Conversation);
        Assert.Same(workflow.Tasks[Design], edited.Tasks[Design]);
        Assert.Same(edited, edited.Must(new SetConversation(Build, ConversationMode.Chat)));
    }

    [Fact]
    public void A_dependency_that_closes_a_cycle_is_rejected_with_its_path()
    {
        var rejection = DesignBuildReview().Rejection(new Connect(new ConnectionKey(Review, Design), ConnectionKind.Dependency));

        var cycle = Assert.IsType<EditRejection.OrderingCycle>(rejection);
        Assert.Equal(new[] { Review, Design, Build, Review }, cycle.Path);
    }

    [Fact]
    public void A_context_connection_may_close_the_same_loop()
    {
        var looped = DesignBuildReview().Must(new Connect(new ConnectionKey(Review, Design), ConnectionKind.Context));

        Assert.Equal(
            new[]
            {
                KeyValuePair.Create(new ConnectionKey(Design, Build), ConnectionKind.Dependency),
                KeyValuePair.Create(new ConnectionKey(Build, Review), ConnectionKind.Dependency),
                KeyValuePair.Create(new ConnectionKey(Review, Design), ConnectionKind.Context),
            },
            looped.Connections);
    }

    [Fact]
    public void Changing_a_looping_context_connection_to_a_dependency_is_rejected_with_its_path()
    {
        var looped = DesignBuildReview().Must(new Connect(new ConnectionKey(Review, Design), ConnectionKind.Context));

        var rejection = looped.Rejection(new SetConnectionKind(new ConnectionKey(Review, Design), ConnectionKind.Dependency));

        var cycle = Assert.IsType<EditRejection.OrderingCycle>(rejection);
        Assert.Equal(new[] { Review, Design, Build, Review }, cycle.Path);
    }

    [Fact]
    public void Deleting_a_task_removes_its_connections_and_position()
    {
        var workflow = DesignBuildReview().Must(new Connect(new ConnectionKey(Design, Review), ConnectionKind.Context));

        var after = workflow.Must(new Delete([Build], []));

        Assert.Equal(new[] { Design, Review }, after.Tasks.Keys);
        Assert.Equal(new[] { new ConnectionKey(Design, Review) }, after.Connections.Keys);
        Assert.Equal(new[] { Design, Review }, after.Positions.Keys);
    }

    [Fact]
    public void Deleting_a_connection_keeps_both_tasks()
    {
        var after = DesignBuildReview().Must(new Delete([], [new ConnectionKey(Design, Build)]));

        Assert.Equal(new[] { Design, Build, Review }, after.Tasks.Keys);
        Assert.Equal(new[] { new ConnectionKey(Build, Review) }, after.Connections.Keys);
    }

    [Fact]
    public void Moving_a_task_changes_only_its_position_and_keeps_the_semantic_instances()
    {
        var workflow = DesignBuildReview();

        var moved = workflow.Must(new MoveTasks([new TaskPosition(Build, new CanvasPoint(240, 120)), new TaskPosition(Missing, new CanvasPoint(1, 1))]));

        Assert.Equal(
            new[]
            {
                KeyValuePair.Create(Design, new CanvasPoint(120, 90)),
                KeyValuePair.Create(Build, new CanvasPoint(240, 120)),
                KeyValuePair.Create(Review, new CanvasPoint(720, 247.5)),
            },
            moved.Positions);
        Assert.Same(workflow.Tasks, moved.Tasks);
        Assert.Same(workflow.Connections, moved.Connections);
    }

    [Fact]
    public void Edits_with_no_effect_return_the_same_instance()
    {
        var workflow = DesignBuildReview();
        WorkflowEdit[] noEffect =
        [
            new EditTitle(Design, "Design"),
            new SetField(Design, "instructions", ""),
            new SetConversation(Design, ConversationMode.Autonomous),
            new MoveTasks([new TaskPosition(Design, new CanvasPoint(120, 90))]),
            new MoveTasks([new TaskPosition(Missing, new CanvasPoint(5, 5))]),
            new SetConnectionKind(new ConnectionKey(Design, Build), ConnectionKind.Dependency),
            new Delete([Missing], [new ConnectionKey(Build, Design)]),
        ];

        Assert.All(noEffect, edit => Assert.Same(workflow, workflow.Must(edit)));
    }

    [Fact]
    public void Deleting_twice_converges()
    {
        var once = DesignBuildReview().Must(new Delete([Build], [new ConnectionKey(Design, Build)]));

        var twice = once.Must(new Delete([Build], [new ConnectionKey(Design, Build)]));

        Assert.Same(once, twice);
        Assert.Equal(new[] { Design, Review }, twice.Tasks.Keys);
    }

    [Fact]
    public void Text_line_breaks_are_stored_as_line_feeds()
    {
        var workflow = Workflow.Empty(new WorkflowId(Guid.Parse("019a9d2e-4c10-7a3b-8e21-5f0c9b7d1a01")))
            .Must(Place(Implement(Design, "Design", "One\r\nTwo\rThree"), new CanvasPoint(0, 0)));

        var edited = workflow.Must(new SetField(Design, "acceptanceCriteria", "Done\r\nReviewed"));

        Assert.Equal("One\nTwo\nThree", edited.Tasks[Design].Field("instructions"));
        Assert.Equal("Done\nReviewed", edited.Tasks[Design].Field("acceptanceCriteria"));
        Assert.Same(edited, edited.Must(new SetField(Design, "acceptanceCriteria", "Done\nReviewed")));
    }

    [Fact]
    public void Setting_an_agent_changes_only_that_task_and_clearing_it_restores_the_task()
    {
        var workflow = DesignBuildReview();
        var codex = new ExecutionSettings(ClientId.Codex) { Model = " gpt-6-sol ", Reasoning = "" };

        var configured = workflow.Must(new SetExecution(Build, codex));

        Assert.Equal(new ExecutionSettings(ClientId.Codex) { Model = "gpt-6-sol" }, configured.Tasks[Build].Execution);
        Assert.Null(configured.Tasks[Build].Execution!.Reasoning);
        Assert.Same(workflow.Tasks[Design], configured.Tasks[Design]);
        Assert.Same(workflow.Connections, configured.Connections);
        Assert.Same(configured, configured.Must(new SetExecution(Build, new ExecutionSettings(ClientId.Codex) { Model = "gpt-6-sol" })));
        Assert.Equal(workflow.Tasks[Build], configured.Must(new SetExecution(Build, null)).Tasks[Build]);
    }

    [Fact]
    public void Invalid_edits_are_rejected_with_their_reason()
    {
        var workflow = ThreeTasks().Must(new Connect(new ConnectionKey(Design, Build), ConnectionKind.Context));

        Assert.Equal(
            new EditRejection.SelfConnection(Design),
            workflow.Rejection(new Connect(new ConnectionKey(Design, Design), ConnectionKind.Dependency)));
        Assert.Equal(
            new EditRejection.DuplicateConnection(new ConnectionKey(Design, Build), ConnectionKind.Context),
            workflow.Rejection(new Connect(new ConnectionKey(Design, Build), ConnectionKind.Dependency)));
        Assert.Equal(
            new EditRejection.UnknownTask(Missing),
            workflow.Rejection(new Connect(new ConnectionKey(Design, Missing), ConnectionKind.Dependency)));
        Assert.Equal(
            new EditRejection.UnknownTask(Missing),
            workflow.Rejection(new EditTitle(Missing, "Ghost")));
        Assert.Equal(
            new EditRejection.UnknownConnection(new ConnectionKey(Build, Design)),
            workflow.Rejection(new SetConnectionKind(new ConnectionKey(Build, Design), ConnectionKind.Context)));
        Assert.Equal(
            new EditRejection.TaskAlreadyExists(Design),
            workflow.Rejection(new PlaceNode(Design, BuiltInBlueprints.Implement, new CanvasPoint(0, 0))));
        Assert.Equal(
            new EditRejection.UnknownTask(Missing),
            workflow.Rejection(new SetExecution(Missing, new ExecutionSettings(ClientId.Pi))));
    }

    [Fact]
    public void A_review_takes_its_one_dependency_predecessor_that_edits_as_its_subject_and_rejects_a_second()
    {
        var reviewer = new TaskId(Guid.Parse("019a9d2e-5f00-7000-8000-000000000066"));
        var workflow = ThreeTasks()
            .Must(new PlaceNode(reviewer, BuiltInBlueprints.Review, new CanvasPoint(1000, 90)) { Title = "Check" })
            .Must(new PlaceNode(Missing, Spec(), new CanvasPoint(0, 400)) { Title = "Spec" });
        Assert.Null(workflow.SubjectOf(reviewer));

        workflow = workflow
            .Must(new Connect(new ConnectionKey(Missing, reviewer), ConnectionKind.Dependency))
            .Must(new Connect(new ConnectionKey(Build, reviewer), ConnectionKind.Dependency))
            .Must(new Connect(new ConnectionKey(Design, reviewer), ConnectionKind.Context));

        Assert.Equal(Build, workflow.SubjectOf(reviewer));
        Assert.Null(workflow.SubjectOf(Build));
        Assert.Equal(
            new EditRejection.SecondSubject(reviewer, Build),
            workflow.Rejection(new SetConnectionKind(new ConnectionKey(Design, reviewer), ConnectionKind.Dependency)));
        Assert.Equal(
            new EditRejection.SecondSubject(reviewer, Build),
            workflow.Rejection(new Connect(new ConnectionKey(Review, reviewer), ConnectionKind.Dependency)));
        Assert.Equal(Review, workflow.Must(new Delete([Build], [])).Must(new Connect(new ConnectionKey(Review, reviewer), ConnectionKind.Dependency)).SubjectOf(reviewer));
    }

    [Fact]
    public void An_approval_waits_for_the_person_and_passes_its_inputs_on_once_approved()
    {
        var node = new IDevelop.Nodes.NodeContext(new TaskDefinition(Design, BuiltInBlueprints.Approval), "The plan is ready.");

        Assert.Equal(new IDevelop.Nodes.NodeStep.WaitForPerson(new IDevelop.Nodes.Pending.Approval()), IDevelop.Nodes.NodeWorks.For(BuiltInBlueprints.Approval.Work).Next(node, null));
        Assert.IsNotAssignableFrom<IDevelop.Nodes.IConverses>(IDevelop.Nodes.NodeWorks.For(BuiltInBlueprints.Approval.Work));
        Assert.IsAssignableFrom<IDevelop.Nodes.IConverses>(IDevelop.Nodes.NodeWorks.For(BuiltInBlueprints.Review.Work));
    }
}
