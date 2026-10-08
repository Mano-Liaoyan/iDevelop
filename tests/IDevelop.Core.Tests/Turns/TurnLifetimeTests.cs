using System.Diagnostics;
using IDevelop.Core.Tests.Materialization;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;
using static IDevelop.Core.Tests.Turns.TurnFixture;
using static IDevelop.TestSupport.Processes;

namespace IDevelop.Core.Tests.Turns;

/// <summary>Process lifetime at the turn runner: what workflow cleanup stops, what it cannot, and what it never signals.</summary>
public sealed class TurnLifetimeTests
{
    [Fact]
    public async Task Workflow_cleanup_stops_a_descendant_that_a_standalone_run_leaves_running()
    {
        using var children = new Processes();
        await using (var f = new TurnFixture())
        {
            var (pidFile, ping, pong) = Probe(f.Evidence);
            await f.Open(FakeAgents.Fresh(ClientId.Codex).SpawnResponder(pidFile, ping, pong)
                .Print(FakeAgents.SessionLine(ClientId.Codex, "session-1"))
                .Write("result.txt", "done\n").Print(FakeAgents.ReplyLines(ClientId.Codex, "Done.")));
            var turn = await f.Settled(await f.Start());
            var responder = await children.PidAsync(pidFile).WaitAsync(Bound);

            Assert.Equal(CleanupResult.Completed, turn.Cleanup!.Result);
            AssertGone(responder);
            File.WriteAllText(ping, "ping");
            await System.Threading.Tasks.Task.Delay(300);
            Assert.False(File.Exists(pong));
            f.Publish(turn);
            Assert.Single(f.Preparation.Read().Results);
        }

        using var temp = new TempFolder();
        using var fakes = new FakeClients(temp.Create("bin"));
        var (standalonePid, standalonePing, standalonePong) = Probe(temp.Create("evidence"));
        FakeAgents.Install(fakes, ClientId.Codex, FakeAgents.Fresh(ClientId.Codex).SpawnResponder(standalonePid, standalonePing, standalonePong)
            .Print(FakeAgents.ReplyLines(ClientId.Codex, "DONE")).Exit(0));
        await using var runs = ProjectRuns.Open(temp.Create("project"), await fakes.DiscoverAsync().WaitAsync(Bound));
        var settled = NextSettled(runs);
        Assert.IsType<StartResult.Started>(runs.Start(TestNodes.Implement(TestTasks.Build, "Build", "Say DONE.", execution: AttemptEvents.CodexHigh)));
        Assert.Equal(AttemptStatus.Succeeded, (await settled.WaitAsync(Bound)).Status);
        await children.PidAsync(standalonePid).WaitAsync(Bound);

        File.WriteAllText(standalonePing, "ping");

        await WaitUntilAsync(() => File.Exists(standalonePong));
        Assert.Equal("pong", File.ReadAllText(standalonePong));
    }

    [UnixTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_escaped_descendant_outlives_cleanup_and_its_late_write_blocks_the_next_move(bool writes)
    {
        await using var f = new TurnFixture();
        using var children = new Processes();
        var pidFile = Path.Combine(f.Evidence, "escaped.pid");
        var gate = Path.Combine(f.Evidence, "escape-gate");
        await f.Open(FakeAgents.Fresh(ClientId.Codex).Print(FakeAgents.SessionLine(ClientId.Codex, "session-1"))
            .Write("result.txt", "done\n").SpawnEscapedWriter(pidFile, gate, "result.txt", "late\n")
            .Print(FakeAgents.ReplyLines(ClientId.Codex, "Done.")));
        var turn = await f.Settled(await f.Start());
        var escaped = await children.PidAsync(pidFile).WaitAsync(Bound);

        Assert.Equal(CleanupResult.Completed, turn.Cleanup!.Result);
        using (var process = Process.GetProcessById(escaped))
        {
            Assert.False(process.HasExited);
        }

        Assert.IsType<CaptureDisposition.Matched>(turn.Capture.Disposition);
        var result = f.Publish(turn);
        Assert.Equal("done\n", f.Preparation.Git.Git("show", Assert.IsType<CodeOutput.Produced>(result.Code).Code.Commit.Hex + ":result.txt"));
        Assert.IsType<TurnDisposition.Published>(Assert.IsType<Release.Released>(turn.Release()).Receipt);

        if (writes)
        {
            File.WriteAllText(gate, "go");
            AssertGone(escaped);
        }

        var retry = await f.Preparation.Prepare(T, f.Preparation.Op(), new AttemptCause.Retry(turn.Address.Launch.Attempt, f.Preparation.Op()));

        Assert.Single(f.Preparation.Read().Results);
        if (writes)
        {
            var block = Assert.IsType<Preparation.Blocked>(retry).Block;
            Assert.Equal(("DirtyWorktree", "The checkout tip or contents differ from the recorded attempt base."), (block.Problem.ToString(), block.Detail));
            Assert.Equal("late\n", File.ReadAllText(Path.Combine(f.Checkout, "result.txt")));
        }
        else
        {
            Assert.IsType<Preparation.Ready>(retry);
        }
    }

    [Fact]
    public async Task Cleanup_that_cuts_off_a_write_freezes_the_partial_file_the_successor_reads()
    {
        var consumer = PreparationFixture.Writer(U) with { Execution = new(ClientId.Codex) { Model = "gpt-6-sol", Reasoning = "high" } };
        await using var f = new TurnFixture(configure: workflow => Connect(Edit(workflow, TestNodes.Place(consumer, new(0, 0))), T, U));
        using var children = new Processes();
        var pidFile = Path.Combine(f.Evidence, "writer.pid");
        await f.Open(FakeAgents.Fresh(ClientId.Codex).Print(FakeAgents.SessionLine(ClientId.Codex, "session-1"))
            .SpawnPartialWriter(pidFile, "result.txt", "par").Print(FakeAgents.ReplyLines(ClientId.Codex, "Done.")));
        var turn = await f.Settled(await f.Start());
        var writer = await children.PidAsync(pidFile).WaitAsync(Bound);

        Assert.Equal(CleanupResult.Completed, turn.Cleanup!.Result);
        AssertGone(writer);
        Assert.Equal(["par", "par"], f.Preparation.Read().Captures[turn.Capture.Capture]
            .Select(capture => f.Preparation.Git.Git("show", capture.Candidate.Hex + ":result.txt")));
        f.Publish(turn);
        Assert.Single(f.Preparation.Read().Results);
        var ready = Assert.IsType<Preparation.Ready>(await f.Preparation.Prepare(U));
        Assert.Equal("par", File.ReadAllText(Path.Combine(ready.Checkout, "result.txt")));
    }

    [UnixFact]
    public async Task A_restarted_owner_never_signals_the_recorded_group()
    {
        await using var f = new TurnFixture(client: ClientId.Pi, model: "deepseek/deepseek-flash");
        using var processes = new Processes();
        var trapped = Path.Combine(f.Evidence, "signals.txt");
        var childPid = Path.Combine(f.Evidence, "child.pid");
        await f.Open(FakeAgents.Fresh(ClientId.Pi).TrapSignals(trapped).SpawnSleepingChild(childPid)
            .WaitForFile(Path.Combine(f.Evidence, "gate")));
        var intent = f.First();
        var launch = await f.Crash(processes, intent.Operation, "runner.running");
        var child = await processes.PidAsync(childPid).WaitAsync(Bound);
        var launched = Assert.Single(f.Log(launch).Events.OfType<AttemptEvent.Launched>());
        Assert.Equal(new Containment.Group(launched.ProcessId), launched.Containment);
        var root = new ProcessIdentity(launched.ProcessId, launched.ProcessStarted);

        await using (var fresh = f.OpenRuns(await f.Fakes.DiscoverAsync()))
        {
            fresh.MaterializerClock = TimeProvider.System;
            for (var reconcile = 0; reconcile < 2; reconcile++)
            {
                var found = Assert.IsType<Reconciliation.Found>(await fresh.Reconcile(f.Preparation.Permit, f.Preparation.Op(), launch).WaitAsync(Bound));
                var unresolved = Assert.IsType<TurnSettlement.Unresolved>(found.Settlement).Turn;
                Assert.Equal(("Uncertain", ProcessMatch.Same), (unresolved.Reason.ToString(), unresolved.Root));
            }

            Assert.Equal(launch, Assert.IsType<TurnStart.Existing>(await fresh.StartTurn(f.Preparation.Permit, intent).WaitAsync(Bound)).Launch);
        }

        await System.Threading.Tasks.Task.Delay(300);
        Assert.False(File.Exists(trapped));
        Assert.Equal(ProcessMatch.Same, ProcessCheck.Check(root));
        using (var survivor = Process.GetProcessById(child))
        {
            Assert.False(survivor.HasExited);
        }

        Assert.Equal(1, f.Launches);
    }

    private static (string Pid, string Ping, string Pong) Probe(string folder) =>
        (Path.Combine(folder, "responder.pid"), Path.Combine(folder, "ping"), Path.Combine(folder, "pong"));

    private static System.Threading.Tasks.Task<AttemptRecord> NextSettled(ProjectRuns runs)
    {
        var settled = new TaskCompletionSource<AttemptRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        runs.Changed += (_, _) =>
        {
            if (runs.Latest.GetValueOrDefault(TestTasks.Build) is { Status: not AttemptStatus.Running } record)
            {
                settled.TrySetResult(record);
            }
        };
        return settled.Task;
    }
}
