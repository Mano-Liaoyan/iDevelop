using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Materialization;

public sealed class OriginalRevisionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async System.Threading.Tasks.Task Reserved_preparation_resumes_its_original_revision_after_an_amendment(bool newOperation)
    {
        var workflow = FixtureWorkflow(Goal(T, "B", "Build B"), Goal(U, "X", "Build X"));
        using var f = new PreparationFixture(workflow);
        var original = f.Read().Revision.Id;
        var operation = f.Op();
        await Assert.ThrowsAsync<Crash>(async () => await f.Materializer(probe: step =>
        {
            if (step == "journal.prepared.before") throw new Crash();
        }).Prepare(f.Lease(T), operation, new AttemptCause.Initial()));
        var reserved = Assert.Single(f.Read().Attempts).Value;
        Assert.Equal(A1, reserved.Id);
        Assert.Empty(f.Read().Preparations);
        var amended = Revision.Capture(Edit(workflow, new WorkflowEdit.SetField(U, "goal", "Build X again")));
        Assert.IsType<RunDecision.Recorded>(f.Store.Amend(f.Permit, f.Op(), original, amended,
            new AmendmentOrigin.Person(), f.Op()));

        var resume = newOperation ? f.Op() : operation;
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T, resume));
        Assert.Equal(reserved.Id, ready.Execution.Launch.Attempt);
        Assert.Equal(original, f.Read().Attempts[ready.Execution.Launch.Attempt].Revision);
        Assert.Contains("Build B", ready.Execution.Prompt);
        Assert.Equal("Build X again", f.Read().Revision.Snapshot.Tasks[U].Field("goal"));
        Assert.Equal(amended.Id, f.Read().Revision.Id);
        var repeated = Assert.IsType<Preparation.Ready>(await f.Prepare(T, resume));
        Assert.Equal(ready.Execution, repeated.Execution);
        Assert.Single(f.Read().Attempts.Values, attempt => attempt.Task == T);
        Assert.Single(f.Read().Preparations.Values, preparation => preparation.Launch.Attempt == reserved.Id);
        var input = f.Read().Inputs[ready.Execution.Inputs];
        Assert.Equal(new InputId(Id(101)), input.Id);
        Assert.IsType<RunDecision.Granted>(f.Store.Claim(f.Lease(T), f.Op(), ready.Execution.Launch, input, ready.Execution.PromptHash));
        Assert.IsType<RunDecision.Existing>(f.Store.Claim(f.Lease(T), f.Op(), ready.Execution.Launch, input, ready.Execution.PromptHash));
        Assert.Single(f.Read().Claims);

        var fresh = Assert.IsType<Preparation.Ready>(await f.Prepare(U));
        Assert.Equal(amended.Id, f.Read().Attempts[fresh.Execution.Launch.Attempt].Revision);
        Assert.Contains("Build X again", fresh.Execution.Prompt);
    }

    [Fact]
    public async System.Threading.Tasks.Task A_recorded_plan_resumes_its_original_revision_after_an_unrelated_amendment()
    {
        var workflow = FixtureWorkflow(Goal(T, "B", "Build B"), Goal(U, "X", "Build X"));
        using var f = new PreparationFixture(workflow);
        var original = f.Read().Revision.Id;
        var operation = f.Op();
        await Assert.ThrowsAsync<Crash>(async () => await f.Materializer(probe: step =>
        {
            if (step == "journal.plan.after") throw new Crash();
        }).Prepare(f.Lease(T), operation, new AttemptCause.Initial()));
        Assert.Single(f.Read().Plans.Values.OfType<MaterializationPlan.Preparation>());
        Assert.Empty(f.Read().Attempts);
        var amended = Revision.Capture(Edit(workflow, new WorkflowEdit.SetField(U, "goal", "Build X again")));
        Assert.IsType<RunDecision.Recorded>(f.Store.Amend(f.Permit, f.Op(), original, amended,
            new AmendmentOrigin.Person(), f.Op()));
        Assert.Equal(RunProblem.RevisionConflict,
            Problem(f.Store.Plan(f.Lease(T), f.Op(), original, new AttemptCause.Initial())));

        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T, operation));
        Assert.Equal(A1, ready.Execution.Launch.Attempt);
        Assert.Equal(original, f.Read().Attempts[A1].Revision);
        Assert.Contains("Build B", ready.Execution.Prompt);
        Assert.Single(f.Read().Attempts.Values, attempt => attempt.Task == T);
        Assert.Equal(amended.Id, f.Read().Revision.Id);
        var input = f.Read().Inputs[ready.Execution.Inputs];
        Assert.IsType<RunDecision.Granted>(f.Store.Claim(f.Lease(T), f.Op(), ready.Execution.Launch, input, ready.Execution.PromptHash));

        var fresh = Assert.IsType<Preparation.Ready>(await f.Prepare(U, f.Op()));
        Assert.Equal(amended.Id, f.Read().Attempts[fresh.Execution.Launch.Attempt].Revision);
        Assert.Contains("Build X again", fresh.Execution.Prompt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async System.Threading.Tasks.Task An_incompatible_recorded_plan_is_refused_and_a_new_operation_uses_the_amended_revision(bool incoming)
    {
        var workflow = FixtureWorkflow(Goal(T, "B", "Build B"), Goal(U, "X", "Build X"));
        using var f = new PreparationFixture(workflow);
        var original = f.Read().Revision.Id;
        var operation = f.Op();
        await Assert.ThrowsAsync<Crash>(async () => await f.Materializer(probe: step =>
        {
            if (step == "journal.plan.after") throw new Crash();
        }).Prepare(f.Lease(T), operation, new AttemptCause.Initial()));
        var changed = incoming ? Connect(workflow, U, T, ConnectionKind.Context) :
            Edit(workflow, new WorkflowEdit.SetField(T, "goal", "Build B again"));
        var amended = Revision.Capture(changed);
        Assert.IsType<RunDecision.Recorded>(f.Store.Amend(f.Permit, f.Op(), original, amended,
            new AmendmentOrigin.Person(), f.Op()));

        var refused = Assert.IsType<Preparation.Rejected>(await f.Prepare(T, operation));
        Assert.Equal(RunProblem.RevisionConflict, refused.Reason.Problem);
        Assert.Empty(f.Read().Attempts);
        var fresh = Assert.IsType<Preparation.Ready>(await f.Prepare(T, f.Op()));
        Assert.Equal(amended.Id, f.Read().Attempts[fresh.Execution.Launch.Attempt].Revision);
        Assert.Contains(incoming ? "Build B" : "Build B again", fresh.Execution.Prompt);
        Assert.Single(f.Read().Attempts.Values, attempt => attempt.Task == T);
    }

    [Fact]
    public async System.Threading.Tasks.Task An_unreserved_plan_does_not_pin_a_fresh_preparation_to_the_old_revision()
    {
        var workflow = FixtureWorkflow(Goal(T, "B", "Build B"), Goal(U, "X", "Build X"));
        using var f = new PreparationFixture(workflow);
        var original = f.Read().Revision.Id;
        var operation = f.Op();
        await Assert.ThrowsAsync<Crash>(async () => await f.Materializer(probe: step =>
        {
            if (step == "journal.plan.after") throw new Crash();
        }).Prepare(f.Lease(T), operation, new AttemptCause.Initial()));
        Assert.Empty(f.Read().Attempts);
        var amended = Revision.Capture(Edit(workflow, new WorkflowEdit.SetField(U, "goal", "Build X again")));
        Assert.IsType<RunDecision.Recorded>(f.Store.Amend(f.Permit, f.Op(), original, amended,
            new AmendmentOrigin.Person(), f.Op()));

        var resumed = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        Assert.Equal(amended.Id, f.Read().Attempts[resumed.Execution.Launch.Attempt].Revision);
        Assert.Contains("Build B", resumed.Execution.Prompt);
        var fresh = Assert.IsType<Preparation.Ready>(await f.Prepare(U));
        Assert.Equal(amended.Id, f.Read().Attempts[fresh.Execution.Launch.Attempt].Revision);
        Assert.Contains("Build X again", fresh.Execution.Prompt);
        Assert.Equal(2, f.Read().Attempts.Count);
    }

    private static TaskDefinition Goal(TaskId task, string title, string goal) =>
        (new TaskDefinition(task, BuiltInBlueprints.Plan) { Title = title, Execution = Task().Execution }).WithField("goal", goal)!;

    private sealed class Crash : Exception;
}
