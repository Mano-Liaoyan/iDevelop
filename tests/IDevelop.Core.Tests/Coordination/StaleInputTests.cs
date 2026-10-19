using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Coordination.CoordinatorFixture;

namespace IDevelop.Core.Tests.Coordination;

/// <summary>A consumer that finished against a replaced input: its result goes out of date, and it runs again by itself (#125).</summary>
public sealed class StaleInputTests
{
    /// <summary>
    /// A writes <c>a.txt="old\n"</c> and B starts from it. While B runs, the person retries A, whose retry runs beside B and
    /// publishes <c>a.txt="new\n"</c>. B finishes against the input it claimed, so B's result is stale, and B runs again by
    /// itself as a Retry, in its worktree with its earlier change and A's new work merged in.
    /// </summary>
    [Fact]
    public async Task A_stale_consumer_runs_again_by_itself_and_the_run_completes()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Agent(B)], (A, B)));
        var gate = Path.Combine(f.Evidence, "gate");
        f.Answer(A, Writes(A, "a.txt", "old\n"), Writes(A, "a.txt", "new\n")).Answer(B, FakeRule.On()
            .Print(FakeAgents.SessionLine(ClientId.Codex, "session-B")).Write("b.txt", "B\n").WaitForFile(gate)
            .Print(FakeAgents.ReplyLines(ClientId.Codex, "B ready.\n")), Writes(B, "again.txt", "B again\n"));
        await f.Open();
        await f.Resume();
        await f.Until(view => view.Tasks[B].State == TaskState.Running);
        var cause = new AttemptCause.Retry(Assert.IsType<ResultOrigin.Executed>(f.Result(A).Origin).Attempt, f.Preparation.Op());
        using (var lease = Assert.IsType<LeaseTake.Taken>(f.Coordinator.Permit!.TakeTask(A)).Lease)
            Assert.IsType<Preparation.Ready>(await f.Preparation.Materializer().Prepare(lease, RunOperations.First(f.Preparation.RunId, A, cause), cause));
        f.Coordinator.Refresh();
        File.WriteAllText(gate, "open");

        await f.UntilStatus(RunStatus.Completed);
        Assert.Equal([2, 2], new[] { f.Launches(A), f.Launches(B) });
        var record = f.Read();
        var stale = record.Results.First(result => result.Task == B);
        Assert.Contains(stale.Id, record.StaleResults);
        var first = Assert.IsType<ResultOrigin.Executed>(stale.Origin).Attempt;
        var rerun = Assert.IsType<ResultOrigin.Executed>(f.Result(B).Origin).Attempt;
        Assert.Equal(new AttemptCause.Retry(first, RunOperations.Rerun(f.Preparation.RunId, first)), record.Attempts[rerun].Cause);
        Assert.Equal(record.Preparations[new(first, 1)].Location.Owner, record.Preparations[new(rerun, 1)].Location.Owner);
        Assert.Equal(("new\n", "B\n", "B again\n"), (f.ResultFile(B, "a.txt"), f.ResultFile(B, "b.txt"), f.ResultFile(B, "again.txt")));
    }
}
