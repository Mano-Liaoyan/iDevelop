using IDevelop.Execution;
using IDevelop.Projects;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Runs;

/// <summary>The workflow document follows a run's recorded amendments only from a revision the run expected (E3d.2).</summary>
public sealed class AmendmentProjectionTests
{
    private static readonly TaskId Added = new(Guid.Parse("00000000-0000-0000-0000-0000000000a7"));
    private static readonly TaskId Later = new(Guid.Parse("00000000-0000-0000-0000-0000000000a8"));

    /// <summary>A run of T and U, amended once to add a task after T and to fill U's brief.</summary>
    private static (RunFixtures Fixture, Workflow Approved, Workflow Amended) Amended()
    {
        var approved = FixtureWorkflow(Task(), Task(U) with { Title = "U" });
        var f = new RunFixtures(approved);
        f.Approve();
        var amended = Edit(Connect(Edit(approved, TestNodes.Place(Task(Added) with { Title = "Added" }, new(320, 0))), T, Added),
            new WorkflowEdit.SetField(U, "brief", "Check it."));
        Amend(f, approved, amended);
        return (f, approved, amended);
    }

    private static void Amend(RunFixtures f, Workflow previous, Workflow next) => Assert.IsType<RunDecision.Recorded>(
        f.Store.Amend(f.Permit, f.Op(), Revision.Capture(previous).Id, Revision.Capture(next), new AmendmentOrigin.Person(), f.Op()));

    private static RevisionId Id(Workflow workflow) => Revision.Capture(workflow).Id;

    [Fact]
    public void A_document_at_the_expected_revision_receives_the_amendment_even_after_its_cards_moved()
    {
        var (f, approved, amended) = Amended();
        using var _ = f;
        var moved = Edit(approved, new WorkflowEdit.MoveTasks([new(T, new CanvasPoint(40, 80))]));

        var apply = Assert.IsType<AmendmentProjection.Apply>(AmendmentProjection.Of(moved, f.Read()));

        Assert.Equal((Id(approved), Id(amended)), (apply.From, apply.Target));
        var projected = Edit(moved, apply.Edit);
        Assert.Equal(Id(amended), Id(projected));
        Assert.Equal(new CanvasPoint(40, 80), projected.Positions[T]);
        Assert.Equal(new CanvasPoint(320, 0), projected.Positions[Added]);
        Assert.IsType<AmendmentProjection.Current>(AmendmentProjection.Of(projected, f.Read()));
    }

    [Fact]
    public void A_document_two_amendments_behind_receives_both()
    {
        var (f, approved, amended) = Amended();
        using var _ = f;
        var again = Connect(Edit(amended, TestNodes.Place(Task(Later) with { Title = "Later" }, new(640, 0))), Added, Later);
        Amend(f, amended, again);

        var apply = Assert.IsType<AmendmentProjection.Apply>(AmendmentProjection.Of(approved, f.Read()));

        Assert.Equal(Id(again), Id(Edit(approved, apply.Edit)));
        Assert.Equal(Id(again), Id(Edit(amended, Assert.IsType<AmendmentProjection.Apply>(AmendmentProjection.Of(amended, f.Read())).Edit)));
    }

    [Fact]
    public void A_document_edited_since_shows_the_difference_and_receives_nothing()
    {
        var (f, approved, _) = Amended();
        using var __ = f;
        var edited = Edit(approved, new WorkflowEdit.EditTitle(T, "Plan again"));

        var differs = Assert.IsType<AmendmentProjection.Differs>(AmendmentProjection.Of(edited, f.Read()));

        Assert.Equal(new AmendmentProjection.Differs(Id(approved), [T, U, Added], [new(T, Added)]), differs);
    }

    [Fact]
    public void An_amendment_that_drops_a_connection_or_changes_its_kind_projects_too()
    {
        var approved = Connect(Connect(FixtureWorkflow(Task(), Task(U) with { Title = "U" }, Task(C) with { Title = "C" }), T, U), T, C);
        using var f = new RunFixtures(approved);
        f.Approve();
        var amended = Edit(Edit(approved, new WorkflowEdit.Delete([], [new(T, U)])), new WorkflowEdit.SetConnectionKind(new(T, C), ConnectionKind.Context));
        Amend(f, approved, amended);

        var apply = Assert.IsType<AmendmentProjection.Apply>(AmendmentProjection.Of(approved, f.Read()));

        Assert.Equal(Id(amended), Id(Edit(approved, apply.Edit)));
    }

    /// <summary>
    /// <paramref name="workflow"/> with every task on a copy of its blueprint under another name, which is no part of a
    /// revision, so the copy holds the same revision.
    /// </summary>
    private static Workflow Renamed(Workflow workflow)
    {
        var renamed = Workflow.Empty(workflow.Id);
        foreach (var task in workflow.Tasks.Values)
        {
            var blueprint = new Blueprint(task.Blueprint.Key, task.Blueprint.Name + " v2", task.Blueprint.Work, task.Blueprint.Fields, task.Blueprint.Defaults);
            var copy = new TaskDefinition(task.Id, blueprint) { Title = task.Title, Execution = task.Execution, Conversation = task.Conversation };
            renamed = Edit(renamed, TestNodes.Place(task.Fields.Aggregate(copy, (each, field) => each.WithField(field.Key, field.Value)!), workflow.Positions[task.Id]));
        }
        return workflow.Connections.Aggregate(renamed, (each, pair) => Connect(each, pair.Key.From, pair.Key.To, pair.Value));
    }

    [Fact]
    public void A_blueprint_the_document_cannot_take_shows_the_difference_from_what_the_run_executes()
    {
        var (f, approved, _) = Amended();
        using var __ = f;
        var document = Renamed(approved);
        Assert.Equal(Id(approved), Id(document));

        var differs = AmendmentProjection.Of(document, f.Read());

        Assert.Equal(new AmendmentProjection.Differs(Id(approved), [T, U, Added], [new(T, Added)]), differs);
    }

    [Fact]
    public void An_amendment_that_changes_a_blueprint_is_not_projected()
    {
        var (f, _, amended) = Amended();
        using var __ = f;
        Amend(f, amended, Edit(Renamed(amended), new WorkflowEdit.SetField(U, "brief", "Check it again.")));

        var differs = Assert.IsType<AmendmentProjection.Differs>(AmendmentProjection.Of(amended, f.Read()));

        Assert.Equal(Id(amended), differs.Expected);
        Assert.Equal<TaskId>([T, U, Added], differs.Tasks);
    }

    [Fact]
    public void A_run_without_amendments_leaves_an_edited_document_alone()
    {
        var approved = FixtureWorkflow(Task());
        using var f = new RunFixtures(approved);
        f.Approve();

        Assert.IsType<AmendmentProjection.Current>(AmendmentProjection.Of(Edit(approved, new WorkflowEdit.EditTitle(T, "Changed")), f.Read()));
    }

    [Fact]
    public void Undoing_the_projected_edit_leaves_the_recorded_amendment_and_projecting_again_converges()
    {
        var (f, approved, amended) = Amended();
        using var _ = f;
        Directory.CreateDirectory(DataFolder.Workflows(f.Project));
        File.WriteAllBytes(Path.Combine(DataFolder.Workflows(f.Project), $"{W}.json"), WorkflowFile.Serialize(approved));
        var document = Assert.Single(WorkflowDocument.OpenProject(f.Project));
        var apply = Assert.IsType<AmendmentProjection.Apply>(AmendmentProjection.Of(document.Current, f.Read()));

        Assert.IsType<EditResult.Applied>(document.Apply(apply.Edit));
        Assert.Equal(Id(amended), Id(document.Current));
        document.Undo();

        Assert.Equal(Id(approved), Id(document.Current));
        Assert.Equal(Id(amended), f.Read().Revision.Id);
        var again = Assert.IsType<AmendmentProjection.Apply>(AmendmentProjection.Of(document.Current, f.Read()));
        Assert.IsType<EditResult.Applied>(document.Apply(again.Edit));
        Assert.Equal(Id(amended), Id(document.Current));
        Assert.False(document.CanRedo);
    }
}
