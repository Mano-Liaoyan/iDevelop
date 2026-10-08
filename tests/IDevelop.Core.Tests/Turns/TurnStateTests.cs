using System.Diagnostics;
using IDevelop.Core.Tests.Materialization;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;
using static IDevelop.Core.Tests.Turns.TurnFixture;

namespace IDevelop.Core.Tests.Turns;

[Collection(EnvironmentCollection.Name)]
public sealed class TurnStateTests
{
    [Theory]
    [InlineData("unpublished")]
    [InlineData("interrupted")]
    [InlineData("uncertain")]
    public async Task Closing_the_project_keeps_each_lease_until_its_receipt(string row)
    {
        await using var f = new TurnFixture();
        await f.Open(row == "unpublished" ? null : f.Waiting(hang: row == "uncertain"));
        var permit = f.Preparation.Permit;
        if (row == "unpublished")
        {
            var turn = await f.Settled(await f.Start());
            await f.Runs.DisposeAsync().AsTask().WaitAsync(Bound);
            var take = permit.TakeTask(T);
            using var unexpected = (take as LeaseTake.Taken)?.Lease;
            Assert.IsType<LeaseTake.Busy>(take);
            Assert.Equal("NotSettled", Assert.IsType<Release.Held>(turn.Release()).Reason.Problem.ToString());
            Assert.IsType<RunDecision.Recorded>(f.Preparation.Store.CloseAttempt(permit, f.Preparation.Op(),
                turn.Address.Launch.Attempt, TerminalAttemptOutcome.Succeeded, turn.Log));
            var result = Assert.IsType<Publication.Accepted>(f.Preparation.Materializer()
                .Publish(turn.Lease, f.Preparation.Op(), turn.Address.Launch.Attempt)).Result;
            Assert.Equal(result.Id, Assert.IsType<TurnDisposition.Published>(Assert.IsType<Release.Released>(turn.Release()).Receipt).Result);
            Assert.IsType<LeaseTake.Taken>(permit.TakeTask(T)).Lease.Dispose();
            Assert.Equal(1, f.Launches);
            return;
        }

        if (row == "uncertain") f.Runs.StopSeam = _ => false;
        var running = await f.Start();
        var launched = Assert.Single(f.Log(running.Address.Launch).Events.OfType<AttemptEvent.Launched>());
        using var process = Process.GetProcessById(launched.ProcessId);
        try
        {
            await WaitUntilAsync(() => f.Log(running.Address.Launch).Record?.SessionId == "session-1");
            await f.Runs.DisposeAsync().AsTask().WaitAsync(Bound);
            if (row == "interrupted")
            {
                var turn = await f.Settled(running);
                Assert.Equal("Interrupted", turn.Attempt.Status.ToString());
                var take = permit.TakeTask(T);
                using var unexpected = (take as LeaseTake.Taken)?.Lease;
                Assert.IsType<LeaseTake.Busy>(take);
                Assert.Equal("NotSettled", Assert.IsType<Release.Held>(turn.Release()).Reason.Problem.ToString());
                Assert.IsType<RunDecision.Recorded>(f.Preparation.Store.CloseAttempt(permit, f.Preparation.Op(),
                    turn.Address.Launch.Attempt, TerminalAttemptOutcome.Interrupted, turn.Log));
                var ended = Assert.IsType<TurnDisposition.Ended>(Assert.IsType<Release.Released>(turn.Release()).Receipt);
                Assert.Equal("Interrupted", Assert.IsType<AttemptEnd.Logged>(ended.End).Outcome.ToString());
                Assert.IsType<LeaseTake.Taken>(permit.TakeTask(T)).Lease.Dispose();
            }
            else
            {
                var turn = Assert.IsType<TurnSettlement.Unresolved>(await running.Settlement.WaitAsync(Bound)).Turn;
                Assert.Equal("Uncertain", turn.Reason.ToString());
                var take = permit.TakeTask(T);
                using var unexpected = (take as LeaseTake.Taken)?.Lease;
                Assert.IsType<LeaseTake.Busy>(take);
                Assert.Equal("NotSettled", Assert.IsType<Release.Held>(turn.Release()).Reason.Problem.ToString());
            }
            Assert.Equal(1, f.Launches);
        }
        finally
        {
            if (row == "uncertain")
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(Bound);
            }
        }
    }

    [Theory]
    [InlineData("protocol")]
    [InlineData("pipe")]
    public async Task A_failed_protocol_or_pipe_still_stops_by_its_deadline(string row)
    {
        await using var f = row == "protocol" ? new TurnFixture()
            : new TurnFixture(client: ClientId.ClaudeCode, model: "claude-haiku-4-5");
        var rule = row == "protocol"
            ? FakeAgents.Fresh(ClientId.Codex).Print(FakeAgents.SessionLine(ClientId.Codex, "session-1"))
                .Print("""{"id":"turn-1","error":{"message":"Turn refused."}}""").Hang()
            : FakeAgents.Fresh(ClientId.ClaudeCode).Print(FakeAgents.SessionLine(ClientId.ClaudeCode, "session-1"))
                .CloseStdin().Print("""{"type":"control_request","request_id":"plan-1","request":{"subtype":"can_use_tool","tool_name":"ExitPlanMode","input":{"plan":"Change answer.txt"}}}""").Hang();
        await f.Open(rule);
        using var direct = row == "pipe" ? new FakeClients(Path.Combine(f.Evidence, "direct"), FakeClientInstallMode.Direct)
            { LaunchFolder = f.Fakes.LaunchFolder } : null;
        if (direct is not null) FakeAgents.Install(direct, ClientId.ClaudeCode, rule);
        await using var pipeRuns = direct is not null ? f.OpenRuns(await direct.DiscoverAsync()) : null;
        var runs = pipeRuns ?? f.Runs;
        runs.StopSeam = _ => false;
        var permit = f.Preparation.Permit;
        var running = Assert.IsType<TurnStart.Started>(await runs.StartTurn(permit, f.First()).WaitAsync(Bound)).Turn;
        var launched = Assert.Single(f.Log(running.Address.Launch).Events.OfType<AttemptEvent.Launched>());
        using var process = Process.GetProcessById(launched.ProcessId);
        try
        {
            var detail = row == "protocol" ? "Turn refused." : "iDevelop could not write to the client's input pipe.";
            await WaitUntilAsync(() => f.Log(running.Address.Launch).Events.OfType<AttemptEvent.Agent>()
                .Any(e => e.Event is AgentEvent.Failed failed && failed.Reason == detail));
            var turn = Assert.IsType<TurnSettlement.Unresolved>(await running.Settlement.WaitAsync(TimeSpan.FromSeconds(5))).Turn;
            Assert.Equal("Uncertain", turn.Reason.ToString());
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => running.RootExited.WaitAsync(Bound));
            Assert.Equal("The client did not exit after it was stopped.", error.Message);
            Assert.Contains(f.Log(running.Address.Launch).Events.OfType<AttemptEvent.Agent>(),
                e => e.Event is AgentEvent.Failed failed && failed.Reason == detail);
            if (row == "pipe")
            {
                Assert.Equal("ExitPlanMode", Assert.IsType<AgentEvent.PermissionRequested>(
                    Assert.Single(f.Log(running.Address.Launch).Events.OfType<AttemptEvent.Agent>(),
                        e => e.Event is AgentEvent.PermissionRequested).Event).Action.Tool);
                Assert.Equal("DeliveryUnknown", Assert.Single(f.Log(running.Address.Launch).Events
                    .OfType<AttemptEvent.RequestClosed>()).Reason.ToString());
            }
            Assert.IsType<LeaseTake.Busy>(permit.TakeTask(T));
            Assert.Empty(f.Preparation.Read().RootExits);
            Assert.Single(f.Preparation.Read().Claims);
            Assert.Equal(1, f.Launches);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(Bound);
        }
        await SuccessfulControl();
    }

    [Fact]
    public async Task Shutdown_published_before_the_turn_hears_it_never_launches()
    {
        var other = PreparationFixture.Writer(C) with { Execution = new(ClientId.Codex) { Model = "gpt-6-sol", Reasoning = "high" } };
        await using var f = new TurnFixture(configure: workflow => Edit(workflow, TestNodes.Place(other, new(0, 0))));
        await f.Open(f.Waiting());
        var clock = new HeldClock();
        f.Runs.TimeProvider = clock;
        var running = await f.Start(new TurnIntent.First(f.Preparation.Op(), C, new AttemptCause.Initial()));
        var otherFolder = f.Preparation.Store.AttemptFolder(W, f.Preparation.RunId, C, running.Address.Launch.Attempt);
        await WaitUntilAsync(() => AttemptEvidence.Read(otherFolder).Record?.SessionId == "session-1");
        using var barrier = new ProbeBarrier("runner.launch.before");
        f.Runs.Probe = barrier.Probe;
        var command = f.Runs.StartTurn(f.Preparation.Permit, f.First());
        await barrier.Reached.Task.WaitAsync(Bound);
        var disposal = System.Threading.Tasks.Task.Run(() =>
        {
            clock.HoldOn(Environment.CurrentManagedThreadId);
            return f.Runs.DisposeAsync().AsTask();
        });
        try
        {
            await clock.Held.Task.WaitAsync(Bound);
            barrier.Dispose();
            var start = Assert.IsType<TurnStart.Settled>(await command.WaitAsync(Bound));
            var turn = Assert.IsType<TurnSettlement.Settled>(start.Settlement).Turn;
            Assert.Equal("The project was closed before the client started.", Assert.IsType<RootExit.NotStarted>(turn.Exit.Exit).Detail);
            Assert.Equal("The project was closed before the client started.",
                Assert.Single(f.Log(turn.Address.Launch).Events.OfType<AttemptEvent.LaunchFailed>()).Reason);
            Assert.Equal(2, f.Preparation.Read().Claims.Count);
            Assert.Equal(1, f.Launches);
        }
        finally
        {
            barrier.Dispose();
            clock.Release();
            File.WriteAllText(Path.Combine(f.Evidence, "gate"), "");
            await disposal.WaitAsync(Bound);
        }
        Assert.Equal("Interrupted", (await f.Settled(running)).Attempt.Status.ToString());
    }

    [Fact]
    public async Task Reconcile_after_closing_does_no_work()
    {
        await using var f = new TurnFixture();
        await f.Open();
        var turn = await f.Settled(await f.Start());
        var permit = f.Preparation.Permit;
        var operation = f.Preparation.Op();
        var launch = turn.Address.Launch;
        Assert.IsType<TurnSettlement.Settled>(Assert.IsType<Reconciliation.Found>(await f.Runs.Reconcile(permit, operation, launch).WaitAsync(Bound)).Settlement);
        await f.Runs.DisposeAsync().AsTask().WaitAsync(Bound);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => f.Runs.Reconcile(permit, operation, launch));
        Assert.Equal(1, f.Launches);
    }

    [Fact]
    public async Task A_torn_request_write_is_written_again()
    {
        await using var f = new TurnFixture();
        await f.Open();
        var permit = f.Preparation.Permit;
        var intent = f.First();
        TornStream? torn = null;
        f.Runs.RequestStream = inner => torn = new TornStream(inner);
        var refused = Assert.IsType<TurnStart.Refused>(await f.Runs.StartTurn(permit, intent).WaitAsync(Bound));
        Assert.Equal("StorageUnavailable", refused.Reason.Problem.ToString());
        Assert.NotNull(torn);
        Assert.Equal(24, torn.Written);
        Assert.Empty(f.Preparation.Read().Claims);
        Assert.Equal(0, f.Launches);
        Assert.IsType<LeaseTake.Taken>(permit.TakeTask(T)).Lease.Dispose();
        f.Runs.RequestStream = null;
        var turn = await f.Settled(await f.Start(intent));
        Assert.IsType<CaptureDisposition.Matched>(turn.Capture.Disposition);
        Assert.Single(f.Log(turn.Address.Launch).Events.OfType<AttemptEvent.Requested>());
        Assert.Equal(1, f.Launches);
        Assert.Single(f.Preparation.Read().Claims);
    }

    [Fact]
    public async Task Closing_and_reading_stay_prompt_while_the_root_observation_waits()
    {
        await using var f = new TurnFixture();
        await f.Open();
        f.Runs.ShutdownTime = TimeSpan.FromSeconds(5);
        FileStream? held = null;
        var retry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Runs.Probe = point =>
        {
            if (point == "journal.root-exit.before" && held is null) held = f.LockJournal();
            if (point == "journal.root-exit.retry") retry.TrySetResult();
        };
        try
        {
            var permit = f.Preparation.Permit;
            var intent = f.First();
            var running = await f.Start(intent);
            var launch = running.Address.Launch;
            await retry.Task.WaitAsync(Bound);
            var prompt = TimeSpan.FromSeconds(1);
            var clock = Stopwatch.StartNew();
            var existing = Assert.IsType<TurnStart.Existing>(await f.Runs.StartTurn(permit, intent).WaitAsync(Bound));
            var duplicateTime = clock.Elapsed;
            Assert.Equal(launch, existing.Launch);
            Assert.Same(running, existing.Running);
            Assert.True(duplicateTime < prompt, $"Duplicate start took {duplicateTime}.");

            clock.Restart();
            var reconciliation = f.Runs.Reconcile(permit, f.Preparation.Op(), launch);
            var reconcileTime = clock.Elapsed;
            Assert.True(reconcileTime < prompt, $"Reconcile took {reconcileTime} to return its task.");
            Assert.False(reconciliation.IsCompleted);

            var disposeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var disposal = System.Threading.Tasks.Task.Run(() =>
            {
                var synchronous = Stopwatch.StartNew();
                disposeStarted.TrySetResult();
                var leaving = f.Runs.DisposeAsync();
                return (synchronous.Elapsed, leaving);
            });
            await disposeStarted.Task.WaitAsync(Bound);
            var active = System.Threading.Tasks.Task.Run(() =>
            {
                var getter = Stopwatch.StartNew();
                var value = f.Runs.Active;
                return (getter.Elapsed, value);
            });
            var (disposeTime, leaving) = await disposal.WaitAsync(Bound);
            var (activeTime, _) = await active.WaitAsync(Bound);
            Assert.True(disposeTime < prompt, $"DisposeAsync took {disposeTime} to return its ValueTask.");
            Assert.True(activeTime < prompt, $"Active took {activeTime}.");
            held!.Dispose();
            await leaving.AsTask().WaitAsync(Bound);
            Assert.IsType<TurnSettlement.Settled>(Assert.IsType<Reconciliation.Found>(await reconciliation.WaitAsync(Bound)).Settlement);
            var turn = await f.Settled(running);
            Assert.Equal(0, Assert.IsType<RootExit.Exited>(turn.Exit.Exit).Code);
            Assert.Single(f.Preparation.Read().RootExits);
            Assert.Equal(1, f.Launches);
        }
        finally { held?.Dispose(); }
    }

    [Fact]
    public async Task A_released_turn_leaves_this_window()
    {
        await using var f = new TurnFixture();
        await f.Open();
        var turn = await f.Settled(await f.Start());
        var result = f.Publish(turn);
        Assert.Equal(result.Id, Assert.IsType<TurnDisposition.Published>(Assert.IsType<Release.Released>(turn.Release()).Receipt).Result);
        var reconciled = Assert.IsType<TurnSettlement.Settled>(Assert.IsType<Reconciliation.Found>(await f.Runs.Reconcile(f.Preparation.Permit,
            f.Preparation.Op(), turn.Address.Launch).WaitAsync(Bound)).Settlement).Turn;
        Assert.NotSame(turn, reconciled);
        Assert.True(reconciled.Lease.Held);
        Assert.Equal(result.Id, Assert.IsType<TurnDisposition.Published>(Assert.IsType<Release.Released>(reconciled.Release()).Receipt).Result);
        Assert.IsType<LeaseTake.Taken>(f.Preparation.Permit.TakeTask(T)).Lease.Dispose();
        Assert.Equal(1, f.Launches);
    }

    [Fact]
    public async Task A_bug_in_root_observation_faults_the_turn()
    {
        await using var f = new TurnFixture();
        await f.Open();
        var observations = 0;
        f.Runs.Probe = point =>
        {
            if (point == "journal.root-exit.before" && Interlocked.Increment(ref observations) == 1)
                throw new InvalidOperationException("Observation bug.");
        };
        var running = await f.Start();
        var settlement = await Assert.ThrowsAsync<InvalidOperationException>(() => running.Settlement.WaitAsync(Bound));
        Assert.Equal("Observation bug.", settlement.Message);
        var root = await Assert.ThrowsAsync<InvalidOperationException>(() => running.RootExited.WaitAsync(Bound));
        Assert.Equal("Observation bug.", root.Message);
        Assert.IsType<LeaseTake.Busy>(f.Preparation.Permit.TakeTask(T));
        Assert.Empty(f.Preparation.Read().RootExits);
        Assert.Single(f.Preparation.Read().Claims);
        f.Runs.Probe = null;
        var reconciled = Assert.IsType<TurnSettlement.Unresolved>(Assert.IsType<Reconciliation.Found>(await f.Runs.Reconcile(f.Preparation.Permit,
            f.Preparation.Op(), running.Address.Launch).WaitAsync(Bound)).Settlement).Turn;
        Assert.Equal("Uncertain", reconciled.Reason.ToString());
        Assert.Equal(1, f.Launches);
        await SuccessfulControl();
    }

    [Fact]
    public async Task A_journal_that_stays_busy_fences_the_launch()
    {
        await using var f = new TurnFixture();
        await f.Open();
        FileStream? held = null;
        f.Runs.Probe = point =>
        {
            if (point == "journal.root-exit.before" && held is null) held = f.LockJournal();
            if (point == "journal.root-exit-fenced.before") held!.Dispose();
        };
        try
        {
            var running = await f.Start();
            var turn = Assert.IsType<TurnSettlement.Unresolved>(await running.Settlement.WaitAsync(Bound)).Turn;
            Assert.Equal("OwnershipConflict", turn.Reason.ToString());
            await running.RootExited.WaitAsync(Bound);
            Assert.True(running.RootExited.IsCompletedSuccessfully);
            Assert.Contains(running.Address.Launch, f.Preparation.Read().Fenced);
            Assert.Empty(f.Preparation.Read().RootExits);
            Assert.Single(f.Preparation.Read().Claims);
            Assert.Equal(1, f.Launches);
        }
        finally { held?.Dispose(); }
        await SuccessfulControl();
    }

    private sealed class HeldClock : TimeProvider
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _thread;
        public readonly TaskCompletionSource Held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void HoldOn(int thread) => Volatile.Write(ref _thread, thread);
        public void Release() => _released.TrySetResult();
        public override DateTimeOffset GetUtcNow()
        {
            if (Volatile.Read(ref _thread) == Environment.CurrentManagedThreadId && Held.TrySetResult())
                Assert.True(_released.Task.Wait(Bound), "The held clock was not released.");
            return TimeProvider.System.GetUtcNow();
        }
    }

    private sealed class TornStream(Stream inner) : Stream
    {
        public int Written { get; private set; }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            var count = Math.Min(buffer.Length, 24 - Written);
            inner.Write(buffer[..count]);
            Written += count;
            if (buffer.Length <= count) return;
            inner.Flush();
            throw new IOException("No space left on device.");
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
