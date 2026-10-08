using System.Text.Json;
using IDevelop.Core.Tests.Materialization;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;
using static IDevelop.Core.Tests.Turns.TurnFixture;

namespace IDevelop.Core.Tests.Turns;

public sealed class TurnLifecycleTests
{
    [Theory]
    [InlineData(ClientId.ClaudeCode, "claude-haiku-4-5")]
    [InlineData(ClientId.Codex, "gpt-6-sol")]
    [InlineData(ClientId.Pi, "deepseek/deepseek-flash")]
    [InlineData(ClientId.Antigravity, "gemini-3.8-flash")]
    public async Task Each_client_launches_in_the_prepared_checkout(ClientId client, string model)
    {
        await using var f = new TurnFixture(client: client, model: model);
        var arguments = Path.Combine(f.Evidence, "arguments.json");
        await f.Open(f.Success().RecordArguments(arguments));
        var turn = await f.Settled(await f.Start());
        Assert.EndsWith(".worktrees/93f23689/90d5b0a2", File.ReadAllText(Path.Combine(f.Evidence, "cwd.txt")).Replace('\\', '/'));
        Assert.Equal("done\n", File.ReadAllText(Path.Combine(f.Checkout, "result.txt")));
        Assert.IsType<CaptureDisposition.Matched>(turn.Capture.Disposition);
        Assert.Equal("Succeeded", turn.Attempt.Status.ToString());
        Assert.Equal(1, f.Launches);
        if (client == ClientId.Codex)
        {
            using var frame = JsonDocument.Parse(File.ReadAllText(arguments + ".thread.json"));
            Assert.Equal("thread/start", frame.RootElement.GetProperty("method").GetString());
            Assert.Equal(Path.GetFullPath(Path.Combine(f.Preparation.Git.Folder, ".worktrees", "93f23689", "90d5b0a2")),
                frame.RootElement.GetProperty("params").GetProperty("cwd").GetString());
        }
    }

    [Theory]
    [InlineData(ClientId.ClaudeCode, "claude-haiku-4-5", "--resume")]
    [InlineData(ClientId.Codex, "gpt-6-sol", "thread/resume")]
    [InlineData(ClientId.Pi, "deepseek/deepseek-flash", "--session-id")]
    [InlineData(ClientId.Antigravity, "gemini-3.8-flash", "--conversation")]
    public async Task A_reply_resumes_the_session_in_the_same_checkout(ClientId client, string model, string resume)
    {
        await using var f = new TurnFixture(ConversationMode.Chat, client: client, model: model);
        await f.Open();
        var first = await f.Settled(await f.Start());
        Assert.Equal("session-1", first.Attempt.SessionId);
        Assert.Equal("WaitingForInput", first.Attempt.Status.ToString());
        var resting = Assert.IsType<TurnDisposition.Resting>(Assert.IsType<Release.Released>(first.Release()).Receipt);
        Assert.Equal("WaitingForInput", resting.Status.ToString());
        Assert.Equal(Array.Empty<string>(), resting.Queued);
        Assert.IsType<LeaseTake.Taken>(f.Preparation.Permit.TakeTask(T)).Lease.Dispose();
        var firstLaunch = Assert.Single(f.Log(first.Address.Launch).Events.OfType<AttemptEvent.Launched>());
        Assert.Equal("Gone", ProcessCheck.Check(new(firstLaunch.ProcessId, firstLaunch.ProcessStarted)).ToString());
        Assert.Equal(1, f.Launches);
        var argumentsFile = Path.Combine(f.Evidence, "reply.json");
        var promptFile = Path.Combine(f.Evidence, "reply.txt");
        FakeAgents.Install(f.Fakes, client, FakeAgents.Resuming(client, "session-1")
            .RecordArguments(argumentsFile).RecordWorkingDirectory(Path.Combine(f.Evidence, "reply-cwd.txt"))
            .CaptureStdin(promptFile).Print(FakeAgents.SessionLine(client, "session-1"))
            .Print(FakeAgents.ReplyLines(client, "Done.")));
        var next = await f.Settled(await f.Start(new TurnIntent.Next(f.Preparation.Op(), new(first.Address.Launch.Attempt, 2), "Use the fixture")));
        Assert.Equal("WaitingForInput", next.Attempt.Status.ToString());
        Assert.Equal("session-1", next.Attempt.SessionId);
        Assert.IsType<CaptureDisposition.Matched>(next.Capture.Disposition);
        Assert.Equal(Path.GetFullPath(Path.Combine(f.Preparation.Git.Folder, ".worktrees", "93f23689", "90d5b0a2")),
            File.ReadAllText(Path.Combine(f.Evidence, "reply-cwd.txt")));
        Assert.Equal(client == ClientId.Antigravity
            ? "{\"event\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"Use the fixture\"}}\n" : "Use the fixture", File.ReadAllText(promptFile));
        if (client == ClientId.Codex)
        {
            using var frame = JsonDocument.Parse(File.ReadAllText(argumentsFile + ".thread.json"));
            Assert.Equal("thread/resume", frame.RootElement.GetProperty("method").GetString());
            var parameters = frame.RootElement.GetProperty("params");
            Assert.Equal("session-1", parameters.GetProperty("threadId").GetString());
            Assert.Equal(Path.GetFullPath(Path.Combine(f.Preparation.Git.Folder, ".worktrees", "93f23689", "90d5b0a2")),
                parameters.GetProperty("cwd").GetString());
        }
        else
        {
            var arguments = JsonSerializer.Deserialize<string[]>(File.ReadAllBytes(argumentsFile))!;
            var index = Array.IndexOf(arguments, resume);
            Assert.True(index >= 0, "The client's resume flag is missing.");
            Assert.Equal("session-1", arguments[index + 1]);
        }
        Assert.Equal(2, f.Launches);
        Assert.Single(f.Log(next.Address.Launch).Events.OfType<AttemptEvent.TurnRequested>());
        Assert.IsType<Release.Released>(next.Release());
    }

    [Theory]
    [InlineData("RunStopped")]
    [InlineData("TaskBusy")]
    public async Task A_refused_start_runs_again_and_converges(string problem)
    {
        await using var f = new TurnFixture();
        await f.Open();
        var intent = f.First();
        using var held = problem == "TaskBusy" ? Assert.IsType<LeaseTake.Taken>(f.Preparation.Permit.TakeTask(T)).Lease : null;
        if (problem == "RunStopped") f.Runs.Probe = StopBeforeClaim(f);
        var refused = Assert.IsType<TurnStart.Refused>(await f.Runs.StartTurn(f.Preparation.Permit, intent).WaitAsync(Bound));
        Assert.Equal(problem, refused.Reason.Problem.ToString());
        Assert.Empty(f.Preparation.Read().Claims);
        Assert.Equal(0, f.Launches);
        held?.Dispose();
        f.Runs.Probe = null;
        if (problem == "TaskBusy")
        {
            var turn = await f.Settled(await f.Start(intent));
            Assert.IsType<CaptureDisposition.Matched>(turn.Capture.Disposition);
            Assert.Equal(1, f.Launches);
            Assert.Single(f.Preparation.Read().Claims);
        }
        else
        {
            Assert.Equal("RunStopped", Assert.IsType<TurnStart.Refused>(await f.Runs.StartTurn(f.Preparation.Permit, intent).WaitAsync(Bound)).Reason.Problem.ToString());
            Assert.Equal("StopRequested", f.Preparation.Read().Phase.ToString());
            Assert.Empty(f.Preparation.Read().Claims);
            Assert.Equal(0, f.Launches);
            await SuccessfulControl();
        }
    }

    [Fact]
    public async Task Root_exit_frees_the_slot_while_cleanup_is_pending()
    {
        var x = PreparationFixture.Writer(C) with { Execution = new(ClientId.Codex) { Model = "gpt-6-sol", Reasoning = "high" } };
        await using var f = new TurnFixture(configure: workflow => Edit(workflow, TestNodes.Place(x, new(0, 0))));
        await f.Open();
        using var barrier = new ProbeBarrier("runner.cleanup.inside");
        var cleanups = 0;
        f.Runs.Probe = point =>
        {
            if (point == "runner.cleanup.inside" && Interlocked.Increment(ref cleanups) == 1) barrier.Probe(point);
        };
        var writer = await f.Start();
        await barrier.Reached.Task.WaitAsync(Bound);
        await writer.RootExited.WaitAsync(Bound);
        Assert.True(writer.RootExited.IsCompletedSuccessfully);
        Assert.False(writer.Settlement.IsCompleted);
        Assert.Empty(f.Log(writer.Address.Launch).Events.OfType<AttemptEvent.CleanedUp>());
        Assert.Single(f.Preparation.Read().RootExits);
        var independent = await f.Start(new TurnIntent.First(f.Preparation.Op(), C, new AttemptCause.Initial()));
        var settled = await f.Settled(independent);
        Assert.IsType<CaptureDisposition.Matched>(settled.Capture.Disposition);
        Assert.Equal("Succeeded", settled.Attempt.Status.ToString());
        Assert.Single(AttemptEvidence.Read(f.Preparation.Store.AttemptFolder(W, f.Preparation.RunId, C, independent.Address.Launch.Attempt))
            .Events.OfType<AttemptEvent.CleanedUp>());
        Assert.Equal(2, f.Launches);
        Assert.IsType<LeaseTake.Busy>(f.Preparation.Permit.TakeTask(T));
        barrier.Dispose();
        var turn = await f.Settled(writer);
        Assert.IsType<CaptureDisposition.Matched>(turn.Capture.Disposition);
        Assert.Single(f.Log(writer.Address.Launch).Events.OfType<AttemptEvent.CleanedUp>());
        Assert.Equal(2, f.Preparation.Read().TurnClosures.Count);
    }

    [Fact]
    public async Task Cancelling_a_turn_keeps_its_partial_write_without_a_result()
    {
        await using var f = new TurnFixture();
        await f.Open(FakeAgents.Fresh(ClientId.Codex).Print(FakeAgents.SessionLine(ClientId.Codex, "session-1"))
            .Write("result.txt", "partial\n").WaitForFile(Path.Combine(f.Evidence, "gate")));
        var running = await f.Start();
        await WaitUntilAsync(() => File.Exists(Path.Combine(f.Checkout, "result.txt")) &&
            File.ReadAllText(Path.Combine(f.Checkout, "result.txt")) == "partial\n");
        Assert.IsType<SendResult.Queued>(await running.CancelAsync().WaitAsync(Bound));
        var turn = await f.Settled(running);
        Assert.Equal("Cancelled", turn.Attempt.Status.ToString());
        Assert.IsType<CaptureDisposition.Matched>(turn.Capture.Disposition);
        Assert.Equal(new[] { "partial\n", "partial\n" }, f.Preparation.Read().Captures[turn.Capture.Capture]
            .Select(capture => f.Preparation.Git.Git("show", capture.Candidate.Hex + ":result.txt")));
        Assert.IsType<RunDecision.Recorded>(f.Preparation.Store.CloseAttempt(f.Preparation.Permit, f.Preparation.Op(),
            turn.Address.Launch.Attempt, TerminalAttemptOutcome.Cancelled, turn.Log));
        Assert.Equal("OutcomeMismatch", Assert.IsType<Publication.Rejected>(f.Preparation.Materializer()
            .Publish(turn.Lease, f.Preparation.Op(), turn.Address.Launch.Attempt)).Reason.Problem.ToString());
        Assert.Empty(f.Preparation.Read().Results);
        Assert.Equal("partial\n", File.ReadAllText(Path.Combine(f.Checkout, "result.txt")));
        Assert.Equal(1, f.Launches);
        Assert.Equal("Cancelled", Assert.IsType<AttemptEnd.Logged>(Assert.IsType<TurnDisposition.Ended>(Assert.IsType<Release.Released>(turn.Release()).Receipt).End).Outcome.ToString());
        await SuccessfulControl();
    }

    [Fact]
    public async Task Stop_before_claim_starts_no_client()
    {
        await using var f = new TurnFixture();
        await f.Open();
        f.Runs.Probe = StopBeforeClaim(f);
        Assert.Equal("RunStopped", Assert.IsType<TurnStart.Refused>(await f.Runs.StartTurn(f.Preparation.Permit, f.First()).WaitAsync(Bound)).Reason.Problem.ToString());
        Assert.Equal(0, f.Launches);
        Assert.Empty(f.Preparation.Read().Claims);
        Assert.Equal("StopRequested", f.Preparation.Read().Phase.ToString());
        await SuccessfulControl();
    }

    [Fact]
    public async Task A_refused_later_claim_leaves_the_log_resting()
    {
        await using var f = new TurnFixture(ConversationMode.Chat);
        await f.Open();
        var first = await f.Settled(await f.Start());
        Assert.Equal("WaitingForInput", first.Attempt.Status.ToString());
        Assert.IsType<Release.Released>(first.Release());
        f.Runs.Probe = StopBeforeClaim(f);
        Assert.Equal("RunStopped", Assert.IsType<TurnStart.Refused>(await f.Runs.StartTurn(f.Preparation.Permit,
            new TurnIntent.Next(f.Preparation.Op(), new(first.Address.Launch.Attempt, 2), "Use the fixture")).WaitAsync(Bound)).Reason.Problem.ToString());
        var log = f.Log(first.Address.Launch);
        Assert.IsType<AttemptEvent.Exited>(log.Events[^1]);
        Assert.Equal("WaitingForInput", log.Record!.Status.ToString());
        Assert.Empty(log.Events.OfType<AttemptEvent.TurnRequested>());
        Assert.Equal(1, f.Launches);
        Assert.Single(f.Preparation.Read().Claims);
        Assert.Equal("StopRequested", f.Preparation.Read().Phase.ToString());
        await RestingNextControl();
    }

    private static Action<string> StopBeforeClaim(TurnFixture f) => point =>
    {
        if (point == "runner.claim.before") Assert.IsType<RunDecision.Recorded>(f.Preparation.Store.Stop(f.Preparation.Permit, f.Preparation.Op()));
    };
}
