using System.Text.Json;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.TestSupport.FakeAgents;

namespace IDevelop.Core.Tests;

[Collection(ProcessCollection.Name)]
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
    public async Task A_successful_app_server_that_does_not_exit_fails_at_the_shutdown_deadline()
    {
        Install(_fakes, ClientId.Codex, Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, "Done")).Hang());
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        runs.ShutdownTime = TimeSpan.FromMilliseconds(200);
        var task = Task(ClientId.Codex);
        runs.Start(task);
        var record = await Settled(runs, task.Id);
        Assert.Equal((AttemptStatus.Failed, "The client reported success but did not shut down."), (record.Status, record.Detail));
        Assert.Equal(TurnOutcome.Failed, record.Turns.Single().Outcome);
        Assert.Single(Events(record).OfType<AttemptEvent.ShutdownForced>());
    }

    [UnixFact]
    public async Task A_broken_permission_pipe_closes_DeliveryUnknown_and_fails_the_turn()
    {
        Install(_fakes, ClientId.ClaudeCode, Fresh(ClientId.ClaudeCode).Print(SessionLine(ClientId.ClaudeCode, Session)).CloseStdin().Print(ExitPlan)
            .Print("""{"type":"control_cancel_request","request_id":"plan-1"}""").Hang());
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
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
