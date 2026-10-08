using IDevelop.Core.Tests.Turns;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Coordination.CoordinatorFixture;

namespace IDevelop.Core.Tests.Coordination;

/// <summary>Who may command a run, and how the coordinator meets locks and blocks it does not hold (E3b).</summary>
public sealed class OwnershipTests
{
    [Fact]
    public async Task A_second_window_reads_the_run_but_cannot_command_it()
    {
        await using var f = new CoordinatorFixture(Chain());
        f.Answer(A, Writes(A, "a.txt", "A\n")).Answer(B, Writes(B, "b.txt", "B\n")).Answer(X, Reports(X));
        await f.Open();
        var (runs, second) = await f.SecondWindow();
        await using (runs)
        {
            Assert.False(second.Controlled);
            Assert.Equal("Controlled by another window", second.View.Label);
            var sequence = f.Read().Sequence;
            var stop = Assert.IsType<RunCommand.Unavailable>(await second.Stop(second.Address, f.Preparation.Op()).WaitAsync(Bound));
            Assert.Equal("This run is controlled by another iDevelop window.", stop.Message);
            Assert.IsType<RunCommand.Unavailable>(await second.Resume(second.Address).WaitAsync(Bound));
            Assert.Equal(sequence, f.Read().Sequence);
            Assert.Equal(0, f.TotalLaunches);
            Assert.Same(second, Assert.IsType<RunOpen.Opened>(runs.OpenRun(f.Address.Workflow, f.Address.Run)).Coordinator);

            await f.Resume();
            await f.UntilStatus(RunStatus.Completed);
            second.Refresh();
            var read = await second.Until(view => view.Phase == RunPhase.Completed);
            Assert.Equal(RunStatus.Completed, read.Status);
            Assert.All(read.Tasks.Values, task => Assert.Equal(TaskState.Done, task.State));
        }
        Assert.Equal(3, f.TotalLaunches);
    }

    [Fact]
    public async Task Commands_name_their_run()
    {
        await using var f = new CoordinatorFixture(Chain());
        f.Answer(A, Writes(A, "a.txt", "A\n"));
        await f.Open();
        var sequence = f.Read().Sequence;
        RunAddress[] others =
        [
            f.Address with { Run = new(Guid.NewGuid()) },
            f.Address with { Workflow = new(Guid.NewGuid()) },
            f.Address with { Project = Path.Combine(f.Evidence, "other") },
        ];
        foreach (var other in others)
        {
            Assert.Equal(RunProblem.IdentityMismatch, Assert.IsType<RunCommand.Refused>(await f.Coordinator.Stop(other, f.Preparation.Op()).WaitAsync(Bound)).Reason.Problem);
            Assert.Equal(RunProblem.IdentityMismatch, Assert.IsType<RunCommand.Refused>(await f.Coordinator.Resume(other).WaitAsync(Bound)).Reason.Problem);
        }
        Assert.Equal(sequence, f.Read().Sequence);
        Assert.Equal(RunStatus.Paused, f.View.Status);
    }

    [Fact]
    public async Task A_busy_task_lock_is_retried_without_a_durable_block()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A)]));
        f.Answer(A, Writes(A, "a.txt", "A\n"));
        await f.Open();
        var path = StandaloneLease.LockPath(f.Preparation.Git.Folder, A);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var held = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            await f.Resume();
            var busy = await f.Until(view => view.Tasks[A].State == TaskState.Refused);
            Assert.Equal(RunProblem.TaskBusy, busy.Tasks[A].Refusal!.Problem);
            Assert.Equal(RunStatus.Running, busy.Status);
            Assert.Empty(f.Read().Blocks);
            Assert.Empty(f.Read().Claims);
            Assert.Equal(0, f.TotalLaunches);
        }
        finally { held.Dispose(); }
        await f.UntilStatus(RunStatus.Completed);
        Assert.Equal(1, f.Launches(A));
        Assert.Empty(f.Read().Blocks);
    }

    [Fact]
    public async Task A_same_window_settlement_retry_routes_to_Settle()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A)]));
        f.Answer(A, Writes(A, "result.txt", "done\n"));
        await f.Open();
        FileStream? held = null;
        var journal = Path.Combine(f.Preparation.Git.Folder, ".idp", "runs", "write.lock");
        f.Runs.Probe = point => { if (point == "journal.capture-1.before" && held is null) held = new(journal, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); };
        try
        {
            await f.Resume();
            var stalled = await f.Until(view => view.Tasks[A].Unresolved == UnresolvedReason.IncompleteEvidence);
            Assert.Equal(TaskState.Settling, stalled.Tasks[A].State);
            Assert.Equal(RunProblem.JournalBusy, stalled.Tasks[A].Refusal!.Problem);
            Assert.Empty(f.Read().TurnClosures);
            Assert.Single(f.Read().RootExits);
        }
        finally { held?.Dispose(); }
        await f.UntilStatus(RunStatus.Completed);
        var disposition = Assert.Single(f.Read().Dispositions).Value.Disposition;
        Assert.IsType<CaptureDisposition.Matched>(disposition);
        Assert.Equal(2, Assert.Single(f.Read().Captures).Value.Count);
        Assert.Equal("done\n", f.ResultFile(A, "result.txt"));
        Assert.Equal(1, f.Launches(A));
    }

    [Fact]
    public async Task A_faulted_settlement_reruns_through_reconciliation()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A)]));
        f.Answer(A, Writes(A, "result.txt", "done\n"));
        await f.Open();
        var faulted = 0;
        f.Runs.Probe = point =>
        {
            if (point == "journal.capture-disposition.before" && Interlocked.Increment(ref faulted) == 1) throw new InvalidOperationException("Injected defect.");
        };
        await f.Resume();
        await f.UntilStatus(RunStatus.Completed);
        Assert.True(faulted >= 2);
        Assert.IsType<CaptureDisposition.Matched>(Assert.Single(f.Read().Dispositions).Value.Disposition);
        Assert.Equal("done\n", f.ResultFile(A, "result.txt"));
        Assert.Equal(1, f.Launches(A));
    }

    [Fact]
    public async Task A_refused_publication_retries_with_the_settled_turn_s_lease()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A)]));
        f.Answer(A, Writes(A, "result.txt", "done\n"));
        await f.Open();
        FileStream? held = null;
        var journal = Path.Combine(f.Preparation.Git.Folder, ".idp", "runs", "write.lock");
        f.Runs.Probe = point => { if (point == "coordinator.publish.before" && held is null) held = new(journal, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); };
        try
        {
            await f.Resume();
            var refused = await f.Until(view => view.Tasks[A].State == TaskState.Refused);
            Assert.Equal(RunProblem.JournalBusy, refused.Tasks[A].Refusal!.Problem);
            Assert.Equal(RunStatus.Running, refused.Status);
            Assert.Empty(f.Read().Results);
            Assert.IsType<LeaseTake.Busy>(f.Coordinator.Permit!.TakeTask(A));
        }
        finally { held?.Dispose(); }
        await f.UntilStatus(RunStatus.Completed);
        Assert.Equal("done\n", f.ResultFile(A, "result.txt"));
        Assert.Single(f.Read().Closures);
        Assert.Equal(1, f.Launches(A));
    }

    [Fact]
    public async Task Resume_tries_a_refused_start_again()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A)]));
        f.Answer(A, Writes(A, "a.txt", "A\n"));
        await f.Open(install: false);
        await f.Resume();
        var refused = await f.UntilStatus(RunStatus.NeedsAttention);
        Assert.Equal(TaskState.Refused, refused.Tasks[A].State);
        Assert.Equal(RunProblem.TaskUnconfigured, refused.Tasks[A].Refusal!.Problem);
        Assert.NotNull(refused.Tasks[A].Problem);
        f.Coordinator.Refresh();
        Assert.Equal(TaskState.Refused, (await f.UntilStatus(RunStatus.NeedsAttention)).Tasks[A].State);
        f.Install();
        await f.Clients.RefreshAsync();
        await f.Resume();
        await f.UntilStatus(RunStatus.Completed);
        Assert.Equal(1, f.Launches(A));
    }

    [Fact]
    public async Task A_blocked_start_reruns_its_own_operation_once_the_block_resolves()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Agent(B)], (A, B)));
        f.Answer(A, Writes(A, "result.txt", "done\n")).Answer(B, FakeRule.On()
            .Print(FakeAgents.SessionLine(ClientId.Codex, "session-B"))
            .Copy("result.txt", Path.Combine(f.Evidence, "consumer.txt"))
            .Print(FakeAgents.ReplyLines(ClientId.Codex, "B ready.\n")));
        await f.Open();
        var drifted = false;
        f.Runs.Probe = point =>
        {
            // B's claim rechecks its producer's checkout, which drifted after A's result was accepted.
            if (point != "runner.claim.before" || drifted || f.Read().Results.IsEmpty) return;
            drifted = true;
            File.WriteAllText(Path.Combine(f.Checkout(A), "result.txt"), "late\n");
        };
        await f.Resume();
        var stuck = await f.UntilStatus(RunStatus.NeedsAttention);
        var block = stuck.Tasks[A].Block!;
        Assert.Equal((TaskState.Blocked, "DirtyWorktree"), (stuck.Tasks[A].State, block.Problem.ToString()));
        Assert.Equal(new BlockScope.Checkout(["result.txt"]), block.Scope);
        Assert.Equal(TaskState.Blocked, stuck.Tasks[B].State);
        Assert.Equal(0, f.Launches(B));
        Assert.Equal(0, f.Claims(B));

        // A start under another operation for B's prepared initial attempt is refused, and the block stays.
        var permit = f.Coordinator.Permit!;
        var lease = Assert.IsType<LeaseTake.Taken>(permit.TakeTask(B)).Lease;
        using (lease)
            Assert.Equal(RunProblem.InvalidClaim, Assert.IsType<Preparation.Rejected>(await f.Preparation.Materializer()
                .Prepare(lease, f.Preparation.Op(), new AttemptCause.Initial())).Reason.Problem);
        Assert.Single(f.Read().Blocks.Values, state => !state.Resolved);

        var attempt = f.Read().CurrentResults[A].Origin is ResultOrigin.Executed executed ? executed.Attempt : throw new InvalidOperationException();
        var producer = Assert.IsType<LeaseTake.Taken>(permit.TakeTask(A)).Lease;
        using (producer)
        {
            var preservation = f.Preparation.Op();
            var materializer = f.Preparation.Materializer();
            Assert.IsType<Preservation.Preserved>(await materializer.Preserve(producer, preservation, attempt));
            var preview = Assert.IsType<RestorePreviewRead.Previewed>(materializer.PreviewRestore(producer, attempt, preservation)).Preview;
            Assert.IsType<Restoration.Restored>(materializer.Restore(producer, f.Preparation.Op(), attempt, preservation, f.Preparation.Op(), preview.Identity));
        }
        Assert.Equal(RunStatus.NeedsAttention, f.View.Status);
        f.Coordinator.Refresh();
        await f.UntilStatus(RunStatus.Completed);
        Assert.Equal(1, f.Launches(B));
        Assert.Equal(1, f.Claims(B));
        Assert.Equal("done\n", File.ReadAllText(Path.Combine(f.Evidence, "consumer.txt")));
    }
}
