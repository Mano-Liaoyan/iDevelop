using IDevelop.Workflows;
using static IDevelop.Workflows.WorkflowEdit;

namespace IDevelop.Core.Tests;

public class WorkflowTests
{
    private static readonly TaskId Design = new(Guid.Parse("019a9d2e-5a02-7c41-9d3e-2b8f6a1c0e11"));
    private static readonly TaskId Build = new(Guid.Parse("019a9d2e-5b77-7e12-a4f0-7c3d9e2b5f22"));
    private static readonly TaskId Review = new(Guid.Parse("019a9d2e-5c9a-7f05-b1c8-4e6a0d3f8c33"));
    private static readonly TaskId Missing = new(Guid.Parse("019a9d2e-5d00-7000-8000-000000000044"));

    private static Workflow ThreeTasks() => Workflow.Empty(new WorkflowId(Guid.Parse("019a9d2e-4c10-7a3b-8e21-5f0c9b7d1a01")))
        .Must(new CreateTask(new TaskDefinition(Design) { Title = "Design" }, new CanvasPoint(120, 90)))
        .Must(new CreateTask(new TaskDefinition(Build) { Title = "Build" }, new CanvasPoint(420, 90)))
        .Must(new CreateTask(new TaskDefinition(Review) { Title = "Review" }, new CanvasPoint(720, 247.5)));

    private static Workflow DesignBuildReview() => ThreeTasks()
        .Must(new Connect(new ConnectionKey(Design, Build), ConnectionKind.Dependency))
        .Must(new Connect(new ConnectionKey(Build, Review), ConnectionKind.Review));

    [Fact]
    public void A_dependency_that_closes_a_cycle_through_a_review_is_rejected_with_its_path()
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
                KeyValuePair.Create(new ConnectionKey(Build, Review), ConnectionKind.Review),
                KeyValuePair.Create(new ConnectionKey(Review, Design), ConnectionKind.Context),
            },
            looped.Connections);
    }

    [Theory]
    [InlineData(ConnectionKind.Dependency)]
    [InlineData(ConnectionKind.Review)]
    public void Changing_a_looping_context_connection_to_an_ordering_kind_is_rejected_with_its_path(ConnectionKind kind)
    {
        var looped = DesignBuildReview().Must(new Connect(new ConnectionKey(Review, Design), ConnectionKind.Context));

        var rejection = looped.Rejection(new SetConnectionKind(new ConnectionKey(Review, Design), kind));

        var cycle = Assert.IsType<EditRejection.OrderingCycle>(rejection);
        Assert.Equal(new[] { Review, Design, Build, Review }, cycle.Path);
    }

    [Fact]
    public void Changing_a_dependency_to_a_review_keeps_the_graph_acyclic_and_applies()
    {
        var changed = DesignBuildReview().Must(new SetConnectionKind(new ConnectionKey(Design, Build), ConnectionKind.Review));

        Assert.Equal(ConnectionKind.Review, changed.Connections[new ConnectionKey(Design, Build)]);
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
            new EditTask(Design, TaskField.Title, "Design"),
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
            .Must(new CreateTask(new TaskDefinition(Design) { Title = "Design", Instructions = "One\r\nTwo\rThree" }, new CanvasPoint(0, 0)));

        var edited = workflow.Must(new EditTask(Design, TaskField.AcceptanceCriteria, "Done\r\nReviewed"));

        Assert.Equal("One\nTwo\nThree", edited.Tasks[Design].Instructions);
        Assert.Equal("Done\nReviewed", edited.Tasks[Design].AcceptanceCriteria);
        Assert.Same(edited, edited.Must(new EditTask(Design, TaskField.AcceptanceCriteria, "Done\nReviewed")));
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
            workflow.Rejection(new EditTask(Missing, TaskField.Title, "Ghost")));
        Assert.Equal(
            new EditRejection.UnknownConnection(new ConnectionKey(Build, Design)),
            workflow.Rejection(new SetConnectionKind(new ConnectionKey(Build, Design), ConnectionKind.Review)));
        Assert.Equal(
            new EditRejection.TaskAlreadyExists(Design),
            workflow.Rejection(new CreateTask(new TaskDefinition(Design), new CanvasPoint(0, 0))));
        Assert.Equal(
            new EditRejection.UnknownTask(Missing),
            workflow.Rejection(new SetExecution(Missing, new ExecutionSettings(ClientId.Pi))));
    }
}
