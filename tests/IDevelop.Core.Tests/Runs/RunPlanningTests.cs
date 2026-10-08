using IDevelop.Execution;
using IDevelop.Nodes;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Runs;

/// <summary>What a run-owned planner may fill and place comes from its attempt alone (E3d.2).</summary>
public sealed class RunPlanningTests
{
    [Fact]
    public void A_planner_plans_under_its_attempt_and_a_continuation_keeps_those_handles()
    {
        var planner = new TaskDefinition(T, Approval.PlannerInclusionTests.PlannerBlueprint) { Title = "Plan", Execution = Task().Execution }
            .WithField("brief", "Plan it.")!;
        using var f = new RunFixtures(Connect(FixtureWorkflow(planner, (Task(U) with { Title = "U" }).WithField("brief", "")!), T, U));
        f.Approve();
        var first = f.Reserve();
        f.Claim(first);
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), A1, TerminalAttemptOutcome.Failed,
            f.WriteLog(first, TerminalAttemptOutcome.Failed)));
        var continuation = f.Reserve(cause: new AttemptCause.Continue(A1, f.Op()));
        var record = f.Read();

        var handles = RunPlanning.Handles(record, record.Attempts[A1])!;
        Assert.Equal<TaskId>([U], handles.Slots);
        Assert.Equal<BlueprintKey>([.. BuiltInBlueprints.All.Select(type => type.Key), Blueprint().Key, Approval.PlannerInclusionTests.PlannerBlueprint.Key], handles.Types);

        Assert.Equal(new PlanningHandles(A1.Value, [U], [.. BuiltInBlueprints.All.Select(type => type.Key), Blueprint().Key, Approval.PlannerInclusionTests.PlannerBlueprint.Key]), handles);
        Assert.Equal(handles, RunPlanning.Handles(record, record.Attempts[continuation.Attempt.Id]));
        Assert.Contains("- slot-1: \"U\", type Agent. Fields: brief (Brief, required).\n",
            RunPlanning.Context(record, record.Attempts[A1])!.Contract(ConversationMode.Autonomous));
    }
}
