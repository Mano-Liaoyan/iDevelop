using System.Collections.Immutable;
using System.Diagnostics;
using IDevelop.Execution;
using IDevelop.TestSupport;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Runs;

[Collection(ProcessCollection.Name)]
public sealed class ControlTests
{
    [Fact]
    public async System.Threading.Tasks.Task Separate_stores_race_for_one_permit_and_the_loser_can_retry()
    {
        using var f = new RunFixtures();
        f.Approve();
        for (var iteration = 0; iteration < 10; iteration++)
        {
            using var barrier = new Barrier(3);
            var stores = new[] { f.NewStore(), f.NewStore() };
            var attempts = stores.Select(store => System.Threading.Tasks.Task.Run(() =>
            {
                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
                return store.TakeControl(W, Run);
            })).ToArray();
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
            var outcomes = await System.Threading.Tasks.Task.WhenAll(attempts);
            try
            {
                Assert.Equal(new[] { "Busy", "Owned" }, outcomes.Select(outcome => outcome.GetType().Name).Order());
                var owned = Assert.Single(outcomes.OfType<ControlTake.Owned>());
                Assert.True(owned.Permit.Held);
                Assert.Empty(owned.Fenced);
                owned.Permit.Dispose();
                var loser = Array.FindIndex(outcomes, outcome => outcome is ControlTake.Busy);
                var retry = Assert.IsType<ControlTake.Owned>(stores[loser].TakeControl(W, Run));
                using (retry.Permit) Assert.True(retry.Permit.Held);
            }
            finally
            {
                foreach (var owned in outcomes.OfType<ControlTake.Owned>()) owned.Permit.Dispose();
            }
        }
        Assert.Equal((3, 1L), (f.Read().Schema, f.Read().Sequence));
    }

    [Fact]
    public async System.Threading.Tasks.Task Another_process_holds_control_until_it_exits()
    {
        using var f = new RunFixtures();
        f.Approve();
        var release = Path.Combine(f.Project, "release");
        using var holder = Holder(f, release);
        try
        {
            Assert.Equal("locked", await holder.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.IsType<ControlTake.Busy>(f.Store.TakeControl(W, Run));
        }
        finally
        {
            File.WriteAllText(release, "release");
            await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        var owned = Assert.IsType<ControlTake.Owned>(f.Store.TakeControl(W, Run));
        using (owned.Permit)
        {
            Assert.True(owned.Permit.Held);
            Assert.Equal((Path.GetFullPath(f.Project), W, Run), (owned.Permit.Project, owned.Permit.Workflow, owned.Permit.Run));
            Assert.Empty(owned.Fenced);
        }
        Assert.Equal(1, f.Read().Sequence);
    }

    [Fact]
    public async System.Threading.Tasks.Task A_crashed_owner_is_fenced_once_until_recovery_closes_its_claim()
    {
        using var f = new RunFixtures();
        f.Approve();
        var reserved = f.Reserve();
        Assert.Equal(A1, reserved.Attempt.Id);
        f.Claim(reserved);
        f.ReleaseControl();
        using var holder = Holder(f, Path.Combine(f.Project, "release"));
        long before;
        try
        {
            Assert.Equal("locked", await holder.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            before = f.Read().Sequence;
            Assert.IsType<ControlTake.Busy>(f.Store.TakeControl(W, Run));
        }
        finally
        {
            holder.Kill(entireProcessTree: true);
            await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        var taken = Assert.IsType<ControlTake.Owned>(f.Store.TakeControl(W, Run));
        using (taken.Permit)
        {
            Assert.Equal(new[] { new LaunchKey(A1, 1) }, taken.Fenced);
            var record = f.Read();
            Assert.Equal(before + 1, record.Sequence);
            var last = record.Receipts.Values.Single(entry => entry.Sequence == before + 1);
            Assert.Equal(3, last.Schema);
            Assert.Equal(new[] { new LaunchKey(A1, 1) }, Assert.IsType<RunEvent.OwnershipFenced>(last.Event).Claims);
            Assert.Contains("\"type\":\"ownershipFenced\"", RunJournal.Encode(last));
            Assert.Equal(new[] { new LaunchKey(A1, 1) }, record.UnresolvedClaims);
            Assert.Equal(new[] { new LaunchKey(A1, 1) }, record.Fenced);
        }
        var again = Assert.IsType<ControlTake.Owned>(f.Store.TakeControl(W, Run));
        using (again.Permit)
        {
            Assert.Equal(new[] { new LaunchKey(A1, 1) }, again.Fenced);
            Assert.Equal(before + 1, f.Read().Sequence);
            using var lease = Assert.IsType<LeaseTake.Taken>(again.Permit.TakeTask(T)).Lease;
            Assert.IsType<RunDecision.Recorded>(f.Store.Recover(lease, f.Op(), A1, RecoveryOutcome.Stopped, f.Op(), "Stopped."));
        }
        var afterRecovery = f.Read().Sequence;
        var recovered = Assert.IsType<ControlTake.Owned>(f.Store.TakeControl(W, Run));
        using (recovered.Permit) Assert.Empty(recovered.Fenced);
        Assert.Equal(afterRecovery, f.Read().Sequence);
        Assert.Equal(RecoveryOutcome.Stopped, Assert.IsType<AttemptEnd.Recovered>(f.Read().Closures[A1]).Outcome);
    }

    [Fact]
    public void The_same_owner_closes_its_claim_before_releasing_control_so_no_fence_is_needed()
    {
        using var f = new RunFixtures();
        f.Approve();
        var reserved = f.Reserve();
        f.Prepare(reserved);
        f.ReleaseControl();
        var owned = Assert.IsType<ControlTake.Owned>(f.Store.TakeControl(W, Run));
        using (owned.Permit)
        {
            using var lease = Assert.IsType<LeaseTake.Taken>(owned.Permit.TakeTask(T)).Lease;
            f.Claim(reserved, lease: lease);
            Assert.IsType<RunDecision.Recorded>(f.Store.Recover(lease, f.Op(), reserved.Attempt.Id, RecoveryOutcome.Stopped, f.Op(), "Stopped."));
            Assert.Equal(RecoveryOutcome.Stopped, Assert.IsType<AttemptEnd.Recovered>(f.Read().Closures[reserved.Attempt.Id]).Outcome);
        }
        var before = f.Read().Sequence;
        var next = Assert.IsType<ControlTake.Owned>(f.Store.TakeControl(W, Run));
        using (next.Permit) Assert.Empty(next.Fenced);
        Assert.Equal(before, f.Read().Sequence);
        Assert.Empty(f.Read().Fenced);
    }

    [Fact]
    public void Takeover_fences_only_new_claims_and_returns_all_unresolved_claims_in_order()
    {
        using var f = new RunFixtures(FixtureWorkflow(Task(T), Task(U)));
        f.Approve();
        f.Claim(f.Reserve(T));
        f.ReleaseControl();
        var first = Assert.IsType<ControlTake.Owned>(f.Store.TakeControl(W, Run));
        using (first.Permit) Assert.Equal(new[] { new LaunchKey(A1, 1) }, first.Fenced);
        f.Claim(f.Reserve(U));
        f.ReleaseControl();
        var next = Assert.IsType<ControlTake.Owned>(f.Store.TakeControl(W, Run));
        using (next.Permit)
        {
            Assert.Equal(new[] { new LaunchKey(A1, 1), new LaunchKey(new(Id(105)), 1) }, next.Fenced);
            var record = f.Read();
            Assert.Equal(14, record.Sequence);
            Assert.Equal(new[] { new LaunchKey(new(Id(105)), 1) },
                Assert.IsType<RunEvent.OwnershipFenced>(record.Receipts.Values.Single(entry => entry.Sequence == 14).Event).Claims);
            Assert.Equal(new[] { new LaunchKey(A1, 1), new LaunchKey(new(Id(105)), 1) }, record.UnresolvedClaims);
        }
        var repeat = Assert.IsType<ControlTake.Owned>(f.Store.TakeControl(W, Run));
        using (repeat.Permit)
            Assert.Equal(new[] { new LaunchKey(A1, 1), new LaunchKey(new(Id(105)), 1) }, repeat.Fenced);
        Assert.Equal(14, f.Read().Sequence);
    }

    [Fact]
    public void Task_lease_transfer_keeps_the_same_lock_held_between_owners()
    {
        using var f = new RunFixtures();
        f.Approve();
        var owned = Assert.IsType<ControlTake.Owned>(f.Store.TakeControl(W, Run));
        using var permit = owned.Permit;
        var original = Assert.IsType<LeaseTake.Taken>(permit.TakeTask(T)).Lease;
        Assert.Equal((Path.GetFullPath(f.Project), T, permit), (original.Project, original.Task, original.Permit));
        using var transferred = original.Transfer();
        Assert.False(original.Held);
        Assert.True(transferred.Held);
        Assert.Same(permit, transferred.Permit);
        original.Dispose();
        Assert.IsType<LeaseTake.Busy>(permit.TakeTask(T));
        Assert.Null(StandaloneLease.TryTake(f.Project, T));
        transferred.Dispose();
        var retaken = Assert.IsType<LeaseTake.Taken>(permit.TakeTask(T));
        using (retaken.Lease) Assert.True(retaken.Lease.Held);
        Assert.Throws<InvalidOperationException>(() => transferred.Transfer());
        Assert.Throws<InvalidOperationException>(() => original.Transfer());
        using (var standalone = StandaloneLease.TryTake(f.Project, T))
        {
            Assert.NotNull(standalone);
            Assert.True(standalone.Held);
            Assert.IsType<LeaseTake.Busy>(permit.TakeTask(T));
        }
        using var standaloneAgain = StandaloneLease.TryTake(f.Project, T);
        Assert.NotNull(standaloneAgain);
        Assert.True(standaloneAgain.Held);
        permit.Dispose();
        Assert.False(permit.Held);
        Assert.IsType<LeaseTake.Busy>(permit.TakeTask(U));
    }

    [Fact]
    public void Rejected_fence_releases_control_and_preserves_the_journals_reason()
    {
        using var f = new RunFixtures();
        f.Approve();
        var damaged = f.Journal(W, OtherRun);
        File.WriteAllText(damaged, """{"schema":4,"sequence":1,"event":{"type":"approved"}}""" + "\n");
        Assert.Equal(new RunRejection(RunProblem.UnsupportedSchema, 1),
            Assert.IsType<ControlTake.Rejected>(f.Store.TakeControl(W, Run)).Reason);
        File.Delete(damaged);
        var next = Assert.IsType<ControlTake.Owned>(f.Store.TakeControl(W, Run));
        using (next.Permit) Assert.True(next.Permit.Held);
        Assert.Equal(1, f.Read().Sequence);
        Assert.Equal(new RunRejection(RunProblem.NotApproved),
            Assert.IsType<ControlTake.Rejected>(f.Store.TakeControl(W, OtherRun)).Reason);
    }

    [Fact]
    public void Fences_reject_empty_duplicate_unknown_closed_and_already_fenced_claims()
    {
        using var f = new RunFixtures(FixtureWorkflow(Task(T), Task(U)));
        f.Approve();
        var t = f.Reserve(T);
        f.Claim(t);
        var u = f.Reserve(U);
        f.Claim(u);
        Assert.IsType<RunDecision.Recorded>(f.Store.Recover(f.Lease(u.Attempt.Task), f.Op(), u.Attempt.Id, RecoveryOutcome.Stopped, f.Op(), "Stopped."));
        var record = f.Read();
        var key = new LaunchKey(t.Attempt.Id, 1);
        foreach (ImmutableArray<LaunchKey> claims in new ImmutableArray<LaunchKey>[]
            { [], [key, key], [new(new(Id(999)), 1)], [new(u.Attempt.Id, 1)] })
        {
            var entry = new RunEntry(3, record.Sequence + 1, f.Op(), Prompt, At, new RunEvent.OwnershipFenced(claims));
            Assert.Equal(new RunRejection(RunProblem.InvalidClaim, record.Sequence + 1),
                Assert.IsType<RunRead.Rejected>(RunReducer.Apply(W, Run, record, entry)).Reason);
        }
        Assert.IsType<RunDecision.Recorded>(f.Store.Abandon(f.Permit, f.Op(), f.Op(), "Abandoned."));
        f.ReleaseControl();
        var owned = Assert.IsType<ControlTake.Owned>(f.Store.TakeControl(W, Run));
        using (owned.Permit) Assert.Equal(new[] { key }, owned.Fenced);
        var fenced = f.Read();
        Assert.Equal(RunPhase.Abandoned, fenced.Phase);
        var repeated = new RunEntry(3, fenced.Sequence + 1, f.Op(), Prompt, At, new RunEvent.OwnershipFenced([key]));
        Assert.Equal(new RunRejection(RunProblem.InvalidClaim, fenced.Sequence + 1),
            Assert.IsType<RunRead.Rejected>(RunReducer.Apply(W, Run, fenced, repeated)).Reason);
    }

    private static Process Holder(RunFixtures f, string release)
    {
        var fakes = new FakeClients(Path.Combine(f.Project, "fakes"));
        var shim = fakes.Install("holder", FakeRule.On()
            .LockFile(Path.Combine(Path.GetDirectoryName(f.Journal(W, Run))!, "control.lock"))
            .Print("locked").WaitForFile(release));
        return Process.Start(new ProcessStartInfo(shim)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        })!;
    }
}
