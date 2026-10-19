using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Coordination.CoordinatorFixture;

namespace IDevelop.Core.Tests.Coordination;

/// <summary>A person's Retry of a run's task that ended without a result (#123).</summary>
public sealed class RetryTests
{
    private static FakeRule Fails(TaskId task, string file, string text) => FakeRule.On()
        .Print(FakeAgents.SessionLine(ClientId.Codex, "session-" + Name(task))).Write(file, text).Exit(1);

    [Fact]
    public async Task A_failed_task_s_retry_starts_a_fresh_attempt_in_its_worktree_with_its_files_and_the_run_goes_on()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Agent(B)], (A, B)));
        f.Answer(A, Fails(A, "half.txt", "half\n"), Writes(A, "result.txt", "done\n")).Answer(B, Writes(B, "b.txt", "B\n"));
        await f.Open();
        await f.Resume();
        var failed = await f.UntilStatus(RunStatus.NeedsAttention);
        Assert.Equal(TaskState.Failed, failed.Tasks[A].State);
        var previous = failed.Tasks[A].Attempt!.Value;
        var confirmation = f.Preparation.Op();

        var reply = Assert.IsType<RetryReply.Reserved>(await f.Coordinator.Retry(f.Address, A, confirmation).WaitAsync(Bound));

        Assert.NotEqual(previous, reply.Attempt);
        Assert.Equal(new AttemptCause.Retry(previous, confirmation), f.Read().Attempts[reply.Attempt].Cause);
        await f.UntilStatus(RunStatus.Completed);
        Assert.Equal([2, 1], new[] { f.Launches(A), f.Launches(B) });
        Assert.Equal("done\n", f.ResultFile(A, "result.txt"));
        // The retry continues in the failed attempt's worktree with the files it left there.
        var record = f.Read();
        Assert.Equal(record.Preparations[new(previous, 1)].Location.Owner, record.Preparations[new(reply.Attempt, 1)].Location.Owner);
        Assert.Equal("half\n", f.ResultFile(A, "half.txt"));
        Assert.DoesNotContain(record.GitIntents.Values, intent => intent.Mutation is GitMutation.ResetCheckout);
    }

    [Fact]
    public async Task Repeating_a_confirmation_returns_its_attempt_and_starts_nothing_more()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A)]));
        var gate = Path.Combine(f.Evidence, "go");
        f.Answer(A, Fails(A, "half.txt", "half\n"), FakeRule.On().Print(FakeAgents.SessionLine(ClientId.Codex, "session-A"))
            .WaitForFile(gate).Write("result.txt", "done\n").Print(FakeAgents.ReplyLines(ClientId.Codex, "A ready.\n")));
        await f.Open();
        await f.Resume();
        await f.UntilStatus(RunStatus.NeedsAttention);
        var confirmation = f.Preparation.Op();
        var first = Assert.IsType<RetryReply.Reserved>(await f.Coordinator.Retry(f.Address, A, confirmation).WaitAsync(Bound));
        await f.Until(view => view.Tasks[A].State == TaskState.Running);

        Assert.Equal(first, await f.Coordinator.Retry(f.Address, A, confirmation).WaitAsync(Bound));
        // Another confirmation while the retry runs has nothing to retry.
        Assert.Equal(RunProblem.TaskBusy, Assert.IsType<RetryReply.Refused>(await f.Coordinator.Retry(f.Address, A, f.Preparation.Op())
            .WaitAsync(Bound)).Reason.Problem);

        File.WriteAllText(gate, "");
        await f.UntilStatus(RunStatus.Completed);
        Assert.Equal(2, f.Launches(A));
    }

    [Fact]
    public async Task Retry_refuses_a_task_that_has_a_result_or_has_not_ended_and_one_without_confirmation()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Agent(B)], (A, B)));
        f.Answer(A, Writes(A, "result.txt", "done\n")).Answer(B, Fails(B, "half.txt", "half\n"));
        await f.Open();
        await f.Resume();
        await f.UntilStatus(RunStatus.NeedsAttention);

        Assert.Equal(RunProblem.ConfirmationRequired, Assert.IsType<RetryReply.Refused>(
            await f.Coordinator.Retry(f.Address, B, new OperationId(Guid.Empty)).WaitAsync(Bound)).Reason.Problem);
        Assert.Equal(RunProblem.InvalidClaim, Assert.IsType<RetryReply.Refused>(
            await f.Coordinator.Retry(f.Address, A, f.Preparation.Op()).WaitAsync(Bound)).Reason.Problem);
        Assert.Equal([1, 1], new[] { f.Launches(A), f.Launches(B) });
        Assert.Equal(TaskState.Failed, f.State(B));
    }

    [Fact]
    public async Task A_stopped_run_takes_no_retry()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A)]));
        f.Answer(A, Fails(A, "half.txt", "half\n"));
        await f.Open();
        await f.Resume();
        await f.UntilStatus(RunStatus.NeedsAttention);
        Assert.IsType<RunCommand.Accepted>(await f.Coordinator.Stop(f.Address, f.Preparation.Op()).WaitAsync(Bound));
        await f.UntilStatus(RunStatus.Stopped);

        Assert.Equal(RunProblem.RunStopped, Assert.IsType<RetryReply.Refused>(
            await f.Coordinator.Retry(f.Address, A, f.Preparation.Op()).WaitAsync(Bound)).Reason.Problem);
        Assert.Equal(1, f.Launches(A));
    }
}
