using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Approval.ApprovalTests;
using static IDevelop.Core.Tests.Approval.PlannerInclusionTests;
using static IDevelop.Core.Tests.Coordination.CoordinatorFixture;
using static IDevelop.Core.Tests.Runs.RunFixtures;
using Fixture = IDevelop.Core.Tests.Coordination.CoordinatorFixture;

namespace IDevelop.Core.Tests.Approval;

/// <summary>
/// A node's Run goes through the same preflight and approval as Run Workflow (#90). Its preview lists, checks, and offers
/// only that node and the tasks after it, and its approval records the node, so the run starts only it.
/// </summary>
public sealed class NodePreflightTests
{
    private static readonly TaskId AfterX = Fixture.C;

    private static OperationId Command(int n) => new(Guid.Parse($"00000000-0000-0000-0000-00000000a{n:D3}"));

    /// <summary><c>A → B</c>, a root <c>X</c> without an agent, and <c>C</c> (<see cref="AfterX"/>) after <c>X</c>.</summary>
    private static Workflow Scoped() => Graph([Agent(A), Agent(B), Agent(X) with { Execution = null }, Agent(AfterX)], (A, B), (X, AfterX));

    [Fact]
    public async Task A_node_s_preview_lists_and_checks_only_that_node_and_the_tasks_after_it()
    {
        await using var f = new ApprovalFixture(Scoped());
        await f.Open();

        var preview = f.Runs.Preflight(f.Workflow, A);

        Assert.Equal(A, preview.Node);
        Assert.Equal([A, B], preview.Tasks.Select(task => task.Task));
        Assert.Empty(preview.Gaps);
        Assert.Equal<BaseChoice>([BaseChoice.Head], preview.Choices);
        // Run Workflow checks the whole workflow, as before.
        var whole = f.Preflight();
        Assert.Null(whole.Node);
        Assert.Equal(4, whole.Tasks.Length);
        Assert.Equal<PreflightGap>([new PreflightGap.Task(X, new StartProblem.NoAgent())], whole.Gaps);
    }

    [Fact]
    public async Task A_node_after_another_task_cannot_start_a_run_without_it()
    {
        await using var f = new ApprovalFixture(Scoped());
        await f.Open();

        var preview = f.Runs.Preflight(f.Workflow, AfterX);

        Assert.Equal<PreflightGap>([new PreflightGap.After(AfterX, [X])], preview.Gaps);
        var refused = Assert.IsType<WorkflowStart.Refused>(await f.Runs.StartWorkflow(f.Workflow, new(preview, BaseChoice.Head, Command(1))).WaitAsync(Bound));
        Assert.Equal(ApprovalProblem.NotConfirmable, refused.Problem);
        Assert.Empty(f.ApprovedRuns());
    }

    [Fact]
    public async Task Confirming_a_node_s_preview_runs_that_node_and_the_tasks_after_it_and_nothing_else()
    {
        await using var f = ChainAnswers(new ApprovalFixture(Chain()));
        await f.Open();
        var preview = f.Runs.Preflight(f.Workflow, A);

        var started = await Start(f, preview, BaseChoice.Head, Command(2));
        await Completed(started.Coordinator);

        Assert.Equal([1, 1, 0], new[] { f.Launches(A), f.Launches(B), f.Launches(X) });
        var record = f.Read(Assert.Single(f.ApprovedRuns()));
        Assert.Equal([A], record.Requested!);
        Assert.Equal(RunPhase.Completed, record.Phase);
        Assert.Equal(A, Assert.Single(new ApprovalIntents(f.Project, W).All()).Node);
    }

    [Fact]
    public async Task A_node_s_approval_never_finishes_as_a_run_of_another_scope()
    {
        await using var f = new ApprovalFixture(Chain());
        await f.Open();
        var node = f.Runs.Preflight(f.Workflow, A);
        var other = f.Runs.Preflight(f.Workflow, X);
        var whole = f.Preflight();
        var intent = new ApprovalIntent(1, new(Guid.NewGuid()), Command(3), [Command(3)], node.Revision, BaseChoice.Head, node.Base!.Head, null,
            DateTimeOffset.UnixEpoch) { Node = A };

        Assert.True(intent.Matches(node, BaseChoice.Head, []));
        Assert.False(intent.Matches(other, BaseChoice.Head, []));
        Assert.False(intent.Matches(whole, BaseChoice.Head, []));
        Assert.False((intent with { Node = null }).Matches(node, BaseChoice.Head, []));
    }

    [Fact]
    public async Task Run_Workflow_while_a_node_s_run_is_active_finds_that_run_busy_rather_than_joining_it()
    {
        await using var f = new ApprovalFixture(Chain());
        var gate = Path.Combine(f.Evidence, "a-go");
        f.Answer(A, FakeRule.On().Print(FakeAgents.SessionLine(ClientId.Codex, "session-A")).WaitForFile(gate)
            .Print(FakeAgents.ReplyLines(ClientId.Codex, "A ready.\n"))).Answer(B, Reports(B)).Answer(X, Reports(X));
        await f.Open();
        try
        {
            var started = await Start(f, f.Runs.Preflight(f.Workflow, A), BaseChoice.Head, Command(4));
            await started.Coordinator.Until(view => view.Tasks[A].State == TaskState.Running).WaitAsync(Bound);

            var busy = Assert.IsType<WorkflowStart.Busy>(await f.Runs.StartWorkflow(f.Workflow, new(f.Preflight(), BaseChoice.Head, Command(5))).WaitAsync(Bound));

            Assert.Equal(started.Coordinator.Address.Run, busy.Active);
            Assert.Single(f.ApprovedRuns());
        }
        finally
        {
            File.WriteAllText(gate, "go");
        }
    }

    [Fact]
    public async Task A_node_s_preview_offers_only_that_node_s_own_earlier_report()
    {
        await using var f = Fixture();
        await f.Open();
        await PlanTwoTurns(f);
        var n1 = Added(f, "N1");

        var planner = f.Runs.Preflight(f.Workflow, X);
        var after = f.Runs.Preflight(f.Workflow, n1);

        Assert.Equal(X, Assert.Single(planner.Planners).Task);
        Assert.Empty(after.Planners);
        Assert.Empty(after.Reusable);
        Assert.Equal<PreflightGap>([new PreflightGap.After(n1, [X])], after.Gaps);
        // Including the planner's plan in its own run hands it on, so the tasks it planned start.
        var started = Assert.IsType<WorkflowStart.Started>(await f.Runs.StartWorkflow(f.Workflow,
            Including(planner, Confirm, planner.Planners[0])).WaitAsync(Bound));
        await Completed(started.Coordinator);
        Assert.Equal([1, 1], new[] { f.Launches("N1"), f.Launches("N2") });
    }
}
