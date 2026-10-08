using System.Diagnostics;
using System.Text;
using IDevelop.Core.Tests.Runs;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Runs;

public sealed class CoordinatorProcessTests
{
    private static string G(Guid id) => id.ToString("D");

    private static string Soon(int milliseconds = 1500) => DateTime.UtcNow.AddMilliseconds(milliseconds).Ticks.ToString();

    private static string Shift(string ticks, int milliseconds) => (long.Parse(ticks) + TimeSpan.FromMilliseconds(milliseconds).Ticks).ToString();

    private static string A1Key => "Owned:" + G(A1.Value) + "/1";

    [Fact]
    public async System.Threading.Tasks.Task A_reserve_process_retains_authority_and_reuses_the_initial_slot()
    {
        using var f = new RunFixtures();
        f.Approve();
        var release = Path.Combine(f.Project, "release");
        using var reserver = new Racer("reserve", f.Project, G(W.Value), G(Run.Value), G(T.Value), release);
        Assert.Equal("Created@3", await reserver.Line());
        Assert.IsType<ControlTake.Busy>(f.Store.TakeControl(W, Run));
        Assert.Null(StandaloneLease.TryTake(f.Project, T));
        Assert.Equal(3, f.Read().Sequence);
        var bytes = File.ReadAllBytes(f.Journal(W, Run));
        File.WriteAllText(release, "");
        await reserver.Exit();
        using var repeat = new Racer("reserve", f.Project, G(W.Value), G(Run.Value), G(T.Value), "-");
        Assert.Equal("Existing@3", await repeat.Line());
        await repeat.Exit();
        Assert.Equal(bytes, File.ReadAllBytes(f.Journal(W, Run)));
        Assert.True(f.Lease(T).Held);
        Assert.Equal(4, Assert.IsType<RunDecision.Recorded>(f.Store.Stop(f.Permit, f.Op())).Record.Sequence);
    }

    public sealed class PermitRace
    {
        [Fact]
        public async System.Threading.Tasks.Task Two_processes_race_for_one_permit_and_exactly_one_owns_it_each_round()
        {
            using var f = new RunFixtures();
            f.Approve();
            for (var round = 0; round < 10; round++)
            {
                var release = Path.Combine(f.Project, $"release-{round}");
                var start = Soon();
                using var a = new Racer("control", f.Project, G(W.Value), G(Run.Value), start, release);
                using var b = new Racer("control", f.Project, G(W.Value), G(Run.Value), start, release);
                string[] outcomes = [await a.Line(), await b.Line()];
                File.WriteAllText(release, "");
                await a.Exit();
                await b.Exit();
                Assert.Equal(new[] { "Busy", "Owned:" }, outcomes.Order());
            }
            Assert.Equal(1, f.Read().Sequence);
        }
    }

    public sealed class TakeoverRace
    {
        [Fact]
        public async System.Threading.Tasks.Task Three_processes_race_to_take_over_a_killed_owner_and_the_claim_is_fenced_once()
        {
            for (var round = 0; round < 4; round++)
            {
                using var f = new RunFixtures();
                f.Approve();
                f.Prepare(f.Reserve());
                f.ReleaseControl();
                using (var owner = new Racer("own", f.Project, G(W.Value), G(Run.Value), G(T.Value), G(A1.Value), "hold", "-",
                    Path.Combine(f.Project, "never")))
                {
                    Assert.Equal("Owned:", await owner.Line());
                    Assert.Equal("claim:Granted@7", await owner.Line());
                    Assert.Equal("ready:True,-,True", await owner.Line());
                    owner.Kill();
                }
                var before = f.Read().Sequence;
                var release = Path.Combine(f.Project, "release");
                var start = Soon();
                using var a = new Racer("control", f.Project, G(W.Value), G(Run.Value), start, release);
                using var b = new Racer("control", f.Project, G(W.Value), G(Run.Value), start, release);
                using var c = new Racer("control", f.Project, G(W.Value), G(Run.Value), start, release);
                string[] outcomes = [await a.Line(), await b.Line(), await c.Line()];
                var record = f.Read();
                File.WriteAllText(release, "");
                await a.Exit();
                await b.Exit();
                await c.Exit();
                Assert.Equal(new[] { "Busy", "Busy", A1Key }, outcomes.Order());
                Assert.Equal(before + 1, record.Sequence);
                Assert.Equal(new[] { new LaunchKey(A1, 1) },
                    Assert.IsType<RunEvent.OwnershipFenced>(record.Receipts.Values.Single(entry => entry.Sequence == before + 1).Event).Claims);
                var again = Assert.IsType<ControlTake.Owned>(f.NewStore().TakeControl(W, Run));
                using (again.Permit) Assert.Equal(new[] { new LaunchKey(A1, 1) }, again.Fenced);
                Assert.Equal(before + 1, f.Read().Sequence);
            }
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task A_live_owner_is_never_fenced_and_the_same_claim_is_fenced_after_it_is_killed()
    {
        using var f = new RunFixtures();
        f.Approve();
        f.Prepare(f.Reserve());
        f.ReleaseControl();
        using var owner = new Racer("own", f.Project, G(W.Value), G(Run.Value), G(T.Value), G(A1.Value), "hold", "-",
            Path.Combine(f.Project, "never"));
        Assert.Equal("Owned:", await owner.Line());
        Assert.Equal("claim:Granted@7", await owner.Line());
        Assert.Equal("ready:True,-,True", await owner.Line());
        var before = f.Read().Sequence;
        for (var i = 0; i < 3; i++) Assert.IsType<ControlTake.Busy>(f.NewStore().TakeControl(W, Run));
        Assert.Null(StandaloneLease.TryTake(f.Project, T));
        Assert.Equal(before, f.Read().Sequence);
        Assert.Empty(f.Read().Fenced);
        Assert.Equal(new[] { new LaunchKey(A1, 1) }, f.Read().UnresolvedClaims);

        owner.Kill();
        var taken = Assert.IsType<ControlTake.Owned>(f.NewStore().TakeControl(W, Run));
        using (taken.Permit)
        {
            Assert.Equal(new[] { new LaunchKey(A1, 1) }, taken.Fenced);
            Assert.Equal(before + 1, f.Read().Sequence);
            using var lease = Assert.IsType<LeaseTake.Taken>(taken.Permit.TakeTask(T)).Lease;
            Assert.IsType<RunDecision.Recorded>(f.Store.Recover(lease, f.Op(), A1, RecoveryOutcome.Stopped, f.Op(), "Stopped."));
        }
        Assert.Empty(f.Read().UnresolvedClaims);
    }

    [Fact]
    public async System.Threading.Tasks.Task An_owner_that_drops_its_permit_but_keeps_its_lease_is_fenced_while_still_alive()
    {
        using var f = new RunFixtures();
        f.Approve();
        f.Prepare(f.Reserve());
        f.ReleaseControl();
        using var owner = new Racer("own", f.Project, G(W.Value), G(Run.Value), G(T.Value), G(A1.Value), "drop-permit", "-",
            Path.Combine(f.Project, "never"));
        Assert.Equal("Owned:", await owner.Line());
        Assert.Equal("claim:Granted@7", await owner.Line());
        Assert.Equal("ready:False,-,False", await owner.Line());
        var taken = Assert.IsType<ControlTake.Owned>(f.NewStore().TakeControl(W, Run));
        using (taken.Permit)
        {
            Assert.Equal(new[] { new LaunchKey(A1, 1) }, taken.Fenced);
            Assert.IsType<LeaseTake.Busy>(taken.Permit.TakeTask(T));
        }
        owner.Kill();
    }

    [Theory]
    [InlineData("transfer0", "ready:True,-,True", false)]
    [InlineData("transfer1", "ready:False,True,True", false)]
    [InlineData("transfer2", "ready:False,True,True", false)]
    [InlineData("transfer3", "ready:False,False,True", true)]
    public async System.Threading.Tasks.Task A_lease_transfer_killed_at_each_step_frees_the_task_and_fences_the_claim(string step, string ready, bool free)
    {
        using var f = new RunFixtures();
        f.Approve();
        f.Prepare(f.Reserve());
        f.ReleaseControl();
        using var owner = new Racer("own", f.Project, G(W.Value), G(Run.Value), G(T.Value), G(A1.Value), step, "-",
            Path.Combine(f.Project, "never"));
        Assert.Equal("Owned:", await owner.Line());
        Assert.Equal("claim:Granted@7", await owner.Line());
        Assert.Equal(ready, await owner.Line());
        using (var probe = StandaloneLease.TryTake(f.Project, T)) Assert.Equal(free, probe is not null);
        Assert.IsType<ControlTake.Busy>(f.NewStore().TakeControl(W, Run));
        owner.Kill();

        var taken = Assert.IsType<ControlTake.Owned>(f.NewStore().TakeControl(W, Run));
        using (taken.Permit)
        {
            Assert.Equal(new[] { new LaunchKey(A1, 1) }, taken.Fenced);
            using var lease = Assert.IsType<LeaseTake.Taken>(taken.Permit.TakeTask(T)).Lease;
            Assert.IsType<RunDecision.Recorded>(f.Store.Recover(lease, f.Op(), A1, RecoveryOutcome.Stopped, f.Op(), "Stopped."));
        }
    }

    public sealed class LeaseRace
    {
        [Theory]
        [InlineData("lease", "lease")]
        [InlineData("lease", "permit-lease")]
        public async System.Threading.Tasks.Task Two_processes_race_for_one_task_lease_and_exactly_one_takes_it(string first, string second)
        {
            using var f = new RunFixtures();
            f.Approve();
            for (var round = 0; round < 10; round++)
            {
                var release = Path.Combine(f.Project, $"release-{round}");
                var start = Soon();
                using var a = new Racer(first, f.Project, G(W.Value), G(Run.Value), G(T.Value), start, release);
                using var b = new Racer(second, f.Project, G(W.Value), G(Run.Value), G(T.Value), start, release);
                string[] outcomes = [await a.Line(), await b.Line()];
                File.WriteAllText(release, "");
                await a.Exit();
                await b.Exit();
                Assert.Equal(new[] { "Busy", "Taken" }, outcomes.Order());
            }
            Assert.Equal(1, f.Read().Sequence);
        }
    }

    public sealed class CloseTurnRace
    {
        [Fact]
        public async System.Threading.Tasks.Task Close_turn_and_takeover_race_for_control_and_fence_once()
        {
            for (var round = 0; round < 8; round++)
            {
                using var f = new RunFixtures();
                f.Approve();
                var reserved = f.Reserve();
                f.Claim(reserved);
                var checkpoint = f.WriteLog(reserved);
                f.ReleaseControl();
                var release = Path.Combine(f.Project, "release");
                var start = Soon();
                using var closer = new Racer("closeturn", f.Project, G(W.Value), G(Run.Value), G(A1.Value), "1",
                    checkpoint.ByteLength.ToString(), checkpoint.Content.Sha256, start, release);
                using var control = new Racer("control", f.Project, G(W.Value), G(Run.Value), start, release);
                var closed = await closer.Line();
                var owned = await control.Line();
                Assert.Equal(new[] { "Busy", A1Key }, new[] { closed.Split('|')[0], owned }.Order());
                if (closed != "Busy") Assert.Equal(A1Key + "|Rejected:UnresolvedOwnership", closed);
                var record = f.Read();
                Assert.Equal(8, record.Sequence);
                Assert.Equal(new[] { new LaunchKey(A1, 1) }, record.Fenced);
                Assert.Empty(record.TurnClosures);
                Assert.Equal(new[] { new LaunchKey(A1, 1) }, record.UnresolvedClaims);
                var bytes = File.ReadAllBytes(f.Journal(W, Run));
                var decoded = RunJournal.Decode(bytes);
                Assert.Null(decoded.Rejection);
                Assert.Equal(bytes, Encoding.UTF8.GetBytes(string.Concat(decoded.Entries.Select(RunJournal.Encode))));
                File.WriteAllText(release, "");
                await closer.Exit();
                await control.Exit();
                Assert.IsType<RunDecision.Recorded>(f.Store.Recover(f.Lease(T), f.Op(), A1,
                    RecoveryOutcome.Stopped, f.Op(), "Stopped."));
                Assert.Equal(9, f.Read().Sequence);
                Assert.True(f.Read().Closures.ContainsKey(A1));
            }
        }
    }

    public sealed class StopperRace
    {
        [Fact]
        public async System.Threading.Tasks.Task A_stopper_racing_the_owners_first_claim_is_busy_every_round()
        {
            for (var round = 0; round < 8; round++)
            {
                using var f = new RunFixtures();
                f.Approve();
                f.Prepare(f.Reserve());
                f.ReleaseControl();
                var start = Soon(2500);
                var release = Path.Combine(f.Project, "release");
                var go = Path.Combine(f.Project, "go");
                using var stopper = new Racer("stale-stop", f.Project, G(W.Value), G(Run.Value), Shift(start, round * 15), go);
                Assert.Equal("ready", await stopper.Line());
                using var owner = new Racer("own", f.Project, G(W.Value), G(Run.Value), G(T.Value), G(A1.Value), "hold", start, release);
                Assert.Equal("Owned:", await owner.Line());
                File.WriteAllText(go, "");
                Assert.Equal("claim:Granted@7", await owner.Line());
                var bytes = File.ReadAllBytes(f.Journal(W, Run));
                Assert.Equal("Busy|Rejected:TaskBusy", await stopper.Line());
                Assert.Equal(bytes, File.ReadAllBytes(f.Journal(W, Run)));
                Assert.Equal("ready:True,-,True", await owner.Line());
                Assert.Equal(RunPhase.Approved, f.Read().Phase);
                Assert.Equal(7, f.Read().Sequence);
                File.WriteAllText(release, "");
                await owner.Exit();
                await stopper.Exit();
                Assert.IsType<RunDecision.Recorded>(f.Store.Stop(f.Permit, f.Op()));
                Assert.Equal(RunPhase.StopRequested, f.Read().Phase);
            }
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task A_second_window_cannot_reserve_or_stop_until_the_owner_exits()
    {
        using var f = new RunFixtures(FixtureWorkflow(Task(T), Task(U)));
        f.Approve();
        var go = Path.Combine(f.Project, "go");
        using var stopper = new Racer("stale-stop", f.Project, G(W.Value), G(Run.Value), "-", go);
        Assert.Equal("ready", await stopper.Line());
        using var reserver = new Racer("stale-reserve", f.Project, G(W.Value), G(Run.Value), G(U.Value), "-", go);
        Assert.Equal("ready", await reserver.Line());
        using var owner = new Racer("own", f.Project, G(W.Value), G(Run.Value), G(T.Value), "-", "hold", "-",
            Path.Combine(f.Project, "never"));
        Assert.Equal("Owned:", await owner.Line());
        Assert.Equal("ready:True,-,True", await owner.Line());
        Assert.IsType<ControlTake.Busy>(f.Store.TakeControl(W, Run));
        var bytes = File.ReadAllBytes(f.Journal(W, Run));
        File.WriteAllText(go, "");
        Assert.Equal("Busy|Rejected:TaskBusy", await stopper.Line());
        Assert.Equal("Busy|Rejected:TaskBusy", await reserver.Line());
        await stopper.Exit();
        await reserver.Exit();
        Assert.Equal(bytes, File.ReadAllBytes(f.Journal(W, Run)));
        Assert.Equal(1, f.Read().Sequence);
        Assert.Equal(RunPhase.Approved, f.Read().Phase);
        owner.Kill();
        var stopped = Assert.IsType<RunDecision.Recorded>(f.Store.Stop(f.Permit, f.Op()));
        Assert.Equal(2, stopped.Record.Sequence);
        Assert.Equal(RunPhase.StopRequested, stopped.Record.Phase);
    }

    [Fact]
    public void A_fenced_claim_requires_recovery_before_another_turn()
    {
        using var f = new RunFixtures(FixtureWorkflow(Task(T) with { Conversation = ConversationMode.Chat }));
        f.Approve();
        var reserved = f.Reserve();
        f.Claim(reserved);
        var checkpoint = f.WriteLog(reserved, report: "More?", conversation: ConversationMode.Chat);
        f.ReleaseControl();
        var permit = f.Permit;
        Assert.Equal(new[] { new LaunchKey(A1, 1) }, f.Read().Fenced);
        var bytes = File.ReadAllBytes(f.Journal(W, Run));
        Assert.Equal(RunProblem.UnresolvedOwnership, Problem(f.NewStore().CloseTurn(permit, f.Op(), new(A1, 1), checkpoint)));
        Assert.Equal(RunProblem.InvalidClaim, Problem(f.Store.Claim(f.Lease(T), f.Op(), new(A1, 2),
            reserved.Inputs, Revision.Hash("Continue."))));
        Assert.Equal(bytes, File.ReadAllBytes(f.Journal(W, Run)));
        var entry = new RunEntry(3, f.Read().Sequence + 1, f.Op(), Prompt, At, new RunEvent.TurnClosed(new(A1, 1), checkpoint));
        Assert.Equal(RunProblem.InvalidClaim, Assert.IsType<RunRead.Rejected>(RunReducer.Apply(W, Run, f.Read(), entry)).Reason.Problem);
        Assert.IsType<RunDecision.Recorded>(f.Store.Recover(f.Lease(T), f.Op(), A1,
            RecoveryOutcome.Stopped, f.Op(), "Stopped."));
        Assert.True(f.Read().Closures.ContainsKey(A1));
        Assert.Empty(f.Read().UnresolvedClaims);
    }

    [Fact]
    public async System.Threading.Tasks.Task Claim_and_recovery_recheck_the_permit_after_waiting_for_the_journal_lock()
    {
        using var f = new RunFixtures();
        f.Approve();
        var reserved = f.Reserve();
        f.Prepare(reserved);
        var lease = f.Lease(T);
        var writeLock = Path.Combine(f.Project, ".idp", "runs", "write.lock");
        var claimClock = new StoppedClock();
        System.Threading.Tasks.Task<RunDecision> claim;
        using (new FileStream(writeLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            claim = System.Threading.Tasks.Task.Run(() => f.NewStore(claimClock).Claim(lease, f.Op(), new(A1, 1), reserved.Inputs, Prompt));
            await System.Threading.Tasks.Task.WhenAny(claimClock.Waiting.Task, claim);
            Assert.False(claim.IsCompleted);
            f.Permit.Dispose();
        }
        Assert.Equal(RunProblem.TaskBusy, Problem(await claim));
        Assert.Empty(f.Read().Claims);

        using var g = new RunFixtures();
        g.Approve();
        var other = g.Reserve();
        g.Claim(other);
        var held = g.Lease(T);
        var recoverClock = new StoppedClock();
        System.Threading.Tasks.Task<RunDecision> recover;
        using (new FileStream(Path.Combine(g.Project, ".idp", "runs", "write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            recover = System.Threading.Tasks.Task.Run(() => g.NewStore(recoverClock).Recover(held, g.Op(), A1, RecoveryOutcome.Stopped, g.Op(), "Stopped."));
            await System.Threading.Tasks.Task.WhenAny(recoverClock.Waiting.Task, recover);
            Assert.False(recover.IsCompleted);
            g.Permit.Dispose();
        }
        Assert.Equal(RunProblem.TaskBusy, Problem(await recover));
        Assert.False(g.Read().Closures.ContainsKey(A1));
        f.ReleaseControl();
        g.ReleaseControl();
        Assert.IsType<RunDecision.Granted>(f.Store.Claim(f.Lease(T), f.Op(), new(A1, 1), reserved.Inputs, Prompt));
        Assert.Equal(7, f.Read().Sequence);
        Assert.IsType<RunDecision.Recorded>(g.Store.Recover(g.Lease(T), g.Op(), A1,
            RecoveryOutcome.Stopped, g.Op(), "Stopped."));
        Assert.Equal(9, g.Read().Sequence);
    }

    /// <summary>
    /// Time never advances, so the journal lock's patience never runs out. The store reads the clock once when it starts
    /// waiting and again after each failed attempt, so a second read means it is blocked on the lock.
    /// </summary>
    private sealed class StoppedClock : TimeProvider
    {
        private int _reads;

        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override DateTimeOffset GetUtcNow() => At;

        public override long GetTimestamp()
        {
            if (Interlocked.Increment(ref _reads) > 1) Waiting.TrySetResult();
            return 0;
        }
    }

    [Theory]
    [InlineData("e1-run/events.jsonl", 1)]
    [InlineData("e2-run/events.jsonl", 2)]
    [InlineData("e2-run/events.jsonl", 3)]
    public void An_ownership_fence_cannot_enter_a_schema_one_or_two_journal(string fixture, int schema)
    {
        using var f = new RunFixtures();
        var bytes = File.ReadAllBytes(Fixture.Path(fixture));
        File.WriteAllBytes(f.Journal(W, Run), bytes);
        var record = f.Read();
        var key = record.Claims.Keys.FirstOrDefault(new LaunchKey(A1, 1));
        var fence = new RunEntry(schema, record.Sequence + 1, f.Op(), Prompt, At, new RunEvent.OwnershipFenced([key]));
        File.WriteAllBytes(f.Journal(W, Run), [.. bytes, .. Encoding.UTF8.GetBytes(RunJournal.Encode(fence))]);
        Assert.Equal(new RunRejection(RunProblem.UnsupportedSchema, record.Sequence + 1),
            Assert.IsType<RunRead.Rejected>(f.Store.Read(W, Run)).Reason);
    }

    [Fact]
    public void A_schema_three_journal_with_fences_and_stale_claims_replays_byte_for_byte_and_rejects_older_entries()
    {
        var workflow = Connect(FixtureWorkflow(Task(T), Task(U), Task(C)), T, U);
        using var f = new RunFixtures(workflow);
        f.Approve();
        var producer = f.Reserve(T);
        var first = f.Complete(producer, "A ready.\n");
        var consumer = f.Reserve(U);
        f.Claim(consumer);
        var independent = f.Reserve(C);
        f.Claim(independent);
        var retry = f.Reserve(T, new AttemptCause.Retry(producer.Attempt.Id, f.Op()));
        f.Complete(retry, "A2\n", first.Id);
        f.ReleaseControl();
        _ = f.Permit;
        Assert.Equal(new[] { new LaunchKey(consumer.Attempt.Id, 1), new LaunchKey(independent.Attempt.Id, 1) }.OrderBy(key => key.Attempt.Value),
            f.Read().Fenced.OrderBy(key => key.Attempt.Value));
        Assert.IsType<RunDecision.Recorded>(f.Store.Recover(f.Lease(C), f.Op(), independent.Attempt.Id, RecoveryOutcome.Stopped, f.Op(), "Stopped."));
        var live = f.Read();

        var bytes = File.ReadAllBytes(f.Journal(W, Run));
        var decoded = RunJournal.Decode(bytes);
        Assert.Null(decoded.Rejection);
        Assert.Equal(bytes, Encoding.UTF8.GetBytes(string.Concat(decoded.Entries.Select(RunJournal.Encode))));
        Assert.All(decoded.Entries, entry => Assert.Equal(3, entry.Schema));
        var replayed = Assert.IsType<RunRead.Loaded>(RunReducer.Replay(W, Run, decoded.Entries)).Record;
        Assert.Equal((live.Sequence, live.Claims.Count, live.Results.Count, live.Closures.Count),
            (replayed.Sequence, replayed.Claims.Count, replayed.Results.Count, replayed.Closures.Count));
        Assert.Equal(live.Fenced.OrderBy(key => key.Attempt.Value), replayed.Fenced.OrderBy(key => key.Attempt.Value));
        Assert.Equal(new[] { new LaunchKey(consumer.Attempt.Id, 1) }, replayed.UnresolvedClaims);
        Assert.Contains(first.Id, replayed.StaleResults);
        if (Environment.GetEnvironmentVariable("E3A1_SCHEMA3_OUT") is { Length: > 0 } output) File.WriteAllBytes(output, bytes);

        var older = new RunEntry(2, live.Sequence + 1, f.Op(), Prompt, At, new RunEvent.StopRequested());
        File.WriteAllBytes(f.Journal(W, Run), [.. bytes, .. Encoding.UTF8.GetBytes(RunJournal.Encode(older))]);
        Assert.Equal(new RunRejection(RunProblem.UnsupportedSchema, live.Sequence + 1),
            Assert.IsType<RunRead.Rejected>(f.Store.Read(W, Run)).Reason);
    }

}
