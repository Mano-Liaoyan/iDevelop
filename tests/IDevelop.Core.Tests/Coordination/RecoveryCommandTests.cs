using IDevelop.Execution;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Coordination.CoordinatorFixture;

namespace IDevelop.Core.Tests.Coordination;

/// <summary>
/// A person's recovery through the coordinator (E3g.2): the evidence of a task, and the confirmed closure of a turn whose
/// execution stayed unresolved.
/// </summary>
public sealed class RecoveryCommandTests
{
    [Fact]
    public async Task Only_the_controlling_window_closes_a_turn_and_the_command_names_its_run()
    {
        await using var f = new CoordinatorFixture(Chain());
        f.Answer(A, Writes(A, "result.txt", "done\n")).Answer(B, Writes(B, "b.txt", "B\n")).Answer(X, Reports(X));
        await f.Crash(A, "journal.root-exit.before");
        await f.Open();
        await f.Resume();
        var stuck = await f.Until(view => view.Status == RunStatus.NeedsAttention && view.Tasks[X].State == TaskState.Done);
        var attempt = stuck.Tasks[A].Attempt!.Value;
        var sequence = f.Read().Sequence;
        var (runs, second) = await f.SecondWindow();
        await using (runs)
        {
            Assert.Equal(WorkflowRunCoordinator.ElsewhereMessage, Assert.IsType<RunCommand.Unavailable>(
                await second.ConfirmStopped(second.Address, A, attempt, "Stopped by hand.", f.Preparation.Op()).WaitAsync(Bound)).Message);
            Assert.NotNull(second.Evidence(A));
        }
        var elsewhere = f.Address with { Run = new(Guid.Parse("00000000-0000-0000-0000-0000000000ee")) };
        Assert.Equal(RunProblem.IdentityMismatch, Assert.IsType<RunCommand.Refused>(
            await f.Coordinator.ConfirmStopped(elsewhere, A, attempt, "Stopped by hand.", f.Preparation.Op()).WaitAsync(Bound)).Reason.Problem);
        Assert.Equal(sequence, f.Read().Sequence);
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
