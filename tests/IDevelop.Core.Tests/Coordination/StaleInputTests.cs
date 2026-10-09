using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Coordination.CoordinatorFixture;

namespace IDevelop.Core.Tests.Coordination;

/// <summary>A consumer that finished against a replaced input, made current by an approved clean rebase (E3f).</summary>
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

    private static string CheckoutFile(CoordinatorFixture f, TaskId task, string file) => File.ReadAllText(Path.Combine(f.Checkout(task), file));

    [Fact]
    public async Task A_stale_consumer_becomes_current_by_an_approved_clean_rebase_without_another_client_run()
    {
        await using var f = new CoordinatorFixture(Chain());
        var stale = await Stale(f);
        Assert.Equal("old\n", f.ResultFile(B, "a.txt"));
        Assert.Equal(RunProblem.IncompleteResults, Assert.IsType<RunDecision.Rejected>(
            f.Preparation.Store.Settle(f.Coordinator.Permit!, f.Preparation.Op(), RunOutcome.Completed)).Reason.Problem);
        Assert.Equal(RunPhase.Approved, f.Read().Phase);

        var preview = Assert.IsType<RebasePreviewRead.Previewed>(await f.Coordinator.ReviewUpdatedInputs(f.Address, B).WaitAsync(Bound)).Preview;
        Assert.Equal(stale.Id, preview.Stale);
        Assert.Equal([A], preview.Updated.ToArray());
        Assert.Equal(["b.txt"], preview.Changes.ToArray());
        Assert.Equal("B ready.\n", preview.Report);
        Assert.Equal(["a.txt"], Assert.IsType<RebaseCandidate.Clean>(preview.Candidate).Updates.ToArray());
        var approval = f.Coordinator.ApproveRebase(f.Address, B, preview.Identity, f.Preparation.Op());
        // The approval holds the task while it runs, so a second command for it waits for none of its work.
        Assert.Equal(RunProblem.TaskBusy, Assert.IsType<Rebasing.Rejected>(
            await f.Coordinator.ApproveRebase(f.Address, B, preview.Identity, f.Preparation.Op()).WaitAsync(Bound)).Reason.Problem);
        Assert.IsType<Rebasing.Rebased>(await approval.WaitAsync(Bound));

        var done = await f.UntilStatus(RunStatus.Completed);
        Assert.Equal("Completed", done.Label);
        Assert.Equal("new\n", f.ResultFile(B, "a.txt"));
        Assert.Equal("B\n", f.ResultFile(B, "b.txt"));
        Assert.Equal(["new\n", "B\n"], new[] { CheckoutFile(f, B, "a.txt"), CheckoutFile(f, B, "b.txt") });
        Assert.Equal(new ResultOrigin.Rebased(stale.Id, Assert.IsType<ResultOrigin.Rebased>(f.Result(B).Origin).Plan), f.Result(B).Origin);
        Assert.Equal([2, 1], new[] { f.Launches(A), f.Launches(B) });
        Assert.Single(f.Read().Attempts.Values, attempt => attempt.Task == B);
    }

    [Fact]
    public async Task Only_the_controlling_window_reviews_or_approves_and_every_command_names_its_run()
    {
        await using var f = new CoordinatorFixture(Chain());
        var stale = await Stale(f);
        var preview = Assert.IsType<RebasePreviewRead.Previewed>(await f.Coordinator.ReviewUpdatedInputs(f.Address, B).WaitAsync(Bound)).Preview;
        var sequence = f.Read().Sequence;
        var (runs, second) = await f.SecondWindow();
        await using (runs)
        {
            Assert.Equal(WorkflowRunCoordinator.ElsewhereMessage,
                Assert.IsType<RebasePreviewRead.Unavailable>(await second.ReviewUpdatedInputs(second.Address, B).WaitAsync(Bound)).Message);
            Assert.Equal(WorkflowRunCoordinator.ElsewhereMessage, Assert.IsType<Rebasing.Unavailable>(
                await second.ApproveRebase(second.Address, B, preview.Identity, f.Preparation.Op()).WaitAsync(Bound)).Message);
        }
        var elsewhere = f.Address with { Run = new(Guid.Parse("00000000-0000-0000-0000-0000000000ee")) };
        Assert.Equal(RunProblem.IdentityMismatch, Assert.IsType<Rebasing.Rejected>(
            await f.Coordinator.ApproveRebase(elsewhere, B, preview.Identity, f.Preparation.Op()).WaitAsync(Bound)).Reason.Problem);
        Assert.Equal(RunProblem.IdentityMismatch, Assert.IsType<RebasePreviewRead.Rejected>(
            await f.Coordinator.ReviewUpdatedInputs(elsewhere, B).WaitAsync(Bound)).Reason.Problem);
        Assert.Equal(RunProblem.EvidenceMismatch, Assert.IsType<Rebasing.Rejected>(
            await f.Coordinator.ApproveRebase(f.Address, B, new(new string('0', 64)), f.Preparation.Op()).WaitAsync(Bound)).Reason.Problem);
        Assert.Equal(sequence, f.Read().Sequence);
        Assert.Equal(stale.Id, f.Result(B).Id);
        // A block comes back as the block, with its own problem and paths.
        File.WriteAllText(Path.Combine(f.Checkout(B), "extra.txt"), "extra\n");
        var blocked = Assert.IsType<Rebasing.Blocked>(await f.Coordinator.ApproveRebase(f.Address, B, preview.Identity, f.Preparation.Op()).WaitAsync(Bound)).Block;
        Assert.Equal(MaterializationProblem.DirtyWorktree, blocked.Problem);
        Assert.Equal(["extra.txt"], Assert.IsType<BlockScope.Checkout>(blocked.Scope).Paths.ToArray());
        Assert.Equal(stale.Id, f.Result(B).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_approved_rebase_a_crash_interrupted_finishes_once_on_resume_or_stop_and_not_before(bool stop)
    {
        await using var f = new CoordinatorFixture(Chain());
        var stale = await Stale(f);
        var preview = Assert.IsType<RebasePreviewRead.Previewed>(await f.Coordinator.ReviewUpdatedInputs(f.Address, B).WaitAsync(Bound)).Preview;
        var candidate = Assert.IsType<RebaseCandidate.Clean>(preview.Candidate).Commit;
        // The window dies after the branch moved: from then on it does no rebase work at all.
        var crashed = false;
        f.Runs.Probe = point =>
        {
            if (point == "journal.rebase-branch-observed.after") crashed = true;
            if (crashed && point is "journal.rebase-branch-observed.after" or "coordinator.rebase.before") throw new InvalidOperationException("Crashed.");
        };
        Assert.IsType<Rebasing.Rejected>(await f.Coordinator.ApproveRebase(f.Address, B, preview.Identity, f.Preparation.Op()).WaitAsync(Bound));
        await f.Reopen();
        var paused = await f.Decided();
        Assert.Equal(RunStatus.Paused, paused.Status);
        var record = f.Read();
        Assert.Single(record.Plans.Values.OfType<MaterializationPlan.Rebase>());
        Assert.Equal(stale.Id, record.CurrentResults[B].Id);
        Assert.Equal(candidate.Hex, f.Preparation.Git.Git("rev-parse", RunLayout.TaskBranch(record.RunKey!, record.TaskKeys[B])).Trim());
        Assert.Equal("old\n", CheckoutFile(f, B, "a.txt"));

        if (stop) Assert.IsType<RunCommand.Accepted>(await f.Coordinator.Stop(f.Address, f.Preparation.Op()).WaitAsync(Bound));
        else await f.Resume();
        await f.UntilStatus(stop ? RunStatus.Stopped : RunStatus.Completed);
        Assert.Single(f.Read().Results, result => result.Origin is ResultOrigin.Rebased);
        Assert.Equal(candidate, Assert.IsType<CodeOutput.Produced>(f.Result(B).Code).Code.Commit);
        Assert.Equal(["new\n", "B\n"], new[] { CheckoutFile(f, B, "a.txt"), CheckoutFile(f, B, "b.txt") });
        Assert.Equal([2, 1], new[] { f.Launches(A), f.Launches(B) });
    }
}
