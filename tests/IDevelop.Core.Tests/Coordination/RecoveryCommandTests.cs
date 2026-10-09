using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Coordination.CoordinatorFixture;

namespace IDevelop.Core.Tests.Coordination;

/// <summary>
/// A person's recovery through the coordinator (E3g.2): the evidence of a block, Preserve and restore of a drifted
/// checkout, and the confirmed closure of a turn whose execution stayed unresolved.
/// </summary>
public sealed class RecoveryCommandTests
{
    private static Workflow Pair() => Graph([Agent(A), Agent(B)], (A, B));

    /// <summary>A publishes <c>result.txt="done\n"</c>, then its checkout drifts to <c>"late\n"</c> before B's first claim.</summary>
    private static async Task<RunView> Drifted(CoordinatorFixture f)
    {
        f.Answer(A, Writes(A, "result.txt", "done\n")).Answer(B, Writes(B, "b.txt", "B\n"));
        await f.Open();
        var drifted = false;
        f.Runs.Probe = point =>
        {
            if (point != "runner.claim.before" || drifted || f.Read().Results.IsEmpty) return;
            drifted = true;
            File.WriteAllText(Path.Combine(f.Checkout(A), "result.txt"), "late\n");
        };
        await f.Resume();
        var stuck = await f.Until(view => view.Status == RunStatus.NeedsAttention && view.Tasks[A].State == TaskState.Blocked);
        f.Runs.Probe = null;
        return stuck;
    }

    [Fact]
    public async Task Preserve_and_restore_clear_a_producer_s_drift_and_its_consumer_starts()
    {
        await using var f = new CoordinatorFixture(Pair());
        var stuck = await Drifted(f);
        var block = stuck.Tasks[A].Block!;
        Assert.Equal((TaskState.Blocked, MaterializationProblem.DirtyWorktree), (stuck.Tasks[B].State, stuck.Tasks[B].Block!.Problem));
        var attempt = block.Attempt!.Value;

        var evidence = f.Coordinator.Evidence(A, attempt)!;
        Assert.Equal(new BlockScope.Checkout(["result.txt"]), Assert.Single(evidence.Blocks).Scope);
        Assert.Equal(new RootExit.Exited(0), evidence.Exit!.Exit);
        Assert.Null(evidence.RootNow);
        Assert.Equal([1, 2], evidence.Captures.Select(capture => capture.Ordinal));
        Assert.IsType<CaptureDisposition.Matched>(Assert.Single(evidence.Dispositions).Disposition);
        Assert.NotNull(evidence.Cleanup);

        var preservation = f.Preparation.Op();
        var preserved = Assert.IsType<Preservation.Preserved>(await f.Coordinator.Preserve(f.Address, A, attempt, preservation).WaitAsync(Bound));
        Assert.Equal("late\n", f.Preparation.Git.Git("show", preserved.Commit.Hex + ":result.txt"));
        var preview = Assert.IsType<RestorePreviewRead.Previewed>(await f.Coordinator.PreviewRestore(f.Address, A, attempt, preservation).WaitAsync(Bound)).Preview;
        Assert.Equal(["result.txt"], preview.Paths.Select(path => path.Path));
        Assert.Equal(0, f.Launches(B));

        var restore = f.Preparation.Op();
        Assert.IsType<Restoration.Restored>(await f.Coordinator.Restore(f.Address, A, attempt, preservation, preview.Identity, restore).WaitAsync(Bound));
        // A repeat returns the receipt and moves nothing again.
        Assert.IsType<Restoration.Restored>(await f.Coordinator.Restore(f.Address, A, attempt, preservation, preview.Identity, restore).WaitAsync(Bound));

        await f.UntilStatus(RunStatus.Completed);
        Assert.Equal("done\n", File.ReadAllText(Path.Combine(f.Checkout(A), "result.txt")));
        Assert.Equal("late\n", f.Preparation.Git.Git("show", preserved.Receipt.Ref + ":result.txt"));
        Assert.Single(f.Read().Results, result => result.Task == A);
        Assert.DoesNotContain(f.Read().Blocks.Values, state => !state.Resolved);
        Assert.Single(f.Read().Restorations);
        Assert.Equal([1, 1], new[] { f.Launches(A), f.Launches(B) });
    }

    [Fact]
    public async Task A_checkout_that_keeps_changing_keeps_the_block_and_moves_nothing()
    {
        await using var f = new CoordinatorFixture(Pair());
        var stuck = await Drifted(f);
        var attempt = stuck.Tasks[A].Block!.Attempt!.Value;
        f.Runs.Probe = point =>
        {
            if (point == "git.preserve-observe-1.after") File.WriteAllText(Path.Combine(f.Checkout(A), "result.txt"), "changing\n");
        };

        var preservation = f.Preparation.Op();
        var block = Assert.IsType<Preservation.Blocked>(await f.Coordinator.Preserve(f.Address, A, attempt, preservation).WaitAsync(Bound)).Block;

        Assert.Equal((MaterializationProblem.DirtyWorktree, "The checkout changed between preservation observations."), (block.Problem, block.Detail));
        Assert.Equal(new BlockScope.Checkout(["result.txt"]), block.Scope);
        Assert.Equal(RunProblem.InvalidData, Assert.IsType<RestorePreviewRead.Rejected>(
            await f.Coordinator.PreviewRestore(f.Address, A, attempt, preservation).WaitAsync(Bound)).Reason.Problem);
        Assert.Empty(f.Read().Restorations);
        Assert.Equal("changing\n", File.ReadAllText(Path.Combine(f.Checkout(A), "result.txt")));
        Assert.Equal(TaskState.Blocked, (await f.Decided()).Tasks[A].State);
    }

    [Fact]
    public async Task A_system_without_file_identity_refuses_to_move_files_and_says_to_change_them_by_hand()
    {
        await using var f = new CoordinatorFixture(Pair());
        var stuck = await Drifted(f);
        var attempt = stuck.Tasks[A].Block!.Attempt!.Value;
        // macOS reads no file identity, so Restore cannot prove that the checkout and its Git folder share a file system.
        f.Runs.Volumes = _ => null;
        var preservation = f.Preparation.Op();
        Assert.IsType<Preservation.Preserved>(await f.Coordinator.Preserve(f.Address, A, attempt, preservation).WaitAsync(Bound));

        var refused = Assert.IsType<RestorePreviewRead.Refused>(await f.Coordinator.PreviewRestore(f.Address, A, attempt, preservation).WaitAsync(Bound));

        Assert.Equal("iDevelop cannot confirm that the checkout and its Git folder share a file system on this system, so Restore will not replace files. " +
            "Change them outside iDevelop, then preserve again.", refused.Detail);
        Assert.Equal("late\n", File.ReadAllText(Path.Combine(f.Checkout(A), "result.txt")));

        // Once the person put the file back by hand, a new preservation restores nothing and clears the block.
        File.WriteAllText(Path.Combine(f.Checkout(A), "result.txt"), "done\n");
        var again = f.Preparation.Op();
        Assert.IsType<Preservation.Preserved>(await f.Coordinator.Preserve(f.Address, A, attempt, again).WaitAsync(Bound));
        var preview = Assert.IsType<RestorePreviewRead.Previewed>(await f.Coordinator.PreviewRestore(f.Address, A, attempt, again).WaitAsync(Bound)).Preview;
        Assert.Empty(preview.Paths);
        Assert.IsType<Restoration.Restored>(await f.Coordinator.Restore(f.Address, A, attempt, again, preview.Identity, f.Preparation.Op()).WaitAsync(Bound));
        await f.UntilStatus(RunStatus.Completed);
        Assert.Equal(1, f.Launches(B));
    }

    [Fact]
    public async Task Only_the_controlling_window_recovers_and_every_command_names_its_run()
    {
        await using var f = new CoordinatorFixture(Pair());
        var stuck = await Drifted(f);
        var attempt = stuck.Tasks[A].Block!.Attempt!.Value;
        var sequence = f.Read().Sequence;
        var (runs, second) = await f.SecondWindow();
        await using (runs)
        {
            Assert.Equal(WorkflowRunCoordinator.ElsewhereMessage, Assert.IsType<Preservation.Unavailable>(
                await second.Preserve(second.Address, A, attempt, f.Preparation.Op()).WaitAsync(Bound)).Message);
            Assert.Equal(WorkflowRunCoordinator.ElsewhereMessage, Assert.IsType<RestorePreviewRead.Unavailable>(
                await second.PreviewRestore(second.Address, A, attempt, f.Preparation.Op()).WaitAsync(Bound)).Message);
            Assert.Equal(WorkflowRunCoordinator.ElsewhereMessage, Assert.IsType<Restoration.Unavailable>(
                await second.Restore(second.Address, A, attempt, f.Preparation.Op(), new(new string('0', 64)), f.Preparation.Op()).WaitAsync(Bound)).Message);
            Assert.Equal(WorkflowRunCoordinator.ElsewhereMessage, Assert.IsType<RunCommand.Unavailable>(
                await second.ConfirmStopped(second.Address, A, attempt, "Stopped by hand.", f.Preparation.Op()).WaitAsync(Bound)).Message);
            Assert.NotNull(second.Evidence(A));
        }
        var elsewhere = f.Address with { Run = new(Guid.Parse("00000000-0000-0000-0000-0000000000ee")) };
        Assert.Equal(RunProblem.IdentityMismatch, Assert.IsType<Preservation.Rejected>(
            await f.Coordinator.Preserve(elsewhere, A, attempt, f.Preparation.Op()).WaitAsync(Bound)).Reason.Problem);
        Assert.Equal(RunProblem.IdentityMismatch, Assert.IsType<Restoration.Rejected>(
            await f.Coordinator.Restore(elsewhere, A, attempt, f.Preparation.Op(), new(new string('0', 64)), f.Preparation.Op()).WaitAsync(Bound)).Reason.Problem);
        // A closure is only for a turn whose execution is unresolved; A's turn settled.
        Assert.Equal(RunProblem.InvalidClaim, Assert.IsType<RunCommand.Refused>(
            await f.Coordinator.ConfirmStopped(f.Address, A, attempt, "Stopped by hand.", f.Preparation.Op()).WaitAsync(Bound)).Reason.Problem);
        Assert.Equal(sequence, f.Read().Sequence);
    }

    [Fact]
    public async Task A_moved_stash_blocks_publication_and_Restore_clears_it_only_once_the_stash_is_back()
    {
        await using var f = new CoordinatorFixture(Pair());
        f.Answer(A, Writes(A, "result.txt", "done\n")).Answer(B, Writes(B, "b.txt", "B\n"));
        await f.Open();
        var moved = false;
        f.Runs.Probe = point =>
        {
            if (point != "coordinator.publish.before" || moved) return;
            moved = true;
            f.Preparation.Git.Git("update-ref", "refs/stash", PlanCommit.Hex);
        };
        await f.Resume();
        var stuck = await f.Until(view => view.Status == RunStatus.NeedsAttention && view.Tasks[A].State == TaskState.Blocked);
        f.Runs.Probe = null;
        var block = stuck.Tasks[A].Block!;
        Assert.Equal((MaterializationProblem.UncertainOwnership, new BlockScope.Refs(["refs/stash"])), (block.Problem, (BlockScope)block.Scope));
        Assert.Equal([new SharedRefDrift("refs/stash", null, PlanCommit, Known: true)], f.Coordinator.Evidence(A)!.Refs.ToArray());
        var attempt = block.Attempt!.Value;

        async Task<Restoration.Restored> Restored()
        {
            var preservation = f.Preparation.Op();
            Assert.IsType<Preservation.Preserved>(await f.Coordinator.Preserve(f.Address, A, attempt, preservation).WaitAsync(Bound));
            var preview = Assert.IsType<RestorePreviewRead.Previewed>(await f.Coordinator.PreviewRestore(f.Address, A, attempt, preservation).WaitAsync(Bound)).Preview;
            Assert.Equal((0, 1), (preview.Repairs.Length, preview.Rechecks.Length));
            return Assert.IsType<Restoration.Restored>(await f.Coordinator.Restore(f.Address, A, attempt, preservation, preview.Identity, f.Preparation.Op()).WaitAsync(Bound));
        }

        // While the stash still points elsewhere, the recheck resolves nothing and the task stays blocked.
        Assert.Empty((await Restored()).Receipt.Resolved);
        Assert.Equal(TaskState.Blocked, (await f.Decided()).Tasks[A].State);
        Assert.Equal(0, f.Launches(B));

        f.Preparation.Git.Git("update-ref", "-d", "refs/stash");
        Assert.NotEmpty((await Restored()).Receipt.Resolved);
        await f.UntilStatus(RunStatus.Completed);
        Assert.Equal([1, 1], new[] { f.Launches(A), f.Launches(B) });
    }

    [Fact]
    public async Task The_evidence_names_the_newest_turn_and_no_process_when_its_launch_was_not_logged()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A, conversation: ConversationMode.Chat), Agent(B)], (A, B)));
        f.Answer(A, Reports(A), Reports(A));
        await f.Open();
        await f.Resume();
        var waiting = await f.Until(view => view.Tasks[A].State == TaskState.Waiting);
        var attempt = waiting.Tasks[A].Attempt!.Value;
        var first = f.Coordinator.Evidence(A)!;
        Assert.Equal((1, true), (first.Turn, first.Root is not null));
        Assert.IsType<AttemptCause.Initial>(first.Cause);
        await f.Close();

        // The next turn is claimed in another process that exits before it launches anything, as a crash does.
        var record = f.Read();
        var launch = new LaunchKey(attempt, 2);
        using (var racer = new Runs.Racer("turn-crash", f.Preparation.Git.Folder, Runs.RunFixtures.W.Value.ToString("D"), f.Preparation.RunId.Value.ToString("D"),
            A.Value.ToString("D"), RunOperations.Turn(record, launch).Value.ToString("D"), f.Fakes.Folder, f.Fakes.LaunchFolder!, "1", "runner.claim.after",
            attempt.Value.ToString("D"), "2", "Use the fixture"))
        {
            Assert.Equal("Owned:", await racer.Line());
            Assert.Equal("runner.claim.after", await racer.Line());
            await racer.Exit();
        }
        await f.OpenAgain();

        Assert.Equal(TaskState.Uncertain, (await f.Decided()).Tasks[A].State);
        var second = f.Coordinator.Evidence(A)!;
        Assert.Equal((2, (ProcessIdentity?)null, (RunEvent.RootExitObserved?)null), (second.Turn, second.Root, second.Exit));
        Assert.Equal(1, f.Launches(A));
    }

    [Fact]
    public async Task A_waiting_attempt_is_not_closed_as_stopped()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A, conversation: ConversationMode.Chat), Agent(B)], (A, B)));
        f.Answer(A, Reports(A)).Answer(B, Writes(B, "b.txt", "B\n"));
        await f.Open();
        await f.Resume();
        var waiting = await f.Until(view => view.Tasks[A].State == TaskState.Waiting);
        var sequence = f.Read().Sequence;

        Assert.Equal(RunProblem.InvalidClaim, Assert.IsType<RunCommand.Refused>(
            await f.Coordinator.ConfirmStopped(f.Address, A, waiting.Tasks[A].Attempt!.Value, "Stopped by hand.", f.Preparation.Op()).WaitAsync(Bound)).Reason.Problem);

        Assert.Equal(sequence, f.Read().Sequence);
        Assert.Empty(f.Read().Closures);
        Assert.Equal(TaskState.Waiting, (await f.Decided()).Tasks[A].State);
    }

    [Fact]
    public async Task A_turn_unresolved_by_a_crash_closes_once_on_the_person_s_confirmation_and_launches_nothing()
    {
        await using var f = new CoordinatorFixture(Chain());
        f.Answer(A, Writes(A, "result.txt", "done\n")).Answer(B, Writes(B, "b.txt", "B\n")).Answer(X, Reports(X));
        await f.Crash(A, "journal.root-exit.before");
        await f.Open();
        await f.Resume();
        var stuck = await f.Until(view => view.Status == RunStatus.NeedsAttention && view.Tasks[X].State == TaskState.Done);
        Assert.Equal(TaskState.Uncertain, stuck.Tasks[A].State);
        var attempt = stuck.Tasks[A].Attempt!.Value;
        var evidence = f.Coordinator.Evidence(A)!;
        Assert.True(evidence.Root!.Value.Id > 0);
        Assert.Equal(ProcessMatch.Gone, evidence.RootNow);
        Assert.Null(evidence.Exit);
        Assert.Empty(evidence.Captures);

        Assert.Equal(RunProblem.ConfirmationRequired, Assert.IsType<RunCommand.Refused>(
            await f.Coordinator.ConfirmStopped(f.Address, A, attempt, " ", f.Preparation.Op()).WaitAsync(Bound)).Reason.Problem);
        var command = f.Preparation.Op();
        Assert.IsType<RunCommand.Accepted>(await f.Coordinator.ConfirmStopped(f.Address, A, attempt, "The client was stopped by hand.", command).WaitAsync(Bound));
        Assert.IsType<RunCommand.Accepted>(await f.Coordinator.ConfirmStopped(f.Address, A, attempt, "The client was stopped by hand.", command).WaitAsync(Bound));

        var closed = await f.Until(view => view.Tasks[A].State == TaskState.Failed);
        var end = Assert.IsType<AttemptEnd.Recovered>(closed.Tasks[A].End);
        Assert.Equal((RecoveryOutcome.Stopped, command, "The client was stopped by hand."), (end.Outcome, end.Confirmation, end.Reason));
        Assert.Equal((RunStatus.NeedsAttention, TaskState.Pending), (closed.Status, closed.Tasks[B].State));
        Assert.Single(f.Read().Closures, closure => f.Read().Attempts[closure.Key].Task == A);
        Assert.Empty(f.Read().UnresolvedClaims);
        Assert.DoesNotContain(f.Read().Results, result => result.Task == A);
        Assert.Equal([1, 0, 1], new[] { f.Launches(A), f.Launches(B), f.Launches(X) });
        Assert.Single(f.Read().Attempts.Values, run => run.Task == A);
        // The turn's lease went with its closure, so the task's lock is free.
        using (Assert.IsType<LeaseTake.Taken>(f.Coordinator.Permit!.TakeTask(A)).Lease) { }
    }

    [Fact]
    public async Task A_stopping_run_waits_for_an_unresolved_turn_until_the_person_closes_it()
    {
        await using var f = new CoordinatorFixture(Chain());
        f.Answer(A, Writes(A, "result.txt", "done\n")).Answer(B, Writes(B, "b.txt", "B\n")).Answer(X, Reports(X));
        await f.Crash(A, "runner.claim.after");
        await f.Open();
        Assert.IsType<RunCommand.Accepted>(await f.Coordinator.Stop(f.Address, f.Preparation.Op()).WaitAsync(Bound));
        var stopping = await f.Until(view => view.Status == RunStatus.Stopping && view.Tasks[A].State == TaskState.Uncertain);
        Assert.Null(f.Coordinator.Evidence(A)!.Root);
        await Task.Delay(200);
        Assert.Equal(RunPhase.StopRequested, f.Read().Phase);

        Assert.IsType<RunCommand.Accepted>(await f.Coordinator.ConfirmStopped(f.Address, A, stopping.Tasks[A].Attempt!.Value,
            "The client never ran.", f.Preparation.Op()).WaitAsync(Bound));

        await f.UntilStatus(RunStatus.Stopped);
        Assert.Equal(RecoveryOutcome.Stopped, Assert.IsType<AttemptEnd.Recovered>(f.View.Tasks[A].End).Outcome);
        Assert.Equal(0, f.TotalLaunches);
    }
}
