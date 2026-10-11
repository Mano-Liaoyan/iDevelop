using System.Collections.Immutable;
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

    /// <summary>Codex ready with one model, as a machine offers it when the planner's start is planned.</summary>
    internal static readonly ImmutableArray<AgentOffer> Offered = [new(ClientId.Codex, [new ModelOption("gpt-5.5", "GPT-5.5", ["low", "high"])])];

    [Fact]
    public void A_planners_start_records_the_agents_ready_then_and_its_prompt_lists_them()
    {
        var planner = new TaskDefinition(T, Approval.PlannerInclusionTests.PlannerBlueprint) { Title = "Plan", Execution = Task().Execution }
            .WithField("brief", "Plan it.")!;
        using var f = new RunFixtures(Connect(FixtureWorkflow(planner, (Task(U) with { Title = "U" }).WithField("brief", "")!), T, U));
        f.Approve();

        var planned = Assert.IsType<RunDecision.Recorded>(f.Store.Plan(f.Lease(T), f.Op(), f.Read().Revision.Id, new AttemptCause.Initial(), Offered));

        var plan = Assert.IsType<MaterializationPlan.Preparation>(Assert.IsType<RunEvent.Planned>(planned.Event).Plan);
        Assert.Equal<AgentOffer>(Offered, plan.Agents!.Value);
        Assert.IsType<RunDecision.Created>(f.Store.Reserve(f.Lease(T), f.Op(), f.Read().Plans.Single().Key));
        var context = RunPlanning.Context(f.Read(), f.Read().Attempts[plan.Attempt])!;
        Assert.Equal<AgentOffer>(Offered, context.Agents);
        Assert.EndsWith("Agents you may choose:\n- codex (Codex):\n  - gpt-5.5, GPT-5.5. Reasoning: low, high.", context.Contract(ConversationMode.Chat));
        Assert.Contains("\"agents\":[{\"client\":\"codex\",", File.ReadAllText(f.Journal(W, Run)));
    }

    [Fact]
    public void A_planners_retry_records_the_agents_ready_then_and_its_prompt_lists_them_while_the_first_keeps_its_own()
    {
        var planner = new TaskDefinition(T, Approval.PlannerInclusionTests.PlannerBlueprint) { Title = "Plan", Execution = Task().Execution }
            .WithField("brief", "Plan it.")!;
        using var f = new RunFixtures(FixtureWorkflow(planner));
        f.Approve();
        ImmutableArray<AgentOffer> later = [new(ClientId.ClaudeCode, [new ModelOption("claude-opus-5-5", "Claude Opus 5.5", ["high"])])];
        Assert.IsType<RunDecision.Recorded>(f.Store.Plan(f.Lease(T), f.Op(), f.Read().Revision.Id, new AttemptCause.Initial(), Offered));
        var first = Assert.IsType<RunEvent.Reserved>(Assert.IsType<RunDecision.Created>(f.Store.Reserve(f.Lease(T), f.Op(), f.Read().Plans.Single().Key)).Event);
        f.Claim(first);
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), first.Attempt.Id, TerminalAttemptOutcome.Failed,
            f.WriteLog(first, TerminalAttemptOutcome.Failed)));

        var retried = Assert.IsType<RunDecision.Recorded>(f.Store.Plan(f.Lease(T), f.Op(), f.Read().Revision.Id,
            new AttemptCause.Retry(first.Attempt.Id, f.Op()), later));

        var plan = (MaterializationPlan.Preparation)((RunEvent.Planned)retried.Event).Plan;
        Assert.Equal<AgentOffer>(later, plan.Agents!.Value);
        var retry = Assert.IsType<RunEvent.Reserved>(Assert.IsType<RunDecision.Created>(f.Store.Reserve(f.Lease(T), f.Op(),
            f.Read().Plans.Single(pair => RunReducer.Same(pair.Value, plan)).Key)).Event);
        var record = f.Read();
        Assert.EndsWith("Agents you may choose:\n- claude-code (Claude Code):\n  - claude-opus-5-5, Claude Opus 5.5. Reasoning: high.",
            RunPlanning.Context(record, record.Attempts[retry.Attempt.Id])!.Contract(ConversationMode.Chat));
        Assert.EndsWith("Agents you may choose:\n- codex (Codex):\n  - gpt-5.5, GPT-5.5. Reasoning: low, high.",
            RunPlanning.Context(record, record.Attempts[first.Attempt.Id])!.Contract(ConversationMode.Chat));
    }

    [Fact]
    public void A_start_that_renders_no_planners_prompt_records_no_agents()
    {
        var planner = new TaskDefinition(T, Approval.PlannerInclusionTests.PlannerBlueprint) { Title = "Plan", Execution = Task().Execution }
            .WithField("brief", "Plan it.")!;
        using var f = new RunFixtures(FixtureWorkflow(planner, Task(U).WithField("brief", "Build U.")!));
        f.Approve();

        var writer = Assert.IsType<RunDecision.Recorded>(f.Store.Plan(f.Lease(U), f.Op(), f.Read().Revision.Id, new AttemptCause.Initial(), Offered));
        var first = f.Reserve();
        f.Claim(first);
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), first.Attempt.Id, TerminalAttemptOutcome.Failed,
            f.WriteLog(first, TerminalAttemptOutcome.Failed)));
        var continued = Assert.IsType<RunDecision.Recorded>(f.Store.Plan(f.Lease(T), f.Op(), f.Read().Revision.Id,
            new AttemptCause.Continue(first.Attempt.Id, f.Op()), Offered));

        Assert.Null(((MaterializationPlan.Preparation)((RunEvent.Planned)writer.Event).Plan).Agents);
        Assert.Null(((MaterializationPlan.Preparation)((RunEvent.Planned)continued.Event).Plan).Agents);
        Assert.DoesNotContain("\"agents\"", File.ReadAllText(f.Journal(W, Run)));
    }
}
