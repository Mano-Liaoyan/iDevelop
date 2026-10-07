using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Materialization;

[Collection(ProcessCollection.Name)]
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
        Assert.IsType<RunDecision.Recorded>(f.Store.Amend(W, f.RunId, f.Op(), original, amended,
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
        Assert.IsType<RunDecision.Recorded>(f.Store.Amend(W, f.RunId, f.Op(), original, amended,
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
