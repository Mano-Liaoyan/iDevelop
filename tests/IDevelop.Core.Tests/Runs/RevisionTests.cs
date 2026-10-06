using IDevelop.Execution;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Runs;

public sealed class RevisionTests
{
    [Fact]
    public void Moving_cards_and_editing_blueprint_descriptions_preserve_V1()
    {
        var workflow = Edit(FixtureWorkflow(Task(description: "New description")), new WorkflowEdit.MoveTasks([new(T, new(500, 800))]));
        var captured = Revision.Capture(workflow);
        Assert.Equal(V1, captured.Id.Sha256);
        Assert.Equal("m1", captured.Snapshot.Tasks[T].Execution!.Model);
        Assert.Equal(Canonical, Revision.Canonical(workflow));
    }

    [Fact]
    public void Model_m2_captures_literal_V2()
    {
        var captured = Revision.Capture(FixtureWorkflow(Task(model: "m2")));
        Assert.Equal(V2, captured.Id.Sha256);
        Assert.Equal("m2", captured.Snapshot.Tasks[T].Execution!.Model);
    }

    [Fact]
    public void Decoding_V1_with_m2_rejects_the_revision_at_sequence_one()
    {
        var entry = new RunEntry(1, 1, new(Id(900)), Prompt, At,
            new RunEvent.Approved(Run, new(new(V1), FixtureWorkflow(Task(model: "m2"))), new(Base, BaseChoice.Head)));
        var decoded = RunJournal.Decode(RunJournal.Encode(entry));
        Assert.Equal(new RunRejection(RunProblem.RevisionMismatch, 1), decoded.Rejection);
    }

    [Fact]
    public void Repeated_revision_capture_keeps_the_first_approved_snapshot()
    {
        using var fixture = new RunFixtures();
        fixture.Approve();
        var candidate = Revision.Capture(FixtureWorkflow(Task(description: "Later")));
        var decision = fixture.Store.Amend(W, Run, fixture.Op(), new(V1), candidate, new AmendmentOrigin.Person(), fixture.Op());
        Assert.Equal("", Assert.IsType<RunDecision.Recorded>(decision).Record.Revision.Snapshot.Tasks[T].Blueprint.Description);
        Assert.Equal(V1, fixture.Read().Revision.Id.Sha256);
    }
}
