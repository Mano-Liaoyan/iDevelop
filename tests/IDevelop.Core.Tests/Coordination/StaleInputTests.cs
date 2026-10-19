using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Coordination.CoordinatorFixture;

namespace IDevelop.Core.Tests.Coordination;

/// <summary>A consumer that finished against a replaced input: its result is stale, and it needs attention.</summary>
public sealed class StaleInputTests
{
    private static Workflow Chain() => Graph([Agent(A), Agent(B)], (A, B));

    /// <summary>
    /// A writes <c>a.txt="old\n"</c> and B starts from it. While B runs, the person retries A, whose retry runs beside B and
    /// publishes <c>a.txt="new\n"</c>. B finishes against the input it claimed, so B's result is stale.
    /// </summary>
    private static async Task<ResultRecord> Stale(CoordinatorFixture f)
    {
        var gate = Path.Combine(f.Evidence, "gate");
        f.Answer(A, Writes(A, "a.txt", "old\n"), Writes(A, "a.txt", "new\n")).Answer(B, FakeRule.On()
            .Print(FakeAgents.SessionLine(ClientId.Codex, "session-B")).Write("b.txt", "B\n").WaitForFile(gate)
            .Print(FakeAgents.ReplyLines(ClientId.Codex, "B ready.\n")));
        await f.Open();
        await f.Resume();
        await f.Until(view => view.Tasks[B].State == TaskState.Running);
        var cause = new AttemptCause.Retry(Assert.IsType<ResultOrigin.Executed>(f.Result(A).Origin).Attempt, f.Preparation.Op());
        using (var lease = Assert.IsType<LeaseTake.Taken>(f.Coordinator.Permit!.TakeTask(A)).Lease)
            Assert.IsType<Preparation.Ready>(await f.Preparation.Materializer().Prepare(lease, RunOperations.First(f.Preparation.RunId, A, cause), cause));
        f.Coordinator.Refresh();
        File.WriteAllText(gate, "open");
        var stale = await f.Until(view => view.Tasks[B].State == TaskState.Stale && view.Tasks[A].State == TaskState.Done);
        Assert.Equal("Needs attention", stale.Label);
        Assert.Equal([2, 1], new[] { f.Launches(A), f.Launches(B) });
        var result = f.Result(B);
        Assert.Contains(result.Id, f.Read().StaleResults);
        return result;
    }

    [Fact]
    public async Task A_stale_consumer_needs_attention_and_the_run_does_not_complete()
    {
        await using var f = new CoordinatorFixture(Chain());
        var stale = await Stale(f);

        Assert.Equal("old\n", f.ResultFile(B, "a.txt"));
        Assert.Equal(RunProblem.IncompleteResults, Assert.IsType<RunDecision.Rejected>(
            f.Preparation.Store.Settle(f.Coordinator.Permit!, f.Preparation.Op(), RunOutcome.Completed)).Reason.Problem);
        var view = await f.Decided();
        Assert.Equal((RunStatus.NeedsAttention, TaskState.Stale), (view.Status, view.Tasks[B].State));
        Assert.Equal(RunPhase.Approved, f.Read().Phase);
        // Nothing replaces the stale result by itself: B runs once, and its result stays current.
        Assert.Equal(stale.Id, f.Result(B).Id);
        Assert.Equal([2, 1], new[] { f.Launches(A), f.Launches(B) });
        Assert.Single(f.Read().Attempts.Values, attempt => attempt.Task == B);
    }
}
