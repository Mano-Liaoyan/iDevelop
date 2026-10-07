using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using IDevelop.Execution;
using IDevelop.Projects;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.TestSupport.FakeAgents;

namespace IDevelop.Core.Tests;

[Collection(ProcessCollection.Name)]
public sealed class ConversationDrainTests : IDisposable
{
    private const string Session = "session-1";
    private const string Aborted = """{"type":"result","subtype":"error_during_execution","is_error":true,"terminal_reason":"aborted_streaming"}""";
    private const string Question = """{"type":"control_request","request_id":"q1","request":{"subtype":"can_use_tool","tool_name":"AskUserQuestion","input":{"questions":[{"question":"Fixture?","header":"Fixture","options":[{"label":"Local"},{"label":"Remote"}],"multiSelect":false}]}}}""";
    private static readonly HostQuestions.Bounded Bounded = new(TimeSpan.FromSeconds(55), TimeSpan.FromSeconds(5));
    private static readonly ExecutionSettings Settings = new(ClientId.ClaudeCode) { Model = "claude-haiku-4-5", Reasoning = "high" };
    private static readonly QuestionsReply Local = new([new QuestionAnswer("q:0", ["o:0"], null)]);
    private readonly TempFolder _temp = new();
    private readonly FakeClients _fakes;
    private readonly string _project;
    private readonly string _evidence;
    private readonly ManualTimeProvider _clock = new();
    private Workflow _workflow = Workflow.Empty(WorkflowId.New()).Must(TestNodes.Place(Node, new CanvasPoint(0, 0)));
    public ConversationDrainTests()
    {
        _fakes = new FakeClients(_temp.Create("bin"));
        _project = _temp.Create("project");
        _evidence = _temp.Create("evidence");
    }
    public void Dispose() => _temp.Dispose();
    private string FileAt(string name) => Path.Combine(_evidence, name);
    private static TaskDefinition Node => TestNodes.Implement(TestTasks.Build, "Build", "Build it", execution: Settings, conversation: ConversationMode.MayAsk);
    private void InstallQuestion(FakeRule tail)
    {
        var rule = Fresh(ClientId.ClaudeCode).RecordArguments(FileAt("launch.json")).RecordFrames(FileAt("frames.jsonl"))
            .Print(SessionLine(ClientId.ClaudeCode, Session)).Print(Question);
        Install(_fakes, ClientId.ClaudeCode, rule with { Steps = rule.Steps.AddRange(tail.Steps) });
    }
    private async Task<ProjectRuns> Open(HostQuestions? setting = null)
    {
        var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync(), setting ?? Bounded);
        runs.TimeProvider = _clock;
        runs.Follow(_workflow);
        Assert.IsType<StartResult.Started>(runs.Start(Node));
        return runs;
    }
    private static async Task<RequestKey> OpenRequest(IConversationSession session)
    {
        await Until(() => session.Snapshot.Latest?.Requests.Values.Any(request => request is RequestRecord.Question { State: QuestionState.Open }) == true);
        return session.Snapshot.Latest!.Requests.Values.OfType<RequestRecord.Question>().Single().Key;
    }
    private async Task<AttemptRecord> Settled(ProjectRuns runs)
    {
        await Until(() => runs.Latest.GetValueOrDefault(Node.Id) is { Status: not AttemptStatus.Running } && runs.Live(Node.Id) is null);
        return runs.Latest[Node.Id];
    }
    private AttemptEvent[] Events(AttemptRecord record) => [.. AttemptLog.Read(AttemptLog.FolderOf(DataFolder.Attempts(_project), record.Task, record.Id))];
    private int AnswerFrames() => File.Exists(FileAt("frames.jsonl")) ? File.ReadLines(FileAt("frames.jsonl"))
        .Count(line => line.Contains("\"behavior\":\"allow\"", StringComparison.Ordinal)) : 0;
    private static string AnswerText(RequestRecord.Question request) => request.State switch
    {
        QuestionState.AnswerRecorded answer => answer.Reply.Answers.Single().OptionIds.Single() == "o:0" ? "Local" : "Remote",
        QuestionState.Closed { RecordedReply: { } reply } => reply.Answers.Single().OptionIds.Single() == "o:0" ? "Local" : "Remote",
        _ => "No answer",
    };
    private FakeRule InterruptAndEnd() => FakeRule.On().WaitForLine("\"subtype\":\"interrupt\"").Print(Aborted);

    [Fact]
    public async Task Answer_twice_before_expiry()
    {
        InstallQuestion(FakeRule.On().ReadLine(FileAt("answer.json")).WaitForFile(FileAt("go")).Print(ReplyLines(ClientId.ClaudeCode, "Done"))
            .WaitForStdinEnd());
        await using var runs = await Open();
        using var session = runs.OpenConversation(Node.Id);
        var key = await OpenRequest(session);
        var first = await session.AnswerAsync(key, Local, default);
        var second = await session.AnswerAsync(key, new QuestionsReply([new QuestionAnswer("q:0", ["o:0"], null)]), default);
        Assert.Equal([AnswerOutcome.Recorded, AnswerOutcome.AlreadyRecorded], new[] { first.Outcome, second.Outcome });
        File.WriteAllText(FileAt("go"), "go");
        var record = await Settled(runs);
        Assert.Single(Events(record).OfType<AttemptEvent.RequestAnswered>());
        Assert.Equal(1, AnswerFrames());
        using var wire = JsonDocument.Parse(File.ReadAllText(FileAt("answer.json")));
        Assert.Equal("Local", wire.RootElement.GetProperty("response").GetProperty("response").GetProperty("updatedInput").GetProperty("answers").GetProperty("Fixture?").GetString());
        Assert.Contains("--permission-prompt-tool", File.ReadAllText(FileAt("launch.json")));
    }

    [Fact]
    public async Task Expiry_wins_then_answer()
    {
        InstallQuestion(InterruptAndEnd());
        await using var runs = await Open();
        using var session = runs.OpenConversation(Node.Id);
        var key = await OpenRequest(session);
        _clock.Advance(TimeSpan.FromSeconds(55), fireTimers: false);
        Assert.Equal(AnswerOutcome.Stale, (await session.AnswerAsync(key, Local, default)).Outcome);
        var record = await Settled(runs);
        Assert.Equal(new QuestionState.Closed(RequestCloseReason.Deferred, null), Assert.IsType<RequestRecord.Question>(record.Requests[key]).State);
        Assert.Equal(0, AnswerFrames());
        Assert.Equal(AttemptStatus.WaitingForInput, record.Status);
    }

    [Fact]
    public async Task Answer_commits_before_expiry()
    {
        InstallQuestion(FakeRule.On().ReadLine(FileAt("answer.json")).WaitForFile(FileAt("go")).Print(ReplyLines(ClientId.ClaudeCode, "Done")));
        await using var runs = await Open();
        using var session = runs.OpenConversation(Node.Id);
        var key = await OpenRequest(session);
        _clock.Advance(TimeSpan.FromSeconds(54));
        Assert.Equal(AnswerOutcome.Recorded, (await session.AnswerAsync(key, Local, default)).Outcome);
        _clock.Advance(TimeSpan.FromSeconds(1));
        File.WriteAllText(FileAt("go"), "go");
        var record = await Settled(runs);
        Assert.Equal("Local", AnswerText(Assert.IsType<RequestRecord.Question>(record.Requests[key])));
        Assert.Empty(Events(record).OfType<AttemptEvent.RequestDeferred>());
        Assert.Equal(1, AnswerFrames());
    }

    [Fact]
    public async Task Answer_pipe_fails_after_logging()
    {
        Install(_fakes, ClientId.ClaudeCode, Fresh(ClientId.ClaudeCode).RecordFrames(FileAt("frames.jsonl"))
            .Print(SessionLine(ClientId.ClaudeCode, Session)).CloseStdin().Print(Question).Hang());
        var clients = await _fakes.DiscoverAsync();
        await using (var runs = ProjectRuns.Open(_project, clients, Bounded))
        {
            runs.TimeProvider = _clock;
            runs.Follow(_workflow);
            runs.Start(Node);
            using var session = runs.OpenConversation(Node.Id);
            var key = await OpenRequest(session);
            Assert.Equal(AnswerOutcome.DeliveryUnknown, (await session.AnswerAsync(key, Local, default)).Outcome);
            var record = await Settled(runs);
            var request = Assert.IsType<RequestRecord.Question>(record.Requests[key]);
            Assert.Equal("Local", AnswerText(request));
            Assert.Equal(RequestCloseReason.DeliveryUnknown, Assert.IsType<QuestionState.Closed>(request.State).Reason);
            Assert.Single(Events(record).OfType<AttemptEvent.RequestAnswered>());
        }
        await using var reopened = ProjectRuns.Open(_project, clients, Bounded);
        Assert.Equal("Local", AnswerText(reopened.Latest[Node.Id].Requests.Values.OfType<RequestRecord.Question>().Single()));
        Assert.Equal(0, AnswerFrames());
        Assert.Equal(AttemptStatus.Failed, reopened.Latest[Node.Id].Status);
    }

    [Fact]
    public async Task Two_questions_expire_with_queued_Also_add_tests()
    {
        InstallQuestion(FakeRule.On().WaitForFile(FileAt("second")).Print(Question.Replace("q1", "q2", StringComparison.Ordinal))
            .WaitForLine("\"subtype\":\"interrupt\"").Print(Aborted));
        await using var runs = await Open();
        using var session = runs.OpenConversation(Node.Id);
        var first = await OpenRequest(session);
        _clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(new SendResult.Queued(), await session.SendAsync(first.Turn, "Also add tests", false, default));
        File.WriteAllText(FileAt("second"), "go");
        await Until(() => session.Snapshot.Latest!.Requests.Count == 2);
        Assert.Equal([new RequestDeadline(new DateTimeOffset(2026, 10, 7, 12, 0, 55, TimeSpan.Zero), new DateTimeOffset(2026, 10, 7, 12, 1, 0, TimeSpan.Zero)),
            new RequestDeadline(new DateTimeOffset(2026, 10, 7, 12, 0, 55, TimeSpan.Zero), new DateTimeOffset(2026, 10, 7, 12, 1, 0, TimeSpan.Zero))],
            session.Snapshot.Latest!.Requests.Values.OfType<RequestRecord.Question>().OrderBy(request => request.Key.Id).Select(request => Assert.IsType<QuestionState.Open>(request.State).Deadline));
        _clock.Advance(TimeSpan.FromSeconds(35));
        var record = await Settled(runs);
        Assert.Equal([RequestCloseReason.Deferred, RequestCloseReason.Deferred], record.Requests.Values.OfType<RequestRecord.Question>()
            .Select(request => Assert.IsType<QuestionState.Closed>(request.State).Reason));
        Assert.Equal(["Also add tests"], record.Queued.Select(message => message.Text));
        Assert.Single(record.Turns);
        Assert.Empty(Events(record).OfType<AttemptEvent.TurnRequested>());
        var deferred = Assert.Single(Events(record).OfType<AttemptEvent.RequestDeferred>());
        Assert.Equal(["s:q1", "s:q2"], deferred.RequestIds.ToArray());
        Assert.Equal("Fixture?\nLocal\nRemote\n\nFixture?\nLocal\nRemote", deferred.Question);
        Assert.Equal(0, AnswerFrames());
    }

    [Fact]
    public async Task Confirmed_deferred_waiting()
    {
        InstallQuestion(InterruptAndEnd());
        await using var runs = await Open();
        using var session = runs.OpenConversation(Node.Id);
        await OpenRequest(session);
        var pid = runs.Latest[Node.Id].Process!.Value.Id;
        _clock.Advance(TimeSpan.FromSeconds(55));
        var record = await Settled(runs);
        Assert.Equal((AttemptStatus.WaitingForInput, TurnOutcome.Deferred, "Fixture?\nLocal\nRemote"),
            (record.Status, record.Turns.Single().Outcome, Assert.IsType<IDevelop.Nodes.Pending.Question>(record.Pending).Text));
        Assert.Equal(0, LiveProcesses(pid));
        await using var second = ProjectRuns.Open(_project, await _fakes.DiscoverAsync(), Bounded);
        Assert.IsType<TerminalResult.HandedOff>(second.OpenInTerminal(Node.Id));
        Assert.True(session.Snapshot.Actions.Send.Enabled);
    }

    [Fact]
    public async Task Explicit_reply_Use_the_fixture_after_deferral()
    {
        var a = new AttemptId(Guid.Parse("019a9d2e-1111-7000-8000-000000000001"));
        var at = _clock.GetUtcNow();
        var start = new AttemptEvent.Requested(at, a, Node.Id, "Build", Settings, "Build it", "claude", []) { Conversation = ConversationMode.MayAsk };
        using (var log = AttemptLog.Create(DataFolder.Attempts(_project), start))
        {
            log.Append(new AttemptEvent.Agent(at, new AgentEvent.SessionStarted(Session)));
            log.Append(new AttemptEvent.QuestionRecorded(at, "s:q1", [new AskedQuestion("q:0", "Fixture", "Fixture?", [new QuestionOption("o:0", "Local", null)], false, true)],
                new QuestionState.Open(new RequestDeadline(at, at + TimeSpan.FromSeconds(5)))));
            log.Append(new AttemptEvent.MessageQueued(at, "Also add tests", false) { Id = "queued-1" });
            log.Append(new AttemptEvent.RequestDeferred(at, ["s:q1"], "Fixture?\nLocal"));
            log.Append(new AttemptEvent.Exited(at, 0, ""));
        }
        Install(_fakes, ClientId.ClaudeCode, Resuming(ClientId.ClaudeCode, Session).RecordArguments(FileAt("launch.json"))
            .CapturePrompt(FileAt("prompt")).Print(SessionLine(ClientId.ClaudeCode, Session)).Print(ReplyLines(ClientId.ClaudeCode, "Done")));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync(), Bounded);
        runs.Follow(_workflow);
        using var session = runs.OpenConversation(Node.Id);
        Assert.IsType<SendResult.Answered>(await session.SendAsync(new TurnKey(a, 1), "Use the fixture", false, default));
        var record = await Settled(runs);
        Assert.Equal((a, 2, Session, "Also add tests\n\nUse the fixture"), (record.Id, record.Turns.Count, record.SessionId, record.Turns[1].Message));
        Assert.Equal("Also add tests\n\nUse the fixture", File.ReadAllText(FileAt("prompt")));
        Assert.Equal([new TurnRequestId(1, "s:q1")], record.Turns[1].Replies.ToArray());
        var events = Events(record);
        var queued = events.OfType<AttemptEvent.MessageQueued>().ToArray();
        var requested = Assert.Single(events.OfType<AttemptEvent.TurnRequested>());
        Assert.Equal(["Also add tests", "Use the fixture"], queued.Select(message => message.Text));
        Assert.Equal(queued.Select(message => message.Id), requested.Consumed);
        Assert.True(Array.IndexOf(events, queued[1]) < Array.IndexOf(events, requested));
        var page = Assert.IsType<HistoryResult.Page>(await session.ReadPageAsync(a, new HistoryQuery.Latest(), 50, default));
        Assert.Equal([("Also add tests", MessageState.Submitted), ("Use the fixture", MessageState.Submitted)], page.Entries
            .Select(entry => entry.Content).OfType<ConversationContent.Message>().Where(message => message.Author == MessageAuthor.Person).Select(message => (message.Text, message.State)));
        Assert.Equal(RequestCloseReason.Deferred, Assert.IsType<QuestionState.Closed>(Assert.IsType<RequestRecord.Question>(await session.ReadRequestAsync(new RequestKey(new TurnKey(a, 1), "s:q1"), default)).State).Reason);
    }

    [Fact]
    public async Task Cancel_before_deferred_settlement()
    {
        InstallQuestion(FakeRule.On().WaitForLine("\"subtype\":\"interrupt\"").Write(FileAt("interrupted"), "yes").WaitForFile(FileAt("go")).Print(Aborted));
        await using var runs = await Open();
        using var session = runs.OpenConversation(Node.Id);
        var key = await OpenRequest(session);
        _clock.Advance(TimeSpan.FromSeconds(55));
        await Until(() => File.Exists(FileAt("interrupted")));
        Assert.Equal(CommandOutcome.Applied, (await session.CancelAsync(key.Turn, default)).Outcome);
        File.WriteAllText(FileAt("go"), "go");
        var record = await Settled(runs);
        Assert.Equal(AttemptStatus.Cancelled, record.Status);
        Assert.Single(record.Turns);
        Assert.Empty(Events(record).OfType<AttemptEvent.TurnRequested>());
    }

    [Fact]
    public async Task DeferImmediately_has_the_same_waiting_result_without_an_answerable_open_request()
    {
        InstallQuestion(InterruptAndEnd());
        await using var runs = await Open(new HostQuestions.DeferImmediately());
        using var session = runs.OpenConversation(Node.Id);
        var openSeen = 0;
        session.Changed += _ =>
        {
            if (session.Snapshot.Latest?.Requests.Values.Any(request => request is RequestRecord.Question { State: QuestionState.Open }) == true)
                Interlocked.Increment(ref openSeen);
        };
        var record = await Settled(runs);
        Assert.Equal((AttemptStatus.WaitingForInput, TurnOutcome.Deferred), (record.Status, record.Turns.Single().Outcome));
        Assert.Equal(0, openSeen);
        Assert.IsType<QuestionState.Closed>(Assert.Single(Events(record).OfType<AttemptEvent.QuestionRecorded>()).State);
        Assert.Equal(RequestCloseReason.Deferred, Assert.IsType<QuestionState.Closed>(record.Requests.Values.OfType<RequestRecord.Question>().Single().State).Reason);
    }

    [Fact]
    public async Task Answer_from_project_A_while_B_is_selected()
    {
        InstallQuestion(FakeRule.On().ReadLine(FileAt("answer.json")).Print(ReplyLines(ClientId.ClaudeCode, "Done")));
        var a = await Open();
        using var sessionA = a.OpenConversation(Node.Id);
        var key = await OpenRequest(sessionA);
        await using var b = ProjectRuns.Open(_temp.Create("B"), await _fakes.DiscoverAsync(), Bounded);
        b.Follow(Workflow.Empty(WorkflowId.New()).Must(TestNodes.Place(Node, new CanvasPoint(0, 0))));
        using var selected = b.OpenConversation(Node.Id);
        Assert.Equal(Node.Id, selected.Task);
        Assert.Equal(AnswerOutcome.Recorded, (await sessionA.AnswerAsync(key, Local, default)).Outcome);
        Assert.Equal("Local", AnswerText((await Settled(a)).Requests.Values.OfType<RequestRecord.Question>().Single()));
        Assert.Equal(0, b.Latest.Values.Sum(record => record.Requests.Values.Count(request => request is RequestRecord.Question { State: QuestionState.AnswerRecorded })));
        await a.DisposeAsync();
        Assert.Equal(AnswerOutcome.Unavailable, (await sessionA.AnswerAsync(key, Local, default)).Outcome);
        Assert.Equal(new SendResult.Refused(new SendProblem.ClosedOwner()), await sessionA.SendAsync(key.Turn, "Later", false, default));
    }

    [Fact]
    public async Task Latest_node_configuration_changes_before_Send()
    {
        InstallQuestion(FakeRule.On().WaitForLine("\"subtype\":\"interrupt\"").Print(Aborted));
        await using var runs = await Open();
        using var session = runs.OpenConversation(Node.Id);
        var key = await OpenRequest(session);
        var changed = Node with { Execution = new ExecutionSettings(ClientId.Codex) { Model = "gpt-6-sol", Reasoning = "high" } };
        runs.Follow(_workflow.Must(new WorkflowEdit.SetExecution(Node.Id, changed.Execution)));
        Assert.Equal(new SendResult.Refused(new SendProblem.ClientChanged(ClientId.ClaudeCode, ClientId.Codex)), await session.SendAsync(key.Turn, "Use Codex", false, default));
        Assert.False(session.Snapshot.Actions.Send.Enabled);
        Assert.Single(session.Snapshot.Latest!.Turns);
        Assert.Empty(Events(session.Snapshot.Latest).OfType<AttemptEvent.TurnRequested>());
        await session.CancelAsync(key.Turn, default);
        await Settled(runs);
    }

    [Fact]
    public async Task Snapshot_during_deferral_teardown_never_enables_Send_after_release_it_does()
    {
        InstallQuestion(FakeRule.On().WaitForLine("\"subtype\":\"interrupt\"").Write(FileAt("interrupted"), "yes").WaitForFile(FileAt("go")).Print(Aborted));
        await using var runs = await Open();
        using var session = runs.OpenConversation(Node.Id);
        var key = await OpenRequest(session);
        _clock.Advance(TimeSpan.FromSeconds(55));
        await Until(() => File.Exists(FileAt("interrupted")));
        Assert.Equal((true, false, false), (session.Snapshot.Latest!.Stopping, session.Snapshot.Actions.Send.Enabled, session.Snapshot.Actions.MarkDone.Enabled));
        Assert.Equal(new SendResult.Refused(new SendProblem.Ending("Build")), await session.SendAsync(key.Turn, "Too soon", false, default));
        File.WriteAllText(FileAt("go"), "go");
        var record = await Settled(runs);
        Assert.Equal((AttemptStatus.WaitingForInput, true, true), (record.Status, session.Snapshot.Actions.Send.Enabled, session.Snapshot.Actions.MarkDone.Enabled));
    }

    [Fact]
    public async Task Session_reads_live_text_and_notifies_without_changing_the_log_revision()
    {
        Install(_fakes, ClientId.ClaudeCode, Fresh(ClientId.ClaudeCode).Print(SessionLine(ClientId.ClaudeCode, Session))
            .WaitForFile(FileAt("delta"))
            .Print("""{"type":"stream_event","event":{"type":"message_start","message":{"id":"m1"}}}""")
            .Print("""{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hel"}}}""")
            .WaitForFile(FileAt("go"))
            .Print("""{"type":"assistant","message":{"id":"m1","content":[{"type":"text","text":"Hello"}]}}""")
            .Print(ReplyLines(ClientId.ClaudeCode, "Hello")));
        await using var runs = await Open();
        using var session = runs.OpenConversation(Node.Id);
        await Until(() => session.Snapshot.Latest?.SessionId == Session);
        var before = session.Snapshot;
        var notifiedOnWorker = false;
        var notified = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Changed += revision =>
        {
            if (runs.Live(Node.Id)?.Buffers.GetValueOrDefault("m1:0")?.Text == "Hel")
            {
                notifiedOnWorker = Thread.CurrentThread.IsThreadPoolThread;
                notified.TrySetResult(revision);
            }
        };
        File.WriteAllText(FileAt("delta"), "go");
        await notified.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var streaming = session.Snapshot;
        Assert.True(streaming.Revision > before.Revision);
        Assert.Equal(before.LogRevision, streaming.LogRevision);
        Assert.True(notifiedOnWorker);
        var page = Assert.IsType<HistoryResult.Page>(await session.ReadPageAsync(streaming.Current!.Value.Attempt, new HistoryQuery.Latest(), 50, default));
        var row = page.Entries.Single(entry => entry.Content is ConversationContent.Message { Author: MessageAuthor.Agent });
        Assert.Equal(new ConversationContent.Message(MessageAuthor.Agent, "Hel", MessageState.Streaming), row.Content);
        File.WriteAllText(FileAt("go"), "go");
        var record = await Settled(runs);
        var refreshed = Assert.IsType<HistoryResult.Page>(await session.ReadPageAsync(record.Id, new HistoryQuery.RefreshWindow(page.Window), 50, default));
        Assert.Equal((row.Id, new ConversationContent.Message(MessageAuthor.Agent, "Hello", MessageState.Complete)),
            (refreshed.Entries.Single(entry => entry.Id == row.Id).Id, refreshed.Entries.Single(entry => entry.Id == row.Id).Content));
        Assert.Equal([record.Id], (await session.ListAttemptsAsync(default)).Select(attempt => attempt.Id));
        Assert.IsType<HistoryResult.Unavailable>(await session.ReadPageAsync(record.Id, new HistoryQuery.After(new HistoryCursor("invalid")), 50, default));
    }

    [Fact]
    public async Task Session_checks_stale_missing_and_closed_targets_and_disposal_never_stops_execution()
    {
        InstallQuestion(FakeRule.On().ReadLine(FileAt("answer.json")).WaitForFile(FileAt("go")).Print(ReplyLines(ClientId.ClaudeCode, "Done")));
        await using var runs = await Open();
        using var session = runs.OpenConversation(Node.Id);
        var key = await OpenRequest(session);
        var stale = key.Turn with { Number = 9 };
        Assert.Equal(new SendResult.Refused(new SendProblem.StaleTarget()), await session.SendAsync(stale, "Old reply", false, default));
        Assert.Equal(CommandOutcome.Stale, (await session.CancelAsync(stale, default)).Outcome);
        Assert.Equal(CommandOutcome.Stale, (await session.MarkDoneAsync(stale, default)).Outcome);
        Assert.Equal(new TerminalResult.Refused(new TerminalProblem.StaleTarget()), await session.OpenInTerminalAsync(stale, default));
        Assert.Equal(AnswerOutcome.Stale, (await session.AnswerAsync(key with { Turn = stale }, Local, default)).Outcome);
        using (var subscription = runs.OpenConversation(Node.Id)) subscription.Dispose();
        Assert.Equal(AttemptStatus.Running, session.Snapshot.Latest!.Status);
        runs.Follow(_workflow.Must(new WorkflowEdit.Delete([Node.Id], [])));
        Assert.IsType<SendProblem.MissingTask>(runs.CheckSend(Node));
        Assert.Equal(new SendResult.Refused(new SendProblem.MissingTask()), await runs.SendAsync(Node, "No task", false));
        Assert.Equal(new SendResult.Refused(new SendProblem.MissingTask()), await session.SendAsync(key.Turn, "No task", false, default));
        Assert.Equal(CommandOutcome.Unavailable, (await session.MarkDoneAsync(key.Turn, default)).Outcome);
        Assert.Equal(AnswerOutcome.Recorded, (await session.AnswerAsync(key, Local, default)).Outcome);
        File.WriteAllText(FileAt("go"), "go");
        var record = await Settled(runs);
        Assert.Equal("Local", AnswerText(Assert.IsType<RequestRecord.Question>(await session.ReadRequestAsync(key, default))));
        Assert.IsType<HistoryResult.Page>(await session.ReadPageAsync(record.Id, new HistoryQuery.AroundRequest(key), 20, default));
        await runs.DisposeAsync();
        Assert.Equal(AnswerOutcome.Unavailable, (await session.AnswerAsync(key, Local, default)).Outcome);
        Assert.Equal(CommandOutcome.Unavailable, (await session.CancelAsync(key.Turn, default)).Outcome);
        Assert.Equal(CommandOutcome.Unavailable, (await session.MarkDoneAsync(key.Turn, default)).Outcome);
        Assert.Equal(new TerminalResult.Refused(new TerminalProblem.ClosedOwner()), await session.OpenInTerminalAsync(key.Turn, default));
        Assert.IsType<HistoryResult.Unavailable>(await session.ReadPageAsync(record.Id, new HistoryQuery.Latest(), 20, default));
        Assert.Null(await session.ReadRequestAsync(key, default));
        Assert.Empty(await session.ListAttemptsAsync(default));
        Assert.False(session.Snapshot.Actions.Send.Enabled);
    }

    [Fact]
    public async Task Answer_validation_refuses_unknown_questions_options_cardinality_and_conflicting_replies()
    {
        InstallQuestion(FakeRule.On().ReadLine(FileAt("answer.json")).WaitForFile(FileAt("go")).Print(ReplyLines(ClientId.ClaudeCode, "Done")));
        await using var runs = await Open();
        using var session = runs.OpenConversation(Node.Id);
        var key = await OpenRequest(session);
        foreach (var reply in new[]
        {
            new QuestionsReply([new QuestionAnswer("unknown", ["o:0"], null)]),
            new QuestionsReply([new QuestionAnswer("q:0", ["unknown"], null)]),
            new QuestionsReply([new QuestionAnswer("q:0", ["o:0", "o:1"], null)]),
            new QuestionsReply([new QuestionAnswer("q:0", ["o:0", "o:0"], null)]),
            new QuestionsReply([]),
        }) Assert.Equal(AnswerOutcome.Invalid, (await session.AnswerAsync(key, reply, default)).Outcome);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.AnswerAsync(key, Local, cancellation.Token));
        Assert.Equal(AnswerOutcome.Recorded, (await session.AnswerAsync(key, Local, default)).Outcome);
        Assert.Equal(AnswerOutcome.Stale, (await session.AnswerAsync(key, new QuestionsReply([new QuestionAnswer("q:0", ["o:1"], null)]), default)).Outcome);
        File.WriteAllText(FileAt("go"), "go");
        var record = await Settled(runs);
        Assert.Equal("Local", AnswerText(Assert.IsType<RequestRecord.Question>(record.Requests[key])));
        Assert.Single(Events(record).OfType<AttemptEvent.RequestAnswered>());
    }

    [Fact]
    public async Task Deferred_waiting_session_supports_terminal_and_mark_done_with_current_turn_checks()
    {
        InstallQuestion(InterruptAndEnd());
        await using var runs = await Open();
        using var session = runs.OpenConversation(Node.Id);
        var key = await OpenRequest(session);
        _clock.Advance(TimeSpan.FromSeconds(55));
        await Settled(runs);
        Assert.IsType<TerminalResult.HandedOff>(await session.OpenInTerminalAsync(key.Turn, default));
        Assert.Equal(CommandOutcome.Applied, (await session.MarkDoneAsync(key.Turn, default)).Outcome);
        Assert.Equal((AttemptStatus.Succeeded, true), (session.Snapshot.Latest!.Status, session.Snapshot.Latest.Terminal is not null));
        Assert.Equal(CommandOutcome.Refused, (await session.MarkDoneAsync(key.Turn, default)).Outcome);
    }

    [Fact]
    public async Task Expiry_force_stops_a_client_that_ignores_interrupt_at_the_host_stop_deadline()
    {
        InstallQuestion(FakeRule.On().WaitForLine("\"subtype\":\"interrupt\"").Write(FileAt("interrupted"), "yes").Hang());
        await using var runs = await Open();
        using var session = runs.OpenConversation(Node.Id);
        await OpenRequest(session);
        _clock.Advance(TimeSpan.FromSeconds(55));
        await Until(() => File.Exists(FileAt("interrupted")));
        Assert.Equal((AttemptStatus.Running, false), (session.Snapshot.Latest!.Status, session.Snapshot.Actions.Send.Enabled));
        _clock.Advance(TimeSpan.FromSeconds(5));
        var record = await Settled(runs);
        Assert.Equal((AttemptStatus.WaitingForInput, TurnOutcome.Deferred), (record.Status, record.Turns.Single().Outcome));
    }

    [Fact]
    public async Task Withdrawing_an_unaccepted_cancel_keeps_the_run_open_to_messages()
    {
        InstallQuestion(FakeRule.On().ReadLine(FileAt("answer.json")).WaitForLine("\"subtype\":\"interrupt\"").Print(Aborted));
        await using var runs = await Open();
        using var session = runs.OpenConversation(Node.Id);
        var key = await OpenRequest(session);
        using var release = new ManualResetEventSlim();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void HoldDrain(object? sender, EventArgs args)
        {
            if (runs.Latest[Node.Id].Queued is [{ Text: "Hold drain" }])
            {
                held.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(15));
            }
        }
        runs.Changed += HoldDrain;
        var send = session.SendAsync(key.Turn, "Hold drain", false, default);
        try
        {
            await held.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(send.IsCompleted);
            using var cancellation = new CancellationTokenSource();
            var cancel = session.CancelAsync(key.Turn, cancellation.Token);
            cancellation.Cancel();
            release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancel);
            Assert.Equal(new SendResult.Queued(), await send);
        }
        finally
        {
            release.Set();
            runs.Changed -= HoldDrain;
        }
        Assert.Equal(new SendResult.Queued(), await session.SendAsync(key.Turn, "Still open", false, default));
        Assert.Equal(["Hold drain", "Still open"], session.Snapshot.Latest!.Queued.Select(message => message.Text));
        Assert.Empty(Events(session.Snapshot.Latest).OfType<AttemptEvent.CancelRequested>());
        Assert.Equal(AnswerOutcome.Recorded, (await session.AnswerAsync(key, Local, default)).Outcome);
        await session.CancelAsync(key.Turn, default);
        await Settled(runs);
    }

    private static int LiveProcesses(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.HasExited ? 0 : 1; }
        catch (ArgumentException) { return 0; }
    }
    private static async Task Until(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, "The conversation did not reach the expected state.");
            await Task.Delay(10);
        }
    }
}
