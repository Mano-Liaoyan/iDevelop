using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Runs;

public sealed class AuthorityTests
{
    [Theory]
    [InlineData("Plan")]
    [InlineData("Reserve")]
    [InlineData("Refresh")]
    [InlineData("Claim")]
    [InlineData("Recover")]
    [InlineData("Record")]
    [InlineData("AcceptPublication")]
    [InlineData("CloseTurn")]
    [InlineData("CloseAttempt")]
    [InlineData("AcceptReport")]
    [InlineData("ReuseReport")]
    [InlineData("Amend")]
    [InlineData("AmendFromProposal")]
    [InlineData("Stop")]
    [InlineData("Settle")]
    [InlineData("Abandon")]
    public void Every_command_rechecks_released_and_foreign_authority_before_receipts(string command)
    {
        using var f = new RunFixtures();
        f.Approve();
        var reservation = f.Reserve();
        f.Claim(reservation);
        var permit = f.Permit;
        var lease = f.Lease(T);
        var operation = f.Read().Receipts.Values.Single(entry => entry.Sequence == 2).Operation;
        var revision = f.Read().Revision;
        var checkpoint = f.WriteLog(reservation);
        using var other = new RunFixtures();
        other.Approve();
        var before = File.ReadAllBytes(f.Journal(W, Run));
        Assert.Equal(RunProblem.IdentityMismatch, Problem(Invoke(other.Permit, other.Lease(T))));
        f.ReleaseControl();
        Assert.Equal(RunProblem.TaskBusy, Problem(Invoke(permit, lease)));
        Assert.IsType<LeaseTake.Busy>(permit.TakeTask(U));
        Assert.Equal(7, f.Read().Sequence);
        Assert.Equal(before, File.ReadAllBytes(f.Journal(W, Run)));
        var stopped = Assert.IsType<RunDecision.Recorded>(f.Store.Stop(f.Permit, f.Op()));
        Assert.Equal(9, stopped.Record.Sequence);
        Assert.Equal(RunPhase.StopRequested, stopped.Record.Phase);

        RunDecision Invoke(CoordinatorPermit authority, RunLease held) => command switch
        {
            "Plan" => f.Store.Plan(held, operation, revision.Id, new AttemptCause.Initial()),
            "Reserve" => f.Store.Reserve(held, operation, operation),
            "Refresh" => f.Store.Refresh(held, operation, new(A1, 2)),
            "Claim" => f.Store.Claim(held, operation, new(A1, 1), reservation.Inputs, Prompt),
            "Recover" => f.Store.Recover(held, operation, A1, RecoveryOutcome.Stopped, f.Op(), "Stopped."),
            "Record" => f.Store.Record(authority, operation, new RunEvent.Prepared(f.Read().Preparations[new(A1, 1)], SharedRefs)),
            "AcceptPublication" => f.Store.AcceptPublication(authority, operation, operation),
            "CloseTurn" => f.Store.CloseTurn(authority, operation, new(A1, 1), checkpoint),
            "CloseAttempt" => f.Store.CloseAttempt(authority, operation, A1, TerminalAttemptOutcome.Succeeded, checkpoint),
            "AcceptReport" => f.Store.AcceptReport(authority, operation, A1, reservation.Inputs.Id, "Checked."),
            "ReuseReport" => f.Store.ReuseReport(authority, operation, T, new(T, A1), f.Op()),
            "Amend" => f.Store.Amend(authority, operation, revision.Id, revision, new AmendmentOrigin.Person(), f.Op()),
            "AmendFromProposal" => f.Store.AmendFromProposal(authority, operation, revision.Id, new(T, A1, 1, [], [], []),
                new HashSet<TaskId>(), f.Op()),
            "Stop" => f.Store.Stop(authority, operation),
            "Settle" => f.Store.Settle(authority, operation, RunOutcome.Stopped),
            "Abandon" => f.Store.Abandon(authority, operation, f.Op(), "Abandoned."),
            _ => throw new InvalidOperationException(command),
        };
    }

    [Fact]
    public void Reservation_receipts_and_refresh_commands_require_the_attempts_task()
    {
        using var f = new RunFixtures(FixtureWorkflow(Task(T), Task(U)));
        f.Approve();
        var reserved = f.Reserve(T);
        f.Claim(reserved);
        var plan = f.Read().Receipts.Values.Single(entry => entry.Sequence == 2).Operation;
        var reserve = f.Read().Receipts.Values.Single(entry => entry.Sequence == 3).Operation;
        Assert.Equal(RunProblem.IdentityMismatch, Problem(f.Store.Reserve(f.Lease(U), reserve, plan)));
        Assert.IsType<RunDecision.Existing>(f.Store.Reserve(f.Lease(T), reserve, plan));
        var refresh = f.Op();
        var launch = new LaunchKey(A1, 2);
        Assert.Equal(RunProblem.IdentityMismatch, Problem(f.Store.Refresh(f.Lease(U), refresh, launch)));
        Assert.Equal(RunProblem.InvalidClaim, Problem(f.Store.Refresh(f.Lease(T), refresh, launch)));
        Assert.Equal(7, f.Read().Sequence);
        Assert.Equal(8, Assert.IsType<RunDecision.Recorded>(f.Store.Stop(f.Permit, f.Op())).Record.Sequence);
    }

    [Fact]
    public void A_fenced_attempt_with_terminal_evidence_closes_through_recovery()
    {
        using var f = new RunFixtures();
        f.Approve();
        var reserved = f.Reserve();
        f.Claim(reserved);
        var checkpoint = f.WriteLog(reserved);
        f.ReleaseControl();
        var permit = f.Permit;
        var bytes = File.ReadAllBytes(f.Journal(W, Run));
        Assert.Equal(RunProblem.UnresolvedOwnership, Problem(f.Store.CloseAttempt(permit, f.Op(), A1,
            TerminalAttemptOutcome.Succeeded, checkpoint)));
        Assert.Equal(bytes, File.ReadAllBytes(f.Journal(W, Run)));
        Assert.Equal(8, f.Read().Sequence);
        var recovered = Assert.IsType<RunDecision.Recorded>(f.Store.Recover(f.Lease(T), f.Op(), A1));
        Assert.Equal(9, recovered.Record.Sequence);
        Assert.Equal(new AttemptEnd.Logged(TerminalAttemptOutcome.Succeeded, checkpoint), recovered.Record.Closures[A1]);
        Assert.IsType<RunDecision.Existing>(f.Store.CloseAttempt(permit, f.Op(), A1, TerminalAttemptOutcome.Succeeded, checkpoint));
    }

    [Fact]
    public void Legacy_stop_preserves_schema_two_and_cannot_mutate_schema_three()
    {
        using var legacy = new RunFixtures();
        File.WriteAllBytes(legacy.Journal(W, Run), File.ReadAllBytes(Fixture.Path("e2-run/events.jsonl")));
        var stopped = Assert.IsType<RunDecision.Recorded>(legacy.Store.Stop(new LegacyRun(W, Run), new OperationId(Id(2003))));
        Assert.Equal(28, stopped.Record.Sequence);
        Assert.Equal(2, stopped.Record.Receipts.Values.Single(entry => entry.Sequence == 28).Schema);
        using var f = new RunFixtures();
        f.Approve();
        Assert.Equal(RunProblem.UnsupportedSchema, Problem(f.Store.Stop(new LegacyRun(W, Run), f.Op())));
        Assert.Equal(RunProblem.UnsupportedSchema, Problem(f.Store.Settle(new LegacyRun(W, Run), f.Op(), RunOutcome.Stopped)));
        Assert.Equal(RunProblem.UnsupportedSchema, Problem(f.Store.Abandon(new LegacyRun(W, Run), f.Op(), f.Op(), "Abandoned.")));
        Assert.Equal(1, f.Read().Sequence);
        Assert.Equal(2, Assert.IsType<RunDecision.Recorded>(f.Store.Stop(f.Permit, f.Op())).Record.Sequence);
    }

    [Fact]
    public void Transfer_waits_for_scopes_without_releasing_the_task_lock()
    {
        using var f = new RunFixtures();
        f.Approve();
        var old = f.Lease(T);
        var scope = old.Use()!;
        using var next = old.Transfer();
        Assert.True(old.Held);
        Assert.False(next.Held);
        Assert.Null(old.Use());
        Assert.Null(next.Use());
        Assert.Null(StandaloneLease.TryTake(f.Project, T));
        old.Dispose();
        Assert.True(old.Held);
        scope.Dispose();
        Assert.False(old.Held);
        Assert.True(next.Held);
        Assert.Null(StandaloneLease.TryTake(f.Project, T));
        using (next.Use()) Assert.True(next.Held);
        next.Dispose();
        using var control = StandaloneLease.TryTake(f.Project, T);
        Assert.NotNull(control);
        Assert.True(control.Held);
    }

    [Fact]
    public void Disposing_the_successor_during_old_scopes_defers_release()
    {
        using var f = new RunFixtures();
        f.Approve();
        var old = f.Lease(T);
        var scope = old.Use()!;
        var next = old.Transfer();
        next.Dispose();
        Assert.True(old.Held);
        Assert.False(next.Held);
        Assert.Null(StandaloneLease.TryTake(f.Project, T));
        scope.Dispose();
        Assert.False(old.Held);
        Assert.False(next.Held);
        using var control = StandaloneLease.TryTake(f.Project, T);
        Assert.NotNull(control);
        Assert.True(control.Held);
    }
}
