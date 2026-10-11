using System.Collections.Immutable;
using IDevelop.Core.Tests.Runs;
using IDevelop.Execution;
using IDevelop.Nodes;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Materialization;

/// <summary>
/// A run's planner lists the agents that were ready when its start was planned, which the start records, so every
/// preparation, claim, and recovery renders the same prompt whatever the clients offer by then (#99).
/// </summary>
public sealed class PlannerAgentsTests
{
    /// <summary>What the machine offers after the crash: another client than at the start.</summary>
    private static readonly ImmutableArray<AgentOffer> Later = [new(ClientId.ClaudeCode, [new ModelOption("claude-opus-5-5", "Claude Opus 5.5", ["high"])])];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async System.Threading.Tasks.Task A_recovered_start_lists_the_agents_its_plan_recorded_whatever_the_clients_offer_now(bool newOperation)
    {
        var planner = new TaskDefinition(T, BuiltInBlueprints.Plan) { Title = "Plan", Execution = Task().Execution }.WithField("goal", "Plan the export.")!;
        using var f = new PreparationFixture(FixtureWorkflow(planner));
        var operation = f.Op();
        await Assert.ThrowsAsync<Crash>(async () => await f.Materializer(probe: step =>
        {
            if (step == "journal.prepared.before") throw new Crash();
        }).Prepare(f.Lease(T), operation, new AttemptCause.Initial(), agents: RunPlanningTests.Offered));

        var resumed = newOperation ? f.Op() : operation;
        var ready = Assert.IsType<Preparation.Ready>(await f.Materializer().Prepare(f.Lease(T), resumed, new AttemptCause.Initial(), agents: Later));

        Assert.EndsWith("\n\nAgents you may choose:\n- codex (Codex):\n  - gpt-5.5, GPT-5.5. Reasoning: low, high.\n", ready.Execution.Prompt);
        Assert.DoesNotContain("claude", ready.Execution.Prompt);
        Assert.Equal(ready.Execution, Assert.IsType<Preparation.Ready>(await f.Materializer().Prepare(f.Lease(T), resumed, new AttemptCause.Initial(), agents: Later)).Execution);
        Assert.IsType<ClaimCheck.Granted>(f.Materializer().CheckInputsAndClaim(f.Lease(T), f.Op(), ready.Execution.Launch));
        Assert.IsType<ClaimCheck.Existing>(f.Materializer().ClaimedLaunch(f.Permit, resumed, T, new AttemptCause.Initial(), null));
    }

    [Fact]
    public async System.Threading.Tasks.Task A_planner_started_while_no_client_offers_a_model_reads_the_contract_without_agents()
    {
        var planner = new TaskDefinition(T, BuiltInBlueprints.Plan) { Title = "Plan", Execution = Task().Execution }.WithField("goal", "Plan the export.")!;
        using var f = new PreparationFixture(FixtureWorkflow(planner));

        var ready = Assert.IsType<Preparation.Ready>(await f.Materializer().Prepare(f.Lease(T), f.Op(), new AttemptCause.Initial(), agents: []));

        Assert.Contains("Do not choose agents or models. The person does.", ready.Execution.Prompt);
        Assert.Null(f.Read().Plans.Values.OfType<MaterializationPlan.Preparation>().Single().Agents);
    }

    private sealed class Crash : Exception;
}
