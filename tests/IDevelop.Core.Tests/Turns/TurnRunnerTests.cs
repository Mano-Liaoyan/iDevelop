using System.Diagnostics;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;
using static IDevelop.Core.Tests.Turns.TurnFixture;

namespace IDevelop.Core.Tests.Turns;

public sealed class TurnRunnerTests
{
    [Fact]
    public async Task A_workflow_turn_runs_in_its_checkout_and_settles()
    {
        await using var f = new TurnFixture();
        await f.Open();
        using var cleanup = new ProbeBarrier("runner.cleanup.inside");
        f.Runs.Probe = cleanup.Probe;
        var running = await f.Start();
        await cleanup.Reached.Task.WaitAsync(Bound);
        await running.RootExited.WaitAsync(Bound);
        Assert.False(running.Settlement.IsCompleted);
        Assert.Empty(f.Log(running.Address.Launch).Events.OfType<AttemptEvent.CleanedUp>());
        Assert.IsType<LeaseTake.Busy>(f.Preparation.Permit.TakeTask(T));
        cleanup.Dispose();
        var turn = await f.Settled(running);
        Assert.EndsWith(".worktrees/93f23689/90d5b0a2", File.ReadAllText(Path.Combine(f.Evidence, "cwd.txt")).Replace('\\', '/'));
        Assert.Equal("done\n", File.ReadAllText(Path.Combine(f.Checkout, "result.txt")));
        Assert.False(File.Exists(Path.Combine(f.Preparation.Git.Folder, "result.txt")));
        Assert.IsType<CaptureDisposition.Matched>(turn.Capture.Disposition);
        Assert.Equal("Succeeded", turn.Attempt.Status.ToString());
        var lines = File.ReadAllLines(Path.Combine(f.Folder(turn.Address.Launch), "events.jsonl"));
        Assert.Contains("\"lifetime\":\"workflow\"", Assert.Single(lines, line => line.Contains("\"type\":\"launched\"", StringComparison.Ordinal)));
        Assert.Single(lines, line => line.Contains("\"type\":\"cleanedUp\"", StringComparison.Ordinal));
        Assert.True(Array.FindIndex(lines, line => line.Contains("\"type\":\"cleanedUp\"", StringComparison.Ordinal)) <
            Array.FindIndex(lines, line => line.Contains("\"type\":\"exited\"", StringComparison.Ordinal)));
        Assert.Equal(1, f.Launches);
        var result = f.Publish(turn);
        Assert.Single(f.Preparation.Read().Results);
        Assert.Equal("done\n", f.Preparation.Git.Git("show", Assert.IsType<CodeOutput.Produced>(result.Code).Code.Commit.Hex + ":result.txt"));
        Assert.Equal(result.Id, Assert.IsType<TurnDisposition.Published>(Assert.IsType<Release.Released>(turn.Release()).Receipt).Result);
        Assert.IsType<LeaseTake.Taken>(f.Preparation.Permit.TakeTask(T)).Lease.Dispose();
    }

    [Theory]
    [InlineData("concurrent")]
    [InlineData("started")]
    [InlineData("startup-failure")]
    [InlineData("takeover")]
    [InlineData("task")]
    [InlineData("cause")]
    [InlineData("prompt")]
    public async Task A_duplicate_start_creates_one_process(string row)
    {
        await using var f = new TurnFixture();
        await f.Open(f.Waiting());
        if (row == "startup-failure") File.Delete(f.Shim);
        var intent = f.First();
        using var barrier = new ProbeBarrier("runner.launch.before");
        if (row == "concurrent") f.Runs.Probe = barrier.Probe;
        var firstTask = f.Runs.StartTurn(f.Preparation.Permit, intent);
        Task<TurnStart>? duplicateTask = null;
        if (row == "concurrent")
        {
            await barrier.Reached.Task.WaitAsync(Bound);
            duplicateTask = f.Runs.StartTurn(f.Preparation.Permit, intent);
            barrier.Dispose();
        }
        var first = await firstTask.WaitAsync(Bound);
        var launch = row == "startup-failure"
            ? Assert.IsType<TurnSettlement.Settled>(Assert.IsType<TurnStart.Settled>(first).Settlement).Turn.Address.Launch
            : Assert.IsType<TurnStart.Started>(first).Turn.Address.Launch;
        if (duplicateTask is not null)
        {
            var concurrent = Assert.IsType<TurnStart.Existing>(await duplicateTask.WaitAsync(Bound));
            Assert.Equal(launch, concurrent.Launch);
            Assert.Same(Assert.IsType<TurnStart.Started>(first).Turn, concurrent.Running);
        }
        if (row == "takeover")
        {
            await f.Runs.DisposeAsync();
            f.Preparation.ReleaseControl();
            await using var next = f.OpenRuns(await f.Fakes.DiscoverAsync());
            var duplicate = Assert.IsType<TurnStart.Existing>(await next.StartTurn(f.Preparation.Permit, intent).WaitAsync(Bound));
            Assert.Equal(launch, duplicate.Launch);
            Assert.Null(duplicate.Running);
            Assert.Null(duplicate.Settlement);
        }
        else if (row is "task" or "cause" or "prompt")
        {
            var changed = row switch
            {
                "task" => intent with { Task = U },
                "cause" => intent with { Cause = new AttemptCause.Retry(A1, f.Preparation.Op()) },
                _ => intent with { BasePrompt = "Changed" },
            };
            Assert.Equal("OperationConflict", Assert.IsType<TurnStart.Refused>(await f.Runs.StartTurn(f.Preparation.Permit, changed)).Reason.Problem.ToString());
        }
        else
        {
            var duplicate = Assert.IsType<TurnStart.Existing>(await f.Runs.StartTurn(f.Preparation.Permit, intent));
            Assert.Equal(launch, duplicate.Launch);
            if (row == "startup-failure") Assert.IsType<TurnSettlement.Settled>(duplicate.Settlement);
            else Assert.Same(Assert.IsType<TurnStart.Started>(first).Turn, duplicate.Running);
        }
        if (row != "takeover")
            Assert.Equal("TaskBusy", Assert.IsType<TurnStart.Refused>(await f.Runs.StartTurn(f.Preparation.Permit, f.First())).Reason.Problem.ToString());
        if (row != "startup-failure") await WaitUntilAsync(() => f.Launches == 1);
        Assert.Equal(row == "startup-failure" ? 0 : 1, f.Launches);
        Assert.Single(f.Preparation.Read().Claims);
    }

    [Fact]
    public async Task A_launch_failure_is_a_recorded_startup_failure()
    {
        await using var f = new TurnFixture();
        await f.Open();
        File.Delete(f.Shim);
        var start = Assert.IsType<TurnStart.Settled>(await f.Runs.StartTurn(f.Preparation.Permit, f.First()).WaitAsync(Bound));
        var turn = Assert.IsType<TurnSettlement.Settled>(start.Settlement).Turn;
        var failed = Assert.Single(f.Log(turn.Address.Launch).Events.OfType<AttemptEvent.LaunchFailed>());
        Assert.Equal(failed.Reason, Assert.IsType<RootExit.NotStarted>(turn.Exit.Exit).Detail);
        Assert.Contains("did not start", failed.Reason, StringComparison.Ordinal);
        Assert.Equal("Failed", turn.Attempt.Status.ToString());
        Assert.Equal(0, f.Launches);
        Assert.IsType<RunDecision.Recorded>(f.Preparation.Store.CloseAttempt(f.Preparation.Permit, f.Preparation.Op(),
            turn.Address.Launch.Attempt, TerminalAttemptOutcome.Failed, turn.Log));
        Assert.IsType<TurnDisposition.Ended>(Assert.IsType<Release.Released>(turn.Release()).Receipt);
        Assert.IsType<LeaseTake.Taken>(f.Preparation.Permit.TakeTask(T)).Lease.Dispose();
    }

    [Fact]
    public async Task A_root_that_never_exits_resolves_as_uncertain()
    {
        await using var f = new TurnFixture();
        await f.Open(f.Waiting(hang: true));
        f.Runs.StopSeam = _ => false;
        var running = await f.Start();
        await WaitUntilAsync(() => f.Log(running.Address.Launch).Record?.SessionId == "session-1");
        var launched = Assert.Single(f.Log(running.Address.Launch).Events.OfType<AttemptEvent.Launched>());
        using var process = Process.GetProcessById(launched.ProcessId);
        try
        {
            Assert.IsType<SendResult.Queued>(await running.CancelAsync().WaitAsync(Bound));
            var outcome = await running.Settlement.WaitAsync(f.Runs.ShutdownTime * 5);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => running.RootExited);
            Assert.Equal("The client did not exit after it was stopped.", error.Message);
            var turn = Assert.IsType<TurnSettlement.Unresolved>(outcome).Turn;
            Assert.Equal("Uncertain", turn.Reason.ToString());
            Assert.Empty(f.Preparation.Read().RootExits);
            Assert.Single(f.Preparation.Read().Claims);
            Assert.Equal(1, f.Launches);
            Assert.Equal("NotSettled", Assert.IsType<Release.Held>(turn.Release()).Reason.Problem.ToString());
            Assert.IsType<LeaseTake.Busy>(f.Preparation.Permit.TakeTask(T));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(Bound);
        }
        Assert.Empty(f.Preparation.Read().RootExits);
        Assert.Contains(f.Log(running.Address.Launch).Events, e => e is AttemptEvent.CancelRequested);
    }

    [Fact]
    public async Task A_failed_turn_request_append_keeps_an_unresolved_lease_bearing_handle()
    {
        await using var f = new TurnFixture(ConversationMode.Chat);
        await f.Open();
        var first = await f.Settled(await f.Start());
        Assert.Equal("WaitingForInput", first.Attempt.Status.ToString());
        var resting = Assert.IsType<TurnDisposition.Resting>(Assert.IsType<Release.Released>(first.Release()).Receipt);
        Assert.Equal("WaitingForInput", resting.Status.ToString());
        Assert.Empty(resting.Queued);
        var intent = new TurnIntent.Next(f.Preparation.Op(), new(first.Address.Launch.Attempt, 2), "Use the fixture");
        f.Runs.Probe = point => { if (point == "runner.turn-requested.before") throw new IOException("No turn request."); };
        var start = Assert.IsType<TurnStart.Settled>(await f.Runs.StartTurn(f.Preparation.Permit, intent).WaitAsync(Bound));
        var unresolved = Assert.IsType<TurnSettlement.Unresolved>(start.Settlement).Turn;
        Assert.Equal("IncompleteEvidence", unresolved.Reason.ToString());
        Assert.IsType<LeaseTake.Busy>(f.Preparation.Permit.TakeTask(T));
        Assert.Equal("NotSettled", Assert.IsType<Release.Held>(unresolved.Release()).Reason.Problem.ToString());
        Assert.True(f.Preparation.Read().Claims.ContainsKey(intent.Launch));
        Assert.IsType<AttemptEvent.Exited>(f.Log(intent.Launch).Events[^1]);
        Assert.Equal(1, f.Launches);
        f.Runs.Probe = null;
        var duplicate = Assert.IsType<TurnStart.Existing>(await f.Runs.StartTurn(f.Preparation.Permit, intent).WaitAsync(Bound));
        Assert.Equal(intent.Launch, duplicate.Launch);
        Assert.Same(start.Settlement, duplicate.Settlement);
        Assert.Equal(1, f.Launches);
        Assert.Equal(2, f.Preparation.Read().Claims.Count);
    }

    [Theory]
    [InlineData("runner.prepared", false)]
    [InlineData("runner.launch.before", false)]
    [InlineData("runner.launch.before", true)]
    public async Task Shutdown_before_attach_never_launches(string point, bool zeroTimeout)
    {
        await using var f = new TurnFixture();
        await f.Open();
        if (zeroTimeout) f.Runs.LeaveTimeout = TimeSpan.Zero;
        using var barrier = new ProbeBarrier(point);
        f.Runs.Probe = barrier.Probe;
        var command = f.Runs.StartTurn(f.Preparation.Permit, f.First());
        await barrier.Reached.Task.WaitAsync(Bound);
        var disposal = f.Runs.DisposeAsync().AsTask();
        if (zeroTimeout)
        {
            await disposal.WaitAsync(Bound);
            Assert.False(command.IsCompleted);
            barrier.Dispose();
        }
        else barrier.Dispose();
        await disposal.WaitAsync(Bound);
        var start = await command.WaitAsync(Bound);
        if (point == "runner.prepared")
        {
            Assert.Equal("RunStopped", Assert.IsType<TurnStart.Refused>(start).Reason.Problem.ToString());
            Assert.Empty(f.Preparation.Read().Claims);
            Assert.Single(f.Preparation.Read().Preparations);
        }
        else
        {
            var turn = Assert.IsType<TurnSettlement.Settled>(Assert.IsType<TurnStart.Settled>(start).Settlement).Turn;
            Assert.Equal("The project was closed before the client started.", Assert.IsType<RootExit.NotStarted>(turn.Exit.Exit).Detail);
            Assert.Single(f.Log(turn.Address.Launch).Events.OfType<AttemptEvent.LaunchFailed>());
            Assert.Single(f.Preparation.Read().Claims);
        }
        Assert.Equal(0, f.Launches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closing_the_project_settles_a_running_workflow_turn(bool abandon)
    {
        await using var f = new TurnFixture();
        await f.Open(f.Waiting());
        f.Runs.LeaveTimeout = abandon ? TimeSpan.Zero : Bound;
        var running = await f.Start();
        await WaitUntilAsync(() => f.Log(running.Address.Launch).Record?.SessionId == "session-1");
        await f.Runs.DisposeAsync().AsTask().WaitAsync(abandon ? Bound : Bound / 3);
        var turn = await f.Settled(running);
        Assert.Equal("Interrupted", turn.Attempt.Status.ToString());
        var events = f.Log(turn.Address.Launch).Events;
        Assert.Single(events.OfType<AttemptEvent.InterruptRequested>());
        Assert.Single(events.OfType<AttemptEvent.CleanedUp>());
        Assert.Single(events.OfType<AttemptEvent.Exited>());
        Assert.Empty(f.Preparation.Read().Results);
        Assert.IsType<RootExit.Exited>(turn.Exit.Exit);
        Assert.Equal(1, f.Launches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_cancelled_wait_never_revokes_the_command(bool missingShim)
    {
        await using var f = new TurnFixture();
        await f.Open();
        if (missingShim) File.Delete(f.Shim);
        using var barrier = new ProbeBarrier("runner.launch.before");
        f.Runs.Probe = barrier.Probe;
        using var wait = new CancellationTokenSource();
        var intent = f.First();
        var command = f.Runs.StartTurn(f.Preparation.Permit, intent, wait.Token);
        await barrier.Reached.Task.WaitAsync(Bound);
        wait.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command);
        barrier.Dispose();
        f.Runs.Probe = null;
        await WaitUntilAsync(() => f.Preparation.Read().RootExits.Count == 1);
        TurnStart.Existing? duplicate = null;
        await WaitUntilAsync(() =>
        {
            var task = f.Runs.StartTurn(f.Preparation.Permit, intent);
            if (!task.IsCompletedSuccessfully || task.Result is not TurnStart.Existing existing) return false;
            duplicate = existing;
            return true;
        });
        if (missingShim)
        {
            Assert.IsType<TurnSettlement.Settled>(duplicate!.Settlement);
            Assert.Equal(0, f.Launches);
        }
        else
        {
            var turn = await f.Settled(duplicate!.Running!);
            Assert.IsType<CaptureDisposition.Matched>(turn.Capture.Disposition);
            Assert.Equal(1, f.Launches);
        }
        Assert.Single(f.Preparation.Read().Claims);
    }

    [Theory]
    [InlineData("fault")]
    [InlineData("storage")]
    [InlineData("journal")]
    public async Task Faulted_and_rejected_commands_retry_in_the_same_window(string row)
    {
        await using var f = new TurnFixture();
        await f.Open();
        FileStream? held = null;
        var intent = f.First();
        f.Runs.Probe = point =>
        {
            if (point == "runner.request.before" && row == "fault") throw new InvalidOperationException("Request fault.");
            if (point == "runner.request.before" && row == "storage") throw new IOException("Request unavailable.");
            if (point == "journal.claim.before" && row == "journal") held = f.LockJournal();
        };
        try
        {
            if (row == "fault") await Assert.ThrowsAsync<InvalidOperationException>(() => f.Runs.StartTurn(f.Preparation.Permit, intent));
            else Assert.Equal(row == "storage" ? "StorageUnavailable" : "JournalBusy",
                Assert.IsType<TurnStart.Refused>(await f.Runs.StartTurn(f.Preparation.Permit, intent).WaitAsync(Bound)).Reason.Problem.ToString());
            Assert.Empty(f.Preparation.Read().Claims);
            Assert.Equal(0, f.Launches);
            Assert.IsType<LeaseTake.Taken>(f.Preparation.Permit.TakeTask(T)).Lease.Dispose();
        }
        finally { held?.Dispose(); }
        f.Runs.Probe = null;
        var turn = await f.Settled(await f.Start(intent));
        Assert.IsType<CaptureDisposition.Matched>(turn.Capture.Disposition);
        Assert.Equal(1, f.Launches);
        Assert.Single(f.Preparation.Read().Claims);
        Assert.Single(f.Log(turn.Address.Launch).Events.OfType<AttemptEvent.Requested>());
    }

    [Fact]
    public async Task A_rejected_settlement_write_retries_in_the_same_window()
    {
        await using var f = new TurnFixture();
        await f.Open();
        FileStream? held = null;
        f.Runs.Probe = point => { if (point == "journal.capture-1.before") held = f.LockJournal(); };
        var running = await f.Start();
        try
        {
            await running.RootExited.WaitAsync(Bound);
            var unresolved = Assert.IsType<TurnSettlement.Unresolved>(await running.Settlement.WaitAsync(Bound)).Turn;
            Assert.Equal("IncompleteEvidence", unresolved.Reason.ToString());
            Assert.Equal("JournalBusy", unresolved.Rejection!.Problem.ToString());
            Assert.Single(f.Preparation.Read().RootExits);
            Assert.IsType<LeaseTake.Busy>(f.Preparation.Permit.TakeTask(T));
            Assert.Empty(f.Preparation.Read().TurnClosures);
        }
        finally { held?.Dispose(); }
        f.Runs.Probe = null;
        var settled = Assert.IsType<TurnSettlement.Settled>(Assert.IsType<Reconciliation.Found>(await f.Runs.Reconcile(f.Preparation.Permit, f.Preparation.Op(), running.Address.Launch).WaitAsync(Bound)).Settlement).Turn;
        Assert.IsType<CaptureDisposition.Matched>(settled.Capture.Disposition);
        Assert.Equal(2, Assert.Single(f.Preparation.Read().Captures).Value.Count);
        Assert.Equal(1, f.Launches);
        var result = f.Publish(settled);
        Assert.Equal("done\n", f.Preparation.Git.Git("show", Assert.IsType<CodeOutput.Produced>(result.Code).Code.Commit.Hex + ":result.txt"));
    }

    [Theory]
    [InlineData("Published")]
    [InlineData("Ended")]
    [InlineData("Blocked")]
    public async Task Release_waits_for_a_durable_receipt(string receipt)
    {
        await using var f = new TurnFixture();
        var rule = receipt == "Ended" ? f.Success().Exit(1) : f.Success();
        await f.Open(rule);
        if (receipt == "Blocked") f.Runs.Probe = point =>
        {
            if (point == "journal.capture-1.after") File.WriteAllText(Path.Combine(f.Checkout, "result.txt"), "late\n");
        };
        var turn = await f.Settled(await f.Start());
        Assert.Equal("NotSettled", Assert.IsType<Release.Held>(turn.Release()).Reason.Problem.ToString());
        Assert.IsType<LeaseTake.Busy>(f.Preparation.Permit.TakeTask(T));
        if (receipt == "Published")
        {
            Assert.IsType<RunDecision.Recorded>(f.Preparation.Store.CloseAttempt(f.Preparation.Permit, f.Preparation.Op(),
                turn.Address.Launch.Attempt, TerminalAttemptOutcome.Succeeded, turn.Log));
            Assert.Equal("NotSettled", Assert.IsType<Release.Held>(turn.Release()).Reason.Problem.ToString());
            var result = Assert.IsType<Publication.Accepted>(f.Preparation.Materializer().Publish(turn.Lease, f.Preparation.Op(), turn.Address.Launch.Attempt)).Result;
            Assert.Equal(result.Id, Assert.IsType<TurnDisposition.Published>(Assert.IsType<Release.Released>(turn.Release()).Receipt).Result);
            Assert.Equal("done\n", f.Preparation.Git.Git("show", Assert.IsType<CodeOutput.Produced>(result.Code).Code.Commit.Hex + ":result.txt"));
        }
        else if (receipt == "Ended")
        {
            Assert.Equal("Failed", turn.Attempt.Status.ToString());
            Assert.IsType<RunDecision.Recorded>(f.Preparation.Store.CloseAttempt(f.Preparation.Permit, f.Preparation.Op(),
                turn.Address.Launch.Attempt, TerminalAttemptOutcome.Failed, turn.Log));
            var ended = Assert.IsType<TurnDisposition.Ended>(Assert.IsType<Release.Released>(turn.Release()).Receipt);
            Assert.Equal("Failed", Assert.IsType<AttemptEnd.Logged>(ended.End).Outcome.ToString());
        }
        else
        {
            Assert.IsType<CaptureDisposition.Diverged>(turn.Capture.Disposition);
            Assert.IsType<RunDecision.Recorded>(f.Preparation.Store.CloseAttempt(f.Preparation.Permit, f.Preparation.Op(),
                turn.Address.Launch.Attempt, TerminalAttemptOutcome.Succeeded, turn.Log));
            Assert.IsType<Publication.Blocked>(f.Preparation.Materializer().Publish(turn.Lease, f.Preparation.Op(), turn.Address.Launch.Attempt));
            var block = Assert.Single(f.Preparation.Read().Blocks, pair => !pair.Value.Resolved);
            Assert.Equal(block.Key, Assert.IsType<TurnDisposition.Blocked>(Assert.IsType<Release.Released>(turn.Release()).Receipt).Block);
        }
        Assert.IsType<LeaseTake.Taken>(f.Preparation.Permit.TakeTask(T)).Lease.Dispose();
        Assert.Equal(1, f.Launches);
    }

    [Fact]
    public async Task A_failed_launched_append_still_observes_the_real_exit()
    {
        await using var f = new TurnFixture();
        await f.Open(f.Waiting());
        f.Runs.Probe = point =>
        {
            if (point != "runner.launch.after") return;
            WaitUntilAsync(() => f.Launches == 1).GetAwaiter().GetResult();
            throw new IOException("The log is unwritable.");
        };
        var running = await f.Start();
        await running.RootExited.WaitAsync(Bound);
        var turn = Assert.IsType<TurnSettlement.Unresolved>(await running.Settlement.WaitAsync(Bound)).Turn;
        Assert.Equal("IncompleteEvidence", turn.Reason.ToString());
        Assert.IsType<RootExit.Exited>(Assert.Single(f.Preparation.Read().RootExits).Value.Exit);
        Assert.Equal(1, f.Launches);
        Assert.IsType<AttemptEvent.Requested>(Assert.Single(f.Log(running.Address.Launch).Events));
        Assert.Equal("NotSettled", Assert.IsType<Release.Held>(turn.Release()).Reason.Problem.ToString());
        Assert.IsType<LeaseTake.Busy>(f.Preparation.Permit.TakeTask(T));
    }

    [Theory]
    [InlineData(ClientId.Codex, "gpt-6-sol")]
    [InlineData(ClientId.Pi, "deepseek/deepseek-flash")]
    public async Task Stop_and_send_rests_until_an_explicit_next_turn(ClientId client, string model)
    {
        await using var f = new TurnFixture(client: client, model: model);
        await f.Open(f.Waiting());
        var running = await f.Start();
        await WaitUntilAsync(() => f.Log(running.Address.Launch).Record?.SessionId == "session-1");
        Assert.IsType<SendResult.Queued>(await running.SendAsync("Use the fixture", true).WaitAsync(Bound));
        var turn = await f.Settled(running);
        var resting = Assert.IsType<TurnDisposition.Resting>(Assert.IsType<Release.Released>(turn.Release()).Receipt);
        Assert.Equal("Running", resting.Status.ToString());
        Assert.Equal(new[] { "Use the fixture" }, resting.Queued);
        Assert.Equal(1, f.Launches);
        Assert.Single(f.Log(turn.Address.Launch).Events.OfType<AttemptEvent.Launched>());
        FakeAgents.Install(f.Fakes, client, FakeAgents.Resuming(client, "session-1")
            .RecordWorkingDirectory(Path.Combine(f.Evidence, "next-cwd.txt"))
            .Print(FakeAgents.SessionLine(client, "session-1"))
            .Print(FakeAgents.ReplyLines(client, "Done.")));
        var next = await f.Start(new TurnIntent.Next(f.Preparation.Op(), new(turn.Address.Launch.Attempt, 2), "Use the fixture"));
        var settled = await f.Settled(next);
        Assert.Equal("Succeeded", settled.Attempt.Status.ToString());
        Assert.Equal(2, f.Launches);
        Assert.Equal(f.Checkout, File.ReadAllText(Path.Combine(f.Evidence, "next-cwd.txt")));
        Assert.Empty(settled.Attempt.Queued);
        Assert.Equal(2, f.Log(settled.Address.Launch).Events.OfType<AttemptEvent.Launched>().Count());
    }

    [UnixFact]
    public async Task Root_exit_is_observed_before_drainage()
    {
        await using var f = new TurnFixture();
        using var children = new Processes();
        var pidFile = Path.Combine(f.Evidence, "child.pid");
        await f.Open(f.Success().SpawnSleepingChild(pidFile));
        var running = await f.Start();
        await children.PidAsync(pidFile).WaitAsync(Bound);
        await running.RootExited.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(running.Settlement.IsCompleted);
        Assert.Empty(f.Log(running.Address.Launch).Events.OfType<AttemptEvent.Exited>());
        var root = Assert.Single(f.Preparation.Read().RootExits).Value;
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", root.Tip.Hex);
        children.Dispose();
        var turn = await f.Settled(running);
        Assert.Single(f.Log(turn.Address.Launch).Events.OfType<AttemptEvent.Exited>());
        Assert.IsType<CaptureDisposition.Matched>(turn.Capture.Disposition);
    }

    [Fact]
    public async Task A_run_owned_task_refuses_standalone_commands_here()
    {
        await using var f = new TurnFixture();
        await f.Open(f.Waiting());
        var running = await f.Start();
        await WaitUntilAsync(() => f.Log(running.Address.Launch).Record?.SessionId == "session-1");
        Assert.Equal("Workflow", Assert.IsType<StartProblem.RunOwned>(Assert.IsType<StartResult.Refused>(f.Runs.Start(f.Writer)).Problem).Workflow);
        var send = Assert.IsType<SendResult.Refused>(await f.Runs.SendAsync(f.Writer, "Message", false));
        Assert.Equal("Workflow", Assert.IsType<StartProblem.RunOwned>(Assert.IsType<SendProblem.CannotStart>(send.Problem).Problem).Workflow);
        var terminal = Assert.IsType<TerminalResult.Refused>(f.Runs.OpenInTerminal(T));
        Assert.Equal("Workflow", Assert.IsType<StartProblem.RunOwned>(Assert.IsType<TerminalProblem.Blocked>(terminal.Problem).Problem).Workflow);
        Assert.Equal(1, f.Launches);
        Assert.IsType<SendResult.Queued>(await running.CancelAsync().WaitAsync(Bound));
        Assert.Equal("Cancelled", (await f.Settled(running)).Attempt.Status.ToString());
    }


    [Fact]
    public async Task A_busy_journal_retries_the_frozen_root_observation()
    {
        await using var f = new TurnFixture();
        await f.Open();
        f.Runs.ShutdownTime = TimeSpan.FromSeconds(2);
        FileStream? held = null;
        var retryCount = 0;
        f.Runs.Probe = point =>
        {
            if (point == "journal.root-exit.before" && held is null) held = f.LockJournal();
            if (point != "journal.root-exit.retry") return;
            retryCount++;
            Assert.Equal(1, retryCount);
            f.Preparation.Git.Write("result.txt", "late\n", f.Checkout);
            Assert.Equal(0, f.Preparation.Git.Run(f.Checkout, "add", "result.txt").ExitCode);
            Assert.Equal(0, f.Preparation.Git.Run(f.Checkout, "commit", "-qm", "Late commit").ExitCode);
            held!.Dispose();
        };
        try
        {
            var running = await f.Start();
            await running.RootExited.WaitAsync(Bound);
            var turn = await f.Settled(running);
            Assert.Equal(1, retryCount);
            Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", turn.Exit.Tip.Hex);
            Assert.IsType<CaptureDisposition.Diverged>(turn.Capture.Disposition);
            Assert.Single(f.Preparation.Read().RootExits);
            Assert.Equal(1, f.Launches);
        }
        finally { held?.Dispose(); }
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_read_only_turn_snapshots_its_checkout(bool write)
    {
        await using var f = new TurnFixture(readOnly: true);
        var rule = FakeAgents.Fresh(ClientId.Codex)
            .Print(FakeAgents.SessionLine(ClientId.Codex, "session-1"));
        if (write) rule = rule.Write("result.txt", "changed\n");
        await f.Open(rule.Print(FakeAgents.ReplyLines(ClientId.Codex, "Done.")));
        var turn = await f.Settled(await f.Start());
        var events = f.Log(turn.Address.Launch).Events;
        var start = Assert.IsType<AttemptEvent.Requested>(events[0]).Tree;
        var end = Assert.Single(events.OfType<AttemptEvent.Exited>()).Tree;
        Assert.NotNull(start);
        Assert.NotNull(end);
        Assert.Equal(write ? "Failed" : "Succeeded", turn.Attempt.Status.ToString());
        Assert.Equal(write, start != end);
        if (write) Assert.Equal("changed\n", File.ReadAllText(Path.Combine(f.Checkout, "result.txt")));
        else Assert.Equal("root\n", File.ReadAllText(Path.Combine(f.Checkout, "root.txt")));
        Assert.Equal(1, f.Launches);
    }

    [Fact]
    public async Task A_closed_turn_reconciles_from_its_log_prefix()
    {
        await using var f = new TurnFixture(ConversationMode.Chat);
        await f.Open();
        var first = await f.Settled(await f.Start());
        Assert.IsType<TurnDisposition.Resting>(Assert.IsType<Release.Released>(first.Release()).Receipt);
        FakeAgents.Install(f.Fakes, ClientId.Codex, FakeAgents.Resuming(ClientId.Codex, "session-1")
            .Print(FakeAgents.SessionLine(ClientId.Codex, "session-1"))
            .Print(FakeAgents.ReplyLines(ClientId.Codex, "Second reply.")));
        var second = await f.Settled(await f.Start(new TurnIntent.Next(f.Preparation.Op(), new(first.Address.Launch.Attempt, 2), "Again")));
        Assert.Equal(2, second.Attempt.Turns.Count);
        Assert.IsType<TurnDisposition.Resting>(Assert.IsType<Release.Released>(second.Release()).Receipt);
        await using var other = f.OpenRuns(await f.Fakes.DiscoverAsync());
        var restored = Assert.IsType<TurnSettlement.Settled>(Assert.IsType<Reconciliation.Found>(await other.Reconcile(f.Preparation.Permit, f.Preparation.Op(), first.Address.Launch).WaitAsync(Bound)).Settlement).Turn;
        Assert.Equal(first.Log, restored.Log);
        Assert.Equal("WaitingForInput", restored.Attempt.Status.ToString());
        Assert.Single(restored.Attempt.Turns);
        Assert.Equal("Done.", restored.Attempt.Result);
        Assert.Equal(2, f.Launches);
        Assert.IsType<Release.Released>(restored.Release());
    }


    [Theory]
    [InlineData("runner.claim.after", false)]
    [InlineData("journal.claim.after", false)]
    [InlineData("journal.claim.after", true)]
    public async Task A_failure_after_the_claim_keeps_its_owner(string point, bool io)
    {
        await using var f = new TurnFixture();
        await f.Open();
        f.Runs.Probe = step =>
        {
            if (step != point) return;
            if (io) throw new IOException("After claim.");
            throw new InvalidOperationException("After claim.");
        };
        var start = Assert.IsType<TurnStart.Settled>(await f.Runs.StartTurn(f.Preparation.Permit, f.First()).WaitAsync(Bound));
        var turn = Assert.IsType<TurnSettlement.Settled>(start.Settlement).Turn;
        Assert.Equal("After claim.", Assert.IsType<RootExit.NotStarted>(turn.Exit.Exit).Detail);
        Assert.Equal("Failed", turn.Attempt.Status.ToString());
        Assert.Equal(0, f.Launches);
        Assert.Single(f.Preparation.Read().Claims);
        Assert.Single(f.Log(turn.Address.Launch).Events.OfType<AttemptEvent.LaunchFailed>());
        Assert.IsType<LeaseTake.Busy>(f.Preparation.Permit.TakeTask(T));
    }

}
