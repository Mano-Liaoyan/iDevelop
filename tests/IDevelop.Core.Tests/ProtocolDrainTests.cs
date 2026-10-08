using System.Text.Json;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.TestSupport.FakeAgents;

namespace IDevelop.Core.Tests;

public sealed class ProtocolDrainTests : IDisposable
{
    private const string Session = "session-1";
    private const string DeltaStart = """{"type":"stream_event","event":{"type":"message_start","message":{"id":"m1"}}}""";
    private const string Hel = """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hel"}}}""";
    private const string Lo = """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"lo"}}}""";
    private const string Complete = """{"type":"assistant","message":{"id":"m1","content":[{"type":"text","text":"Hello"}]}}""";
    private const string ExitPlan = """{"type":"control_request","request_id":"plan-1","request":{"subtype":"can_use_tool","tool_name":"ExitPlanMode","input":{"plan":"Change answer.txt"}}}""";
    private readonly TempFolder _temp = new();
    private readonly FakeClients _fakes;
    private readonly string _project;
    private readonly string _evidence;

    public ProtocolDrainTests()
    {
        _fakes = new FakeClients(_temp.Create("bin"));
        _project = _temp.Create("project");
        _evidence = _temp.Create("evidence");
    }
    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Codex_stop_and_send_has_only_the_attempt_and_stopped_markers()
    {
        Install(_fakes, ClientId.Codex, Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session))
            .WaitForLine("turn/interrupt")
            .Print("""{"method":"turn/completed","params":{"turn":{"id":"turn-1","status":"interrupted"}}}"""));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var task = Task(ClientId.Codex);
        runs.Follow(Workflow.Empty(WorkflowId.New()).Must(TestNodes.Place(task, new CanvasPoint(0, 0))));
        using var session = runs.OpenConversation(task.Id);
        Assert.IsType<StartResult.Started>(runs.Start(task));
        await Until(() => session.Snapshot.Latest?.SessionId == Session);
        Install(_fakes, ClientId.Codex, Resuming(ClientId.Codex, Session).Print(ReplyLines(ClientId.Codex, "Done")));
        var turn = session.Snapshot.Current!.Value;
        Assert.Equal(new SendResult.Queued(), await session.SendAsync(turn, "Continue", true, default));
        var record = await Settled(runs, task.Id);
        var page = Assert.IsType<HistoryResult.Page>(await session.ReadPageAsync(record.Id, new HistoryQuery.Latest(), 50, default));
        Assert.Equal((AttemptStatus.Succeeded, TurnOutcome.Stopped, 2), (record.Status, record.Turns[0].Outcome, record.Turns.Count));
        Assert.Equal(new[]
        {
            ("attempt", $"Attempt {record.Id}\nCodex, model gpt-6-sol, reasoning high, conversation Autonomous."),
            ("stopped", "The turn was stopped."),
        }, page.Entries.Select(entry => entry.Content).OfType<ConversationContent.Marker>().Select(marker => (marker.Kind, marker.Text)));
    }

    private sealed class NonPumpingContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) { }
    }

    [Fact]
    public async Task Leaving_from_a_non_pumping_context_completes_and_records_interruption()
    {
        Install(_fakes, ClientId.Codex, Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).Hang());
        var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        runs.ShutdownTime = TimeSpan.FromMilliseconds(100);
        runs.LeaveTimeout = TimeSpan.FromSeconds(1);
        var task = Task(ClientId.Codex);
        Assert.IsType<StartResult.Started>(runs.Start(task));
        await Until(() => runs.Latest[task.Id].SessionId == Session);
        var previous = SynchronizationContext.Current;
        System.Threading.Tasks.Task disposal;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new NonPumpingContext());
            disposal = runs.DisposeAsync().AsTask();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await disposal.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal((AttemptStatus.Interrupted, "The project was closed while this task ran."), (runs.Latest[task.Id].Status, runs.Latest[task.Id].Detail));
    }

    [Fact]
    public async Task Deltas_are_live_only_and_the_complete_message_is_persisted_once()
    {
        var gate = Path.Combine(_evidence, "go");
        Install(_fakes, ClientId.ClaudeCode, Fresh(ClientId.ClaudeCode).Print(SessionLine(ClientId.ClaudeCode, Session))
            .Print(DeltaStart).Print(Hel).WaitForFile(gate).Print(Lo).Print(Complete).Print(ReplyLines(ClientId.ClaudeCode, "Hello")));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var task = Task(ClientId.ClaudeCode);
        Assert.IsType<StartResult.Started>(runs.Start(task));
        await Until(() => runs.Live(task.Id)?.Buffers.GetValueOrDefault("m1:0")?.Text == "Hel");
        var live = runs.Live(task.Id)!.Value;
        Assert.Equal((5L, 4L, "Hel", 4L), (live.Revision, live.LogRevision, live.Buffers["m1:0"].Text, live.Buffers["m1:0"].Order));
        File.WriteAllText(gate, "go");
        var record = await Settled(runs, task.Id);
        var events = Events(record);
        Assert.Equal((AttemptStatus.Succeeded, "Hello"), (record.Status, record.Result));
        Assert.Equal(["Hello"], Messages(events).Select(message => message.Text));
        Assert.Equal(0, events.OfType<AttemptEvent.Agent>().Count(e => e.Event is AgentEvent.MessageDelta));
        Assert.Equal(("m1:0", false, (long?)4), (Messages(events).Single().Id, Messages(events).Single().Partial,
            events.OfType<AttemptEvent.Agent>().Single(e => e.Event is AgentEvent.Message).Order));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Overlapping_items_keep_their_history_window_when_completed_or_flushed_partial(bool complete)
    {
        var gate = Path.Combine(_evidence, "go");
        var fake = Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session))
            .Print("""{"method":"item/agentMessage/delta","params":{"itemId":"z","delta":"First"}}""")
            .Print("""{"method":"item/agentMessage/delta","params":{"itemId":"a","delta":"Second"}}""")
            .WaitForFile(gate);
        if (complete)
        {
            fake = fake.Print("""{"method":"item/completed","params":{"item":{"id":"a","type":"agentMessage","text":"Second"}}}""")
                .Print("""{"method":"item/completed","params":{"item":{"id":"z","type":"agentMessage","text":"First"}}}""");
        }

        Install(_fakes, ClientId.Codex, fake.Print("""{"method":"turn/completed","params":{"turn":{"id":"turn-1","status":"completed"}}}"""));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var task = Task(ClientId.Codex);
        using var session = runs.OpenConversation(task.Id);
        Assert.IsType<StartResult.Started>(runs.Start(task));
        await Until(() => runs.Live(task.Id)?.Buffers.Count == 2);
        var attempt = runs.Latest[task.Id].Id;
        try
        {
            var page = Assert.IsType<HistoryResult.Page>(await session.ReadPageAsync(attempt, new HistoryQuery.Latest(), 2, default));
            Assert.Equal(["1/message/ieg", "1/message/iYQ"], page.Entries.Select(entry => entry.Id.Value[(entry.Id.Value.IndexOf("/1/", StringComparison.Ordinal) + 1)..]));
            Assert.Equal([6L, 6L], page.Entries.Select(entry => entry.Order));
            Assert.Equal([MessageState.Streaming, MessageState.Streaming], page.Entries.Select(entry => Assert.IsType<ConversationContent.Message>(entry.Content).State));
            var liveRefresh = Assert.IsType<HistoryResult.Page>(await session.ReadPageAsync(attempt, new HistoryQuery.RefreshWindow(page.Window), 2, default));
            Assert.Equal(["First", "Second"], liveRefresh.Entries.Select(entry => Assert.IsType<ConversationContent.Message>(entry.Content).Text));
            File.WriteAllText(gate, "go");
            var record = await Settled(runs, task.Id);
            Assert.Equal((AttemptStatus.Succeeded, (string?)null), (record.Status, record.Detail));
            var refreshed = Assert.IsType<HistoryResult.Page>(await session.ReadPageAsync(attempt, new HistoryQuery.RefreshWindow(page.Window), 2, default));
            Assert.Equal(["1/message/ieg", "1/message/iYQ"], refreshed.Entries.Select(entry => entry.Id.Value[(entry.Id.Value.IndexOf("/1/", StringComparison.Ordinal) + 1)..]));
            Assert.Equal([6L, 6L], refreshed.Entries.Select(entry => entry.Order));
            Assert.Equal(["First", "Second"], refreshed.Entries.Select(entry => Assert.IsType<ConversationContent.Message>(entry.Content).Text));
            Assert.Equal(complete ? [MessageState.Complete, MessageState.Complete] : new[] { MessageState.Partial, MessageState.Partial },
                refreshed.Entries.Select(entry => Assert.IsType<ConversationContent.Message>(entry.Content).State));
            Assert.Equal(page.Window, refreshed.Window);
            Assert.Equal(page.Before, refreshed.Before);
            Assert.Equal(page.After, refreshed.After);
            var messages = Events(record).OfType<AttemptEvent.Agent>().Where(e => e.Event is AgentEvent.Message).ToArray();
            Assert.Equal(complete ? ["a", "z"] : new[] { "z", "a" }, messages.Select(e => ((AgentEvent.Message)e.Event).Id));
            Assert.Equal(complete ? [2, 1] : new[] { 1, 2 }, messages.Select(e => e.PresentationSequence!.Value));
            Assert.All(messages, e => Assert.Equal(3L, e.Order));
            var persisted = AttemptLog.FolderOf(Path.Combine(_project, ".idp", "attempts"), record.Task, record.Id);
            Assert.Equal(2, File.ReadLines(Path.Combine(persisted, "events.jsonl")).Count(line => line.Contains("\"presentationSequence\"", StringComparison.Ordinal)));
        }
        finally
        {
            File.WriteAllText(gate, "go");
        }
    }

    [Theory]
    [InlineData(ClientId.Pi, "deepseek/deepseek-v4-pro", "high")]
    [InlineData(ClientId.Antigravity, "gemini-3.8-flash", "low")]
    public async Task A_large_one_shot_prompt_to_a_client_that_reads_late_still_succeeds(ClientId client, string model, string reasoning)
    {
        var captured = Path.Combine(_evidence, "stdin.txt");
        Install(_fakes, client, Fresh(client).Sleep(700).CaptureStdin(captured)
            .Print(SessionLine(client, Session)).Print(ReplyLines(client, "Done")));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        runs.ShutdownTime = TimeSpan.FromMilliseconds(200);
        var goal = new string('x', 300_000);
        var task = TestNodes.Implement(TestTasks.Design, "Big", goal, execution: new ExecutionSettings(client) { Model = model, Reasoning = reasoning });
        Assert.IsType<StartResult.Started>(runs.Start(task));
        var record = await Settled(runs, task.Id);
        Assert.Equal((AttemptStatus.Succeeded, (string?)null), (record.Status, record.Detail));
        var prompt = client switch
        {
            ClientId.Pi => "# Big\n\n" + goal + "\n",
            ClientId.Antigravity => "{\"event\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"# Big\\n\\n" + goal + "\\n\"}}\n",
            ClientId.ClaudeCode or ClientId.Codex => throw new InvalidOperationException(),
        };
        Assert.Equal(prompt, File.ReadAllText(captured));
    }

    [Theory]
    [InlineData(ClientId.Pi, "deepseek/deepseek-v4-pro", "high")]
    [InlineData(ClientId.Antigravity, "gemini-3.8-flash", "low")]
    public async Task A_blocked_one_shot_prompt_stays_running_until_cancelled(ClientId client, string model, string reasoning)
    {
        var ready = Path.Combine(_evidence, "ready");
        var gate = Path.Combine(_evidence, "go");
        Install(_fakes, client, Fresh(client).Print(SessionLine(client, Session)).Write(ready, "yes").WaitForFile(gate).Hang());
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        runs.ShutdownTime = TimeSpan.FromMilliseconds(200);
        var task = TestNodes.Implement(TestTasks.Design, "Big", new string('x', 300_000),
            execution: new ExecutionSettings(client) { Model = model, Reasoning = reasoning });
        Assert.IsType<StartResult.Started>(runs.Start(task));
        try
        {
            await Until(() => File.Exists(ready));
            await System.Threading.Tasks.Task.Delay(400);
            Assert.Equal(AttemptStatus.Running, runs.Latest[task.Id].Status);
            Assert.Null(await runs.CancelAsync(task.Id));
            var record = await Settled(runs, task.Id);
            Assert.Equal((AttemptStatus.Cancelled, (string?)null), (record.Status, record.Detail));
            Assert.Single(Events(record).OfType<AttemptEvent.CancelRequested>());
            Assert.Equal([], Events(record).OfType<AttemptEvent.Agent>().Select(e => e.Event).OfType<AgentEvent.Failed>().Select(failed => failed.Reason));
        }
        finally
        {
            File.WriteAllText(gate, "go");
        }
    }

    [Theory]
    [InlineData(ClientId.ClaudeCode)]
    [InlineData(ClientId.Codex)]
    public async Task A_successful_client_exiting_after_the_stop_budget_still_succeeds(ClientId client)
    {
        Install(_fakes, client, Fresh(client).Print(SessionLine(client, Session)).Print(ReplyLines(client, "Done"))
            .WaitForStdinEnd().Sleep(1500).Exit(0));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        runs.ShutdownTime = TimeSpan.FromMilliseconds(200);
        var task = Task(client);
        Assert.IsType<StartResult.Started>(runs.Start(task));
        var record = await Settled(runs, task.Id);
        Assert.Equal((AttemptStatus.Succeeded, (string?)null), (record.Status, record.Detail));
        Assert.Equal(TurnOutcome.Succeeded, record.Turns.Single().Outcome);
        Assert.Equal(0, Assert.Single(Events(record).OfType<AttemptEvent.Exited>()).ExitCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Interrupt_acknowledgement_is_followed_by_the_aborted_full_tail(bool stopAndSend)
    {
        Install(_fakes, ClientId.ClaudeCode,
            Resuming(ClientId.ClaudeCode, Session).Print(SessionLine(ClientId.ClaudeCode, Session)).Print(ReplyLines(ClientId.ClaudeCode, "Next")),
            Fresh(ClientId.ClaudeCode).Print(SessionLine(ClientId.ClaudeCode, Session)).Print(DeltaStart).Print(Hel)
                .WaitForLine("\"subtype\":\"interrupt\"")
                .EchoId("""{"type":"control_response","response":{"subtype":"success","request_id":$id,"response":{}}}""")
                .Print(Complete).Print("""{"type":"result","subtype":"error_during_execution","is_error":true,"terminal_reason":"aborted_streaming"}"""));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var task = Task(ClientId.ClaudeCode);
        runs.Start(task);
        await Until(() => runs.Live(task.Id)?.Buffers.GetValueOrDefault("m1:0")?.Text == "Hel");
        if (stopAndSend) Assert.Equal(new SendResult.Queued(), await runs.SendAsync(task, "Continue", true));
        else Assert.Null(await runs.CancelAsync(task.Id));
        var record = await Settled(runs, task.Id);
        var messages = Messages(Events(record)).ToArray();
        Assert.Equal([new AgentEvent.Message("Hello") { Id = "m1:0", Partial = true }], messages);
        Assert.Equal(TurnOutcome.Stopped, record.Turns[0].Outcome);
        Assert.Equal("Hello", record.Turns[0].FinalText);
        Assert.Equal(stopAndSend ? AttemptStatus.Succeeded : AttemptStatus.Cancelled, record.Status);
        if (stopAndSend) Assert.Equal(new TurnRecord(2, "Continue", TurnOutcome.Succeeded, "Next"), record.Turns[1]);
    }

    [Fact]
    public async Task An_unsupported_Claude_control_request_gets_an_error_and_notice_and_the_turn_succeeds()
    {
        var frames = Path.Combine(_evidence, "frames.jsonl");
        Install(_fakes, ClientId.ClaudeCode, Fresh(ClientId.ClaudeCode).RecordFrames(frames)
            .Print(SessionLine(ClientId.ClaudeCode, Session))
            .Print("""{"type":"control_request","request_id":"hook-1","request":{"subtype":"hook_callback","callback_id":"hook-42","input":{"hook_event_name":"PreToolUse","tool_name":"Read"}}}""")
            .Print(ReplyLines(ClientId.ClaudeCode, "Done")).WaitForStdinEnd());
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var task = Task(ClientId.ClaudeCode);
        Assert.IsType<StartResult.Started>(runs.Start(task));
        var record = await Settled(runs, task.Id);
        Assert.Equal(["""{"type":"control_response","response":{"subtype":"error","request_id":"hook-1","error":"Method not supported."}}"""], File.ReadAllLines(frames));
        Assert.Equal([new AgentEvent.Notice("Claude Code requested an unsupported control method.")],
            Events(record).OfType<AttemptEvent.Agent>().Select(e => e.Event).OfType<AgentEvent.Notice>());
        Assert.Equal((AttemptStatus.Succeeded, "Done", TurnOutcome.Succeeded), (record.Status, record.Result, record.Turns.Single().Outcome));
    }

    [Fact]
    public async Task Codex_cancel_before_the_turn_start_response_writes_one_interrupt_when_the_id_arrives()
    {
        var ready = Path.Combine(_evidence, "ready");
        var go = Path.Combine(_evidence, "go");
        var frames = Path.Combine(_evidence, "frames.jsonl");
        Install(_fakes, ClientId.Codex, Fresh(ClientId.Codex)
            .BeforeTurnResponse(FakeRule.On().RecordFrames(frames).Print(SessionLine(ClientId.Codex, Session))
                .Write(ready, "yes").WaitForFile(go))
            .WaitForLine("\"method\":\"turn/interrupt\"").EchoId("""{"id":$id,"result":{}}""")
            .Print("""{"method":"turn/started","params":{"turn":{"id":"turn-1"}}}""")
            .Print("""{"id":"turn-1","result":{"turn":{"id":"turn-1"}}}""")
            .Print("""{"method":"turn/completed","params":{"turn":{"id":"turn-1","status":"interrupted"}}}""").WaitForStdinEnd());
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var task = Task(ClientId.Codex);
        Assert.IsType<StartResult.Started>(runs.Start(task));
        try
        {
            await Until(() => File.Exists(ready) && runs.Latest[task.Id].SessionId == Session);
            Assert.Null(await runs.CancelAsync(task.Id));
            Assert.Single(Events(runs.Latest[task.Id]).OfType<AttemptEvent.CancelRequested>());
            File.WriteAllText(go, "go");
            var record = await Settled(runs, task.Id);
            Assert.Equal(["""{"id":"stop-1","method":"turn/interrupt","params":{"threadId":"session-1","turnId":"turn-1"}}"""], File.ReadAllLines(frames));
            Assert.Equal((AttemptStatus.Cancelled, TurnOutcome.Stopped), (record.Status, record.Turns.Single().Outcome));
        }
        finally
        {
            File.WriteAllText(go, "go");
        }
    }

    [Fact]
    public async Task Codex_interrupt_drains_the_completed_tail_as_one_partial_message()
    {
        var wire = Path.Combine(_evidence, "interrupt.json");
        Install(_fakes, ClientId.Codex, Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session))
            .Print("""{"method":"item/agentMessage/delta","params":{"itemId":"i1","delta":"Hel"}}""")
            .ReadLine(wire).EchoId("""{"id":$id,"result":{}}""")
            .Print("""{"method":"item/completed","params":{"item":{"id":"i1","type":"agentMessage","text":"Hello"}}}""")
            .Print("""{"method":"turn/completed","params":{"turn":{"id":"turn-1","status":"interrupted"}}}"""));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var task = Task(ClientId.Codex);
        Assert.IsType<StartResult.Started>(runs.Start(task));
        await Until(() => runs.Live(task.Id)?.Buffers.GetValueOrDefault("i1")?.Text == "Hel");
        Assert.Null(await runs.CancelAsync(task.Id));
        var record = await Settled(runs, task.Id);
        Assert.Equal("""{"id":"stop-1","method":"turn/interrupt","params":{"threadId":"session-1","turnId":"turn-1"}}""", File.ReadAllText(wire));
        Assert.Equal([new AgentEvent.Message("Hello") { Id = "i1", Partial = true }], Messages(Events(record)).ToArray());
        Assert.Equal((AttemptStatus.Cancelled, TurnOutcome.Stopped), (record.Status, record.Turns.Single().Outcome));
        Assert.DoesNotContain(Events(record).OfType<AttemptEvent.Agent>(), e => e.Event is AgentEvent.MessageDelta);
    }

    [Fact]
    public async Task Read_only_May_ask_Claude_declines_ExitPlanMode_without_changing_the_project()
    {
        using var git = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", ["init", "-q", _project]) { UseShellExecute = false })!;
        await git.WaitForExitAsync();
        File.WriteAllText(Path.Combine(_project, "answer.txt"), "before");
        var wire = Path.Combine(_evidence, "denial.json");
        Install(_fakes, ClientId.ClaudeCode, Fresh(ClientId.ClaudeCode).Print(SessionLine(ClientId.ClaudeCode, Session)).Print(ExitPlan)
            .ReadLine(wire).Print(ReplyLines(ClientId.ClaudeCode, "Stayed in plan")));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var task = new TaskDefinition(TestTasks.Design, BuiltInBlueprints.Plan)
        {
            Execution = Settings(ClientId.ClaudeCode), Conversation = ConversationMode.MayAsk,
        }.WithField("goal", "Keep the project unchanged")!;
        runs.Start(task);
        var record = await Settled(runs, task.Id);
        using var denial = JsonDocument.Parse(File.ReadAllText(wire));
        Assert.Equal("deny", denial.RootElement.GetProperty("response").GetProperty("response").GetProperty("behavior").GetString());
        Assert.Equal(PermissionState.Denied, Assert.IsType<RequestRecord.Permission>(Assert.Single(record.Requests).Value).State);
        Assert.Equal("before", File.ReadAllText(Path.Combine(_project, "answer.txt")));
        Assert.Equal((AttemptStatus.Succeeded, 0), (record.Status, record.Requests.Values.Count(request => request is RequestRecord.Permission { State: PermissionState.Declining })));
    }

    [Fact]
    public async Task An_Autonomous_question_closes_PolicyDenied_and_the_turn_succeeds()
    {
        var wire = Path.Combine(_evidence, "decline.json");
        Install(_fakes, ClientId.ClaudeCode, Fresh(ClientId.ClaudeCode).Print(SessionLine(ClientId.ClaudeCode, Session))
            .Print(Fixture.Lines("c1/claude-question.jsonl")).ReadLine(wire).Print(ReplyLines(ClientId.ClaudeCode, "Done")));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var task = Task(ClientId.ClaudeCode);
        runs.Start(task);
        var record = await Settled(runs, task.Id);
        var question = Assert.IsType<RequestRecord.Question>(Assert.Single(record.Requests).Value);
        Assert.Equal(new QuestionState.Closed(RequestCloseReason.PolicyDenied, null), question.State);
        Assert.Equal((AttemptStatus.Succeeded, "Done", 0), (record.Status, record.Result, record.Requests.Values.Count(request => request is RequestRecord.Question { State: QuestionState.Open })));
        Assert.Single(Events(record).OfType<AttemptEvent.QuestionRecorded>());
        Assert.DoesNotContain(Events(record).OfType<AttemptEvent.Agent>(), e => e.Event is AgentEvent.QuestionAsked);
        using var decline = JsonDocument.Parse(File.ReadAllText(wire));
        Assert.Equal("deny", decline.RootElement.GetProperty("response").GetProperty("response").GetProperty("behavior").GetString());
    }

    [Fact]
    public async Task An_unexpected_Codex_command_approval_is_declined_and_recorded_Denied()
    {
        var wire = Path.Combine(_evidence, "decline.json");
        Install(_fakes, ClientId.Codex, Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session))
            .Print(Fixture.Lines("c1/codex-approval.jsonl")).ReadLine(wire).Print(ReplyLines(ClientId.Codex, "Done")));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var task = Task(ClientId.Codex);
        runs.Start(task);
        var record = await Settled(runs, task.Id);
        Assert.Equal("""{"id":0,"result":{"decision":"decline"}}""", File.ReadAllText(wire));
        var permission = Assert.IsType<RequestRecord.Permission>(Assert.Single(record.Requests).Value);
        Assert.Equal(("n:0", PermissionState.Denied), (permission.Key.Id, permission.State));
        Assert.Equal((AttemptStatus.Succeeded, "Done"), (record.Status, record.Result));
        Assert.Equal(RequestCloseReason.PolicyDenied, Assert.Single(Events(record).OfType<AttemptEvent.RequestClosed>()).Reason);
    }

    [Fact]
    public async Task A_successful_app_server_that_does_not_exit_fails_at_the_success_exit_deadline()
    {
        Install(_fakes, ClientId.Codex, Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, "Done")).Hang());
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        runs.SuccessExitTime = TimeSpan.FromMilliseconds(200);
        var task = Task(ClientId.Codex);
        runs.Start(task);
        var record = await Settled(runs, task.Id);
        Assert.Equal((AttemptStatus.Failed, "The client reported success but did not shut down."), (record.Status, record.Detail));
        Assert.Equal(TurnOutcome.Failed, record.Turns.Single().Outcome);
        Assert.Single(Events(record).OfType<AttemptEvent.ShutdownForced>());
    }

    [Fact]
    public async Task A_broken_permission_pipe_closes_DeliveryUnknown_and_fails_the_turn()
    {
        using var fakes = new FakeClients(_fakes.Folder, FakeClientInstallMode.Direct);
        var command = Install(fakes, ClientId.ClaudeCode, Fresh(ClientId.ClaudeCode).Print(SessionLine(ClientId.ClaudeCode, Session)).CloseStdin().Print(ExitPlan)
            .Print("""{"type":"control_cancel_request","request_id":"plan-1"}""").Hang());
        Assert.Equal(OperatingSystem.IsWindows() ? "claude.exe" : "claude", Path.GetFileName(command));
        await using var runs = ProjectRuns.Open(_project, await fakes.DiscoverAsync());
        var task = Task(ClientId.ClaudeCode);
        runs.Start(task);
        var record = await Settled(runs, task.Id);
        var permission = Assert.IsType<RequestRecord.Permission>(Assert.Single(record.Requests).Value);
        Assert.Equal(PermissionState.DeliveryUnknown, permission.State);
        Assert.Equal((AttemptStatus.Failed, "iDevelop could not write to the client's input pipe."), (record.Status, record.Detail));
        Assert.Equal(RequestCloseReason.DeliveryUnknown, Assert.Single(Events(record).OfType<AttemptEvent.RequestClosed>()).Reason);
    }

    [Fact]
    public async Task Changed_content_under_one_request_id_fails_the_turn()
    {
        Install(_fakes, ClientId.ClaudeCode, Fresh(ClientId.ClaudeCode).Print(SessionLine(ClientId.ClaudeCode, Session)).Print(ExitPlan)
            .Print(ExitPlan.Replace("Change answer.txt", "Changed plan", StringComparison.Ordinal)).Hang());
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var task = Task(ClientId.ClaudeCode);
        runs.Start(task);
        var record = await Settled(runs, task.Id);
        Assert.Equal((AttemptStatus.Failed, "Claude Code changed request s:plan-1 within the turn."), (record.Status, record.Detail));
        Assert.Single(record.Requests);
    }

    [Fact]
    public async Task Send_returns_after_the_queued_message_is_flushed()
    {
        Install(_fakes, ClientId.ClaudeCode, Fresh(ClientId.ClaudeCode).Print(SessionLine(ClientId.ClaudeCode, Session)).Hang());
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        runs.ShutdownTime = TimeSpan.FromMilliseconds(200);
        var task = Task(ClientId.ClaudeCode);
        runs.Start(task);
        await Until(() => runs.Latest[task.Id].SessionId == Session);
        Assert.Equal(new SendResult.Queued(), await runs.SendAsync(task, "banana", false));
        Assert.Equal("banana", Assert.Single(Events(runs.Latest[task.Id]).OfType<AttemptEvent.MessageQueued>()).Text);
        Assert.Null(await runs.CancelAsync(task.Id));
        var record = await Settled(runs, task.Id);
        Assert.Equal(AttemptStatus.Cancelled, record.Status);
        Assert.Equal(["banana"], record.Queued.Select(message => message.Text));
    }

    [Fact]
    public async Task Cancel_between_a_turn_and_its_queued_message_cancels_without_launching_the_next_turn()
    {
        var gate = Path.Combine(_evidence, "go");
        var resumed = Path.Combine(_evidence, "resumed");
        Install(_fakes, ClientId.ClaudeCode,
            Resuming(ClientId.ClaudeCode, Session).Write(resumed, "yes").Print(ReplyLines(ClientId.ClaudeCode, "Next")),
            Fresh(ClientId.ClaudeCode).Print(SessionLine(ClientId.ClaudeCode, Session)).WaitForFile(gate)
                .Print(ReplyLines(ClientId.ClaudeCode, "First")).WaitForStdinEnd());
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var task = Task(ClientId.ClaudeCode);
        runs.Start(task);
        await Until(() => runs.Latest[task.Id].SessionId == Session);
        Assert.Equal(new SendResult.Queued(), await runs.SendAsync(task, "banana", false));
        Task<StartProblem?>? cancel = null;
        runs.Changed += (_, _) =>
        {
            if (cancel is null && runs.Latest.GetValueOrDefault(task.Id) is { Status: AttemptStatus.Running, Turns: [{ Outcome: TurnOutcome.Succeeded }] })
            {
                cancel = runs.CancelAsync(task.Id);
            }
        };
        File.WriteAllText(gate, "go");
        var record = await Settled(runs, task.Id);
        Assert.NotNull(cancel);
        Assert.Null(await cancel);
        Assert.Equal((AttemptStatus.Cancelled, 1, false), (record.Status, record.Turns.Count, File.Exists(resumed)));
        Assert.Equal(["banana"], record.Queued.Select(message => message.Text));
    }

    private AttemptEvent[] Events(AttemptRecord record) => [.. AttemptLog.Read(AttemptLog.FolderOf(Path.Combine(_project, ".idp", "attempts"), record.Task, record.Id))];
    private static IEnumerable<AgentEvent.Message> Messages(IEnumerable<AttemptEvent> events) => events.OfType<AttemptEvent.Agent>().Select(e => e.Event).OfType<AgentEvent.Message>();
    private static ExecutionSettings Settings(ClientId client) => new(client)
    {
        Model = client == ClientId.Codex ? "gpt-6-sol" : "claude-haiku-4-5", Reasoning = "high",
    };
    private static TaskDefinition Task(ClientId client) => TestNodes.Implement(TestTasks.Design, "Say hi", "Say Hello", execution: Settings(client));
    private static async Task<AttemptRecord> Settled(ProjectRuns runs, TaskId task)
    {
        await Until(() => runs.Latest.GetValueOrDefault(task) is { Status: not AttemptStatus.Running } && runs.Active.All(record => record.Task != task));
        return runs.Latest[task];
    }
    private static async System.Threading.Tasks.Task Until(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, "The turn did not reach the expected state.");
            await System.Threading.Tasks.Task.Delay(10);
        }
    }
}
