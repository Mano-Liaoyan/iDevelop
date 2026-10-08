using System.Diagnostics;
using IDevelop.Execution;
using IDevelop.Projects;
using IDevelop.TestSupport;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;
using static IDevelop.Core.Tests.Runs.RunFixtures;
using RunFixtures = IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Materialization;

public sealed class LeaseTests
{
    [Fact]
    public async System.Threading.Tasks.Task A_competing_standalone_owner_refuses_the_lease_without_a_durable_block()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var permit = f.Permit;
        var operation = f.Op();
        using (var competing = StandaloneLease.TryTake(f.Git.Folder, T))
        {
            Assert.NotNull(competing);
            Assert.IsType<LeaseTake.Busy>(permit.TakeTask(T));
            Assert.Equal(1, f.Read().Sequence);
            Assert.Empty(f.Read().Blocks);
            Assert.DoesNotContain("\"type\":\"blocked\"", File.ReadAllText(Journal(f)));
        }
        var lease = f.Lease(T);
        Assert.Same(permit, lease.Permit);
        Assert.IsType<LeaseTake.Busy>(permit.TakeTask(T));
        var ready = Assert.IsType<Preparation.Ready>(await f.Materializer().Prepare(lease, operation, new AttemptCause.Initial()));
        Assert.Equal(A1, ready.Execution.Launch.Attempt);
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal(10, f.Read().Sequence);
        Assert.IsType<RunEvent.Prepared>(f.Read().Receipts[OperationIds.Derive(operation, "prepared")].Event);
    }

    [Fact]
    public async System.Threading.Tasks.Task A_competing_process_refuses_the_lease_then_the_same_owner_and_operation_prepare()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var permit = f.Permit;
        var operation = f.Op();
        var release = Path.Combine(f.Git.Folder, "release");
        Directory.CreateDirectory(AttemptLog.TaskFolder(DataFolder.Attempts(f.Git.Folder), T));
        using var fakes = new FakeClients(Path.Combine(f.Git.Folder, ".idp", "fakes"));
        var shim = fakes.Install("holder", FakeRule.On()
            .LockFile(Path.Combine(AttemptLog.TaskFolder(DataFolder.Attempts(f.Git.Folder), T), "run.lock"))
            .Print("locked").WaitForFile(release));
        using var holder = Process.Start(new ProcessStartInfo(shim)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        })!;
        try
        {
            Assert.Equal("locked", await holder.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.IsType<LeaseTake.Busy>(permit.TakeTask(T));
            Assert.Equal(1, f.Read().Sequence);
            Assert.Empty(f.Read().Blocks);
            Assert.DoesNotContain("\"type\":\"blocked\"", File.ReadAllText(Journal(f)));
        }
        finally
        {
            File.WriteAllText(release, "release");
            await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        var lease = f.Lease(T);
        Assert.Same(permit, lease.Permit);
        var ready = Assert.IsType<Preparation.Ready>(await f.Materializer().Prepare(lease, operation, new AttemptCause.Initial()));
        Assert.Equal(A1, ready.Execution.Launch.Attempt);
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal(10, f.Read().Sequence);
        Assert.IsType<RunEvent.Prepared>(f.Read().Receipts[OperationIds.Derive(operation, "prepared")].Event);
    }

    [Fact]
    public async System.Threading.Tasks.Task Transfer_rejects_the_original_object_and_the_transferred_lease_prepares()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var original = f.Lease(T);
        using var transferred = original.Transfer();
        var operation = f.Op();
        Assert.Equal(RunProblem.TaskBusy, Assert.IsType<Preparation.Rejected>(
            await f.Materializer().Prepare(original, operation, new AttemptCause.Initial())).Reason.Problem);
        Assert.Equal(1, f.Read().Sequence);
        Assert.False(original.Held);
        Assert.True(transferred.Held);
        Assert.IsType<LeaseTake.Busy>(f.Permit.TakeTask(T));
        var ready = Assert.IsType<Preparation.Ready>(await f.Materializer().Prepare(transferred, operation, new AttemptCause.Initial()));
        Assert.Equal(A1, ready.Execution.Launch.Attempt);
        Assert.Equal(10, f.Read().Sequence);
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
    }

    [Fact]
    public async System.Threading.Tasks.Task Publication_and_turn_preparation_reject_another_tasks_lease_before_mutating()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(U)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await f.Close(ready);
        var wrong = f.Lease(U);
        var operation = f.Op();
        Assert.Equal(RunProblem.IdentityMismatch, Assert.IsType<Publication.Rejected>(
            f.Materializer().Publish(wrong, operation, ready.Execution.Launch.Attempt)).Reason.Problem);
        Assert.Equal(RunProblem.IdentityMismatch, Assert.IsType<Preparation.Rejected>(await f.Materializer()
            .PrepareTurn(wrong, f.Op(), new(ready.Execution.Launch.Attempt, 2), "Continue.")).Reason.Problem);
        Assert.Equal(17, f.Read().Sequence);
        Assert.Empty(f.Read().Results);
        Assert.Equal(T, Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), operation,
            ready.Execution.Launch.Attempt)).Result.Task);
        Assert.Equal(T, Assert.Single(f.Read().Results).Task);
    }

    [Fact]
    public async System.Threading.Tasks.Task A_standalone_lease_blocks_run_owned_preparation_authority()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var operation = f.Op();
        using (var standalone = StandaloneLease.TryTake(f.Git.Folder, T))
        {
            Assert.NotNull(standalone);
            Assert.IsType<LeaseTake.Busy>(f.Permit.TakeTask(T));
            Assert.Equal(1, f.Read().Sequence);
            Assert.Empty(f.Read().Blocks);
        }
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T, operation));
        Assert.Equal(A1, ready.Execution.Launch.Attempt);
        Assert.Equal(10, f.Read().Sequence);
    }

    [Fact]
    public async System.Threading.Tasks.Task A_foreign_project_permit_is_rejected_before_git_or_journal_work()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        using var other = new RunFixtures();
        other.Approve();
        var operation = f.Op();
        Assert.Equal(RunProblem.IdentityMismatch, Assert.IsType<Preparation.Rejected>(await f.Materializer()
            .Prepare(other.Lease(T), operation, new AttemptCause.Initial())).Reason.Problem);
        Assert.Equal(1, f.Read().Sequence);
        Assert.Equal(1, other.Read().Sequence);
        Assert.Empty(f.Read().Blocks);
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T, operation));
        Assert.Equal(A1, ready.Execution.Launch.Attempt);
        Assert.Equal(10, f.Read().Sequence);
    }

    [Fact]
    public async System.Threading.Tasks.Task A_busy_repository_does_not_release_the_callers_task_lease()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var lease = f.Lease(T);
        var operation = f.Op();
        using (var mutation = f.Git.Open().TakeMutationLock())
        {
            Assert.NotNull(mutation);
            Assert.Equal(RunProblem.JournalBusy, Assert.IsType<Preparation.Rejected>(await f.Materializer()
                .Prepare(lease, operation, new AttemptCause.Initial())).Reason.Problem);
            Assert.Equal(1, f.Read().Sequence);
            Assert.Empty(f.Read().Blocks);
            Assert.True(lease.Held);
            Assert.IsType<LeaseTake.Busy>(f.Permit.TakeTask(T));
        }
        var ready = Assert.IsType<Preparation.Ready>(await f.Materializer().Prepare(lease, operation, new AttemptCause.Initial()));
        Assert.Equal(A1, ready.Execution.Launch.Attempt);
        Assert.Equal(10, f.Read().Sequence);
        Assert.True(lease.Held);
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
    }

    [Fact]
    public void Recovery_under_the_owners_lease_records_once_and_rejects_another_task_even_on_replay()
    {
        using var f = new RunFixtures(FixtureWorkflow(Task(T), Task(U)));
        f.Approve();
        var reserved = f.Reserve(T);
        f.Claim(reserved);
        var lease = f.Lease(T);
        var operation = f.Op();
        var confirmation = f.Op();
        Assert.Equal(RunProblem.IdentityMismatch, Problem(f.Store.Recover(f.Lease(U), operation, A1,
            RecoveryOutcome.Stopped, confirmation, "Stopped.")));
        Assert.Equal(7, f.Read().Sequence);
        Assert.Empty(f.Read().Closures);
        var recovered = Assert.IsType<RunDecision.Recorded>(f.Store.Recover(lease, operation, A1,
            RecoveryOutcome.Stopped, confirmation, "Stopped."));
        Assert.Equal(8, recovered.Record.Sequence);
        Assert.Equal(RecoveryOutcome.Stopped, Assert.IsType<AttemptEnd.Recovered>(recovered.Record.Closures[A1]).Outcome);
        Assert.True(lease.Held);
        Assert.Equal(RunProblem.IdentityMismatch, Problem(f.Store.Recover(f.Lease(U), operation, A1,
            RecoveryOutcome.Stopped, confirmation, "Stopped.")));
        Assert.Equal(8, f.Read().Sequence);
        Assert.IsType<RunDecision.Existing>(f.Store.Recover(lease, operation, A1,
            RecoveryOutcome.Stopped, confirmation, "Stopped."));
        Assert.Equal(8, f.Read().Sequence);
    }

    [Fact]
    public void Claim_rejects_another_task_and_released_lease_and_grants_only_once()
    {
        using var f = new RunFixtures(FixtureWorkflow(Task(T), Task(U)));
        f.Approve();
        var reserved = f.Reserve(T);
        f.Prepare(reserved);
        var operation = f.Op();
        var key = new LaunchKey(A1, 1);
        var released = f.Lease(T);
        Assert.Equal(RunProblem.IdentityMismatch, Problem(f.Store.Claim(f.Lease(U), operation, key, reserved.Inputs, Prompt)));
        f.Release(T);
        Assert.Equal(RunProblem.TaskBusy, Problem(f.Store.Claim(released, operation, key, reserved.Inputs, Prompt)));
        Assert.Equal(6, f.Read().Sequence);
        Assert.Empty(f.Read().Claims);
        var lease = f.Lease(T);
        var granted = Assert.IsType<RunDecision.Granted>(f.Store.Claim(lease, operation, key, reserved.Inputs, Prompt));
        Assert.Equal(new LaunchKey(A1, 1), granted.Claim.Key);
        Assert.Equal(7, granted.Record.Sequence);
        Assert.Equal(RunProblem.IdentityMismatch, Problem(f.Store.Claim(f.Lease(U), operation, key, reserved.Inputs, Prompt)));
        Assert.Equal(7, f.Read().Sequence);
        Assert.IsType<RunDecision.Existing>(f.Store.Claim(lease, operation, key, reserved.Inputs, Prompt));
        Assert.IsType<RunDecision.Existing>(f.Store.Claim(lease, f.Op(), key, reserved.Inputs, Prompt));
        Assert.Equal(7, f.Read().Sequence);
        Assert.Equal(new LaunchKey(A1, 1), Assert.Single(f.Read().Claims).Key);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Legacy_recovery_accepts_a_standalone_lease_and_preserves_the_schema(int schema)
    {
        using var f = new RunFixtures();
        var path = Fixture.Path($"e{schema}-run/events.jsonl");
        if (schema == 1) File.WriteAllText(f.Journal(W, Run), string.Join("\n", File.ReadLines(path).Take(7)) + "\n");
        else File.WriteAllBytes(f.Journal(W, Run), File.ReadAllBytes(path));
        var attempt = new AttemptId(Id(schema == 1 ? 105 : 104));
        var operation = new OperationId(Id(2003));
        var confirmation = new OperationId(Id(2004));
        using var wrong = StandaloneLease.TryTake(f.Project, T)!;
        Assert.Equal(RunProblem.IdentityMismatch, Problem(f.Store.Recover(new LegacyRun(W, Run), wrong, operation, attempt, RecoveryOutcome.Stopped, confirmation, "Stopped.")));
        Assert.Equal(schema == 1 ? 7 : 27, f.Read().Sequence);
        using var lease = StandaloneLease.TryTake(f.Project, U)!;
        var recovered = Assert.IsType<RunDecision.Recorded>(f.Store.Recover(new LegacyRun(W, Run), lease, operation, attempt, RecoveryOutcome.Stopped, confirmation, "Stopped."));
        Assert.Equal(schema, recovered.Record.Schema);
        Assert.Equal(schema == 1 ? 8 : 28, recovered.Record.Sequence);
        Assert.Equal(RecoveryOutcome.Stopped, Assert.IsType<AttemptEnd.Recovered>(recovered.Record.Closures[attempt]).Outcome);
    }

    [Fact]
    public void A_legacy_claim_refuses_both_new_commands_and_identical_receipts()
    {
        using var f = new RunFixtures();
        f.Approve();
        var reserved = f.Reserve();
        f.Prepare(reserved);
        var lease = f.Lease(T);
        var current = File.ReadAllText(f.Journal(W, Run));
        File.WriteAllText(f.Journal(W, Run), current.Replace("\"schema\":3", "\"schema\":2", StringComparison.Ordinal));
        var operation = f.Op();
        var key = new LaunchKey(A1, 1);
        Assert.Equal(RunProblem.UnsupportedSchema, Problem(f.Store.Claim(lease, operation, key, reserved.Inputs, Prompt)));
        Assert.Equal(6, f.Read().Sequence);
        Assert.Empty(f.Read().Claims);
        File.WriteAllText(f.Journal(W, Run), current);
        Assert.IsType<RunDecision.Granted>(f.Store.Claim(lease, operation, key, reserved.Inputs, Prompt));
        var claimed = File.ReadAllText(f.Journal(W, Run));
        File.WriteAllText(f.Journal(W, Run), claimed.Replace("\"schema\":3", "\"schema\":2", StringComparison.Ordinal));
        Assert.Equal(RunProblem.UnsupportedSchema, Problem(f.Store.Claim(lease, operation, key, reserved.Inputs, Prompt)));
        Assert.Equal(RunProblem.UnsupportedSchema, Problem(f.Store.Claim(lease, f.Op(), key, reserved.Inputs, Prompt)));
        Assert.Equal((2, 7L), (f.Read().Schema, f.Read().Sequence));
        Assert.Equal(new LaunchKey(A1, 1), Assert.Single(f.Read().Claims).Key);
    }

    [Fact]
    public async System.Threading.Tasks.Task A_released_permit_revokes_the_task_leases_authority()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var lease = f.Lease(T);
        var operation = f.Op();
        f.Permit.Dispose();
        Assert.False(lease.Held);
        Assert.Equal(RunProblem.TaskBusy, Assert.IsType<Preparation.Rejected>(await f.Materializer()
            .Prepare(lease, operation, new AttemptCause.Initial())).Reason.Problem);
        Assert.Equal(1, f.Read().Sequence);
        Assert.Empty(f.Read().Blocks);
        f.ReleaseControl();
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T, operation));
        Assert.Equal(A1, ready.Execution.Launch.Attempt);
        Assert.Equal(10, f.Read().Sequence);
    }

    [Fact]
    public void Claim_keeps_unknown_attempts_distinct_and_refuses_foreign_authority()
    {
        using var f = new RunFixtures();
        using var other = new RunFixtures();
        f.Approve();
        other.Approve();
        var reserved = f.Reserve();
        f.Prepare(reserved);
        var operation = f.Op();
        var key = new LaunchKey(A1, 1);
        Assert.Equal(RunProblem.IdentityMismatch, Problem(f.Store.Claim(other.Lease(T), operation, key, reserved.Inputs, Prompt)));
        var lease = f.Lease(T);
        Assert.Equal(RunProblem.UnknownAttempt, Problem(f.Store.Claim(lease, operation, new(new(Id(999)), 1), reserved.Inputs, Prompt)));
        Assert.Equal(6, f.Read().Sequence);
        Assert.Equal(1, other.Read().Sequence);
        Assert.Empty(f.Read().Claims);
        Assert.IsType<RunDecision.Granted>(f.Store.Claim(lease, operation, key, reserved.Inputs, Prompt));
        Assert.Equal(7, f.Read().Sequence);
        Assert.Equal(new LaunchKey(A1, 1), Assert.Single(f.Read().Claims).Key);
    }

    [Fact]
    public void Schema_three_recovery_requires_its_own_live_permit()
    {
        using var f = new RunFixtures();
        f.Approve();
        var reserved = f.Reserve();
        f.Claim(reserved);
        f.Release(T);
        var operation = f.Op();
        var confirmation = f.Op();
        using (var standalone = StandaloneLease.TryTake(f.Project, T))
        {
            Assert.NotNull(standalone);
            Assert.Equal(RunProblem.UnsupportedSchema, Problem(f.Store.Recover(new LegacyRun(W, Run), standalone,
                operation, A1, RecoveryOutcome.Stopped, confirmation, "Stopped.")));
        }
        using var other = new RunFixtures();
        other.Approve();
        Assert.Equal(RunProblem.IdentityMismatch, Problem(f.Store.Recover(other.Lease(T), operation, A1,
            RecoveryOutcome.Stopped, confirmation, "Stopped.")));
        var lease = f.Lease(T);
        f.Release(T);
        Assert.Equal(RunProblem.TaskBusy, Problem(f.Store.Recover(lease, operation, A1,
            RecoveryOutcome.Stopped, confirmation, "Stopped.")));
        Assert.Equal(7, f.Read().Sequence);
        Assert.Empty(f.Read().Closures);
        Assert.IsType<RunDecision.Recorded>(f.Store.Recover(f.Lease(T), operation, A1,
            RecoveryOutcome.Stopped, confirmation, "Stopped."));
        Assert.Equal(8, f.Read().Sequence);
        Assert.Equal(RecoveryOutcome.Stopped, Assert.IsType<AttemptEnd.Recovered>(f.Read().Closures[A1]).Outcome);
    }

    private static string Journal(PreparationFixture f) => Path.Combine(new RunStorage(f.Git.Folder, W, f.RunId).Folder, "events.jsonl");
}
