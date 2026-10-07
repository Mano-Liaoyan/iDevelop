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
    public async Task A_waiting_reply_uses_the_logged_deferral_ids_in_presentation_order()
    {
        var attempt = AttemptEvents.First;
        using (var log = AttemptLog.Create(DataFolder.Attempts(_project), AttemptEvents.BuildRequested(attempt) with
        {
            Settings = Settings,
            Conversation = ConversationMode.MayAsk,
        }))
        {
            log.Append(AttemptEvents.Said(1, new AgentEvent.SessionStarted(Session)));
            foreach (var id in new[] { "s:z", "s:a" })
            {
                log.Append(new AttemptEvent.QuestionRecorded(AttemptEvents.T0.AddSeconds(2), id,
                    [new AskedQuestion("q:0", "Fixture", "Fixture?", [], false, true)],
                    new QuestionState.Closed(RequestCloseReason.Deferred, null)));
            }

            log.Append(new AttemptEvent.RequestDeferred(AttemptEvents.T0.AddSeconds(3), ["s:z", "s:a"], "Fixture?"));
            log.Append(new AttemptEvent.QuestionRecorded(AttemptEvents.T0.AddSeconds(4), "s:b",
                [new AskedQuestion("q:0", "Late", "Late?", [], false, true)],
                new QuestionState.Closed(RequestCloseReason.Deferred, null)));
            log.Append(AttemptEvents.Exit(5, 0));
        }

        Install(_fakes, ClientId.ClaudeCode, Resuming(ClientId.ClaudeCode, Session)
            .CapturePrompt(FileAt("prompt.txt")).Print(ReplyLines(ClientId.ClaudeCode, "Done")));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync(), new HostQuestions.DeferImmediately());
        runs.Follow(_workflow);
        using var session = runs.OpenConversation(Node.Id);
        var turn = new TurnKey(attempt, 1);
        Assert.Equal(AttemptStatus.WaitingForInput, session.Snapshot.Latest!.Status);
        Assert.Equal(new QuestionState.Closed(RequestCloseReason.Deferred, null),
            Assert.IsType<RequestRecord.Question>(await session.ReadRequestAsync(new RequestKey(turn, "s:b"), default)).State);
        Assert.IsType<SendResult.Answered>(await session.SendAsync(turn, "Use Local", false, default));
        var record = await Settled(runs);
        Assert.Equal("Use Local", File.ReadAllText(FileAt("prompt.txt")));
        Assert.Equal([new TurnRequestId(1, "s:z"), new TurnRequestId(1, "s:a")], record.Turns[1].Replies.ToArray());
    }

    [Fact]
    public async Task The_leave_budget_starts_before_a_stop_waits_for_the_drain_acknowledgement()
    {
        InstallQuestion(FakeRule.On().Hang());
        var runs = await Open();
        runs.ShutdownTime = TimeSpan.FromSeconds(60);
        using var session = runs.OpenConversation(Node.Id);
        var key = await OpenRequest(session);
        using var client = Process.GetProcessById(runs.Latest[Node.Id].Process!.Value.Id);
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
            await held.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var disposal = runs.DisposeAsync().AsTask();
            _clock.Advance(TimeSpan.FromSeconds(10));
            release.Set();
            await disposal.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(new SendResult.Queued(), await send);
            await client.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(new[] { "The project was closed while this task ran." },
                Events(runs.Latest[Node.Id]).OfType<AttemptEvent.InterruptRequested>().Select(interrupt => interrupt.Reason));
            await using var reopened = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
            Assert.Equal(AttemptStatus.Interrupted, reopened.Latest[Node.Id].Status);
            Assert.Equal("The project was closed while this task ran. Its client did not stop in time, and iDevelop settled it when the project was opened again.",
                reopened.Latest[Node.Id].Detail);
        }
        finally
        {
            release.Set();
            runs.Changed -= HoldDrain;
            _clock.Advance(TimeSpan.FromSeconds(60));
            await runs.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_late_question_during_immediate_deferral_is_denied_and_only_the_shown_question_receives_a_reply()
    {
        InstallQuestion(FakeRule.On().WaitForLine("\"subtype\":\"interrupt\"")
            .Print(Question.Replace("q1", "q2", StringComparison.Ordinal)).ReadLine(FileAt("decline.json"))
            .Write(FileAt("declined"), "yes").WaitForFile(FileAt("go")).Print(Aborted));
        await using var runs = await Open(new HostQuestions.DeferImmediately());
        using var session = runs.OpenConversation(Node.Id);
        try
        {
            await Until(() => File.Exists(FileAt("declined")));
            var turn = session.Snapshot.Current!.Value;
            var late = new RequestKey(turn, "s:q2");
            var state = Assert.IsType<RequestRecord.Question>(await session.ReadRequestAsync(late, default)).State;
            using var wire = JsonDocument.Parse(File.ReadAllText(FileAt("decline.json")));
            Assert.Equal("deny", wire.RootElement.GetProperty("response").GetProperty("response").GetProperty("behavior").GetString());
            File.WriteAllText(FileAt("go"), "go");
            Assert.Equal(AttemptStatus.WaitingForInput, (await Settled(runs)).Status);
            Install(_fakes, ClientId.ClaudeCode, Resuming(ClientId.ClaudeCode, Session).CapturePrompt(FileAt("prompt.txt"))
                .Print(ReplyLines(ClientId.ClaudeCode, "Done")));
            Assert.IsType<SendResult.Answered>(await session.SendAsync(turn, "Use Local", false, default));
            var record = await Settled(runs);
            Assert.Equal("Use Local", File.ReadAllText(FileAt("prompt.txt")));
            Assert.Equal([new TurnRequestId(1, "s:q1")], record.Turns[1].Replies.ToArray());
            Assert.Equal(new QuestionState.Closed(RequestCloseReason.PolicyDenied, null), state);
        }
        finally
        {
            File.WriteAllText(FileAt("go"), "go");
            _clock.Advance(TimeSpan.FromSeconds(60));
        }
    }

    [Fact]
    public async Task A_host_detected_Claude_protocol_failure_has_one_failure_marker()
    {
        Install(_fakes, ClientId.ClaudeCode, Fresh(ClientId.ClaudeCode)
            .Print(SessionLine(ClientId.ClaudeCode, Session))
            .Print("""{"type":"control_request","request":{"subtype":"can_use_tool"}}""").Hang());
        await using var runs = await Open();
        using var session = runs.OpenConversation(Node.Id);
        var record = await Settled(runs);
        var page = Assert.IsType<HistoryResult.Page>(await session.ReadPageAsync(record.Id, new HistoryQuery.Latest(), 50, default));
        Assert.Equal(AttemptStatus.Failed, record.Status);
        Assert.Equal(new[] { ("failure", "Claude Code supplied no request id.") }, page.Entries.Select(entry => entry.Content)
            .OfType<ConversationContent.Marker>().Where(marker => marker.Kind == "failure").Select(marker => (marker.Kind, marker.Text)));
    }

    [Fact]
    public async Task A_deferred_Claude_turn_has_only_attempt_configuration_and_deferral_markers()
    {
        InstallQuestion(InterruptAndEnd());
        await using var runs = await Open(new HostQuestions.DeferImmediately());
        using var session = runs.OpenConversation(Node.Id);
        var record = await Settled(runs);
        var page = Assert.IsType<HistoryResult.Page>(await session.ReadPageAsync(record.Id, new HistoryQuery.Latest(), 50, default));
        Assert.Equal(AttemptStatus.WaitingForInput, record.Status);
        Assert.Equal(new[]
        {
            ("attempt", $"Attempt {record.Id}\nClaude Code, model claude-haiku-4-5, reasoning high, conversation MayAsk."),
            ("configuration", "Client reported model claude-haiku-4-5, reasoning unspecified."),
            ("deferred", "Fixture?\nLocal\nRemote"),
        }, page.Entries.Select(entry => entry.Content).OfType<ConversationContent.Marker>().Select(marker => (marker.Kind, marker.Text)));
        var failedAttempt = AttemptEvents.First;
        using (var log = AttemptLog.Create(DataFolder.Attempts(_project), AttemptEvents.BuildRequested(failedAttempt)))
        {
            log.Append(AttemptEvents.Said(1, new AgentEvent.Failed("The client could not execute.")));
            log.Append(AttemptEvents.Exit(2, 1));
        }

        var failedPage = Assert.IsType<HistoryResult.Page>(await session.ReadPageAsync(failedAttempt, new HistoryQuery.Latest(), 10, default));
        Assert.Equal(AttemptStatus.Failed, (await session.ListAttemptsAsync(default)).Single(attempt => attempt.Id == failedAttempt).Status);
        Assert.Equal(new[] { ("failure", "The client could not execute.") }, failedPage.Entries.Select(entry => entry.Content)
            .OfType<ConversationContent.Marker>().Where(marker => marker.Kind == "failure").Select(marker => (marker.Kind, marker.Text)));
    }

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
    public async Task Stop_and_send_closes_the_question_before_a_later_answer_and_runs_the_queued_prompt()
    {
        InstallQuestion(FakeRule.On().WaitForLine("\"subtype\":\"interrupt\"").Write(FileAt("interrupted"), "yes")
            .WaitForFile(FileAt("go")).Print(Aborted));
        await using var runs = await Open();
        using var session = runs.OpenConversation(Node.Id);
        var key = await OpenRequest(session);
        try
        {
            _clock.Advance(TimeSpan.FromSeconds(54));
            Assert.Equal(new SendResult.Queued(), await session.SendAsync(key.Turn, "Do it differently", true, default));
            await Until(() => File.Exists(FileAt("interrupted")));
            Assert.Equal(AnswerOutcome.Stale, (await session.AnswerAsync(key, Local, default)).Outcome);
            Assert.Equal(0, AnswerFrames());
            Assert.Equal(new QuestionState.Closed(RequestCloseReason.Stopped, null),
                Assert.IsType<RequestRecord.Question>(await session.ReadRequestAsync(key, default)).State);
            Install(_fakes, ClientId.ClaudeCode, Resuming(ClientId.ClaudeCode, Session)
                .CapturePrompt(FileAt("prompt.txt")).Print(ReplyLines(ClientId.ClaudeCode, "Done")));
            File.WriteAllText(FileAt("go"), "go");
            var record = await Settled(runs);
            Assert.Equal((AttemptStatus.Succeeded, 2, "Do it differently"), (record.Status, record.Turns.Count, record.Turns[1].Message));
            Assert.Equal("Do it differently", File.ReadAllText(FileAt("prompt.txt")));
            Assert.Equal(0, AnswerFrames());
        }
        finally
        {
            File.WriteAllText(FileAt("go"), "go");
            _clock.Advance(TimeSpan.FromSeconds(60));
        }
    }

    [Fact]
    public async Task Stop_and_send_prevents_expiry_from_deferring_the_queued_prompt()
    {
        InstallQuestion(FakeRule.On().WaitForLine("\"subtype\":\"interrupt\"").Write(FileAt("interrupted"), "yes")
            .WaitForFile(FileAt("go")).Print(Aborted));
        await using var runs = await Open();
        using var session = runs.OpenConversation(Node.Id);
        var key = await OpenRequest(session);
        try
        {
            _clock.Advance(TimeSpan.FromSeconds(54));
            Assert.Equal(new SendResult.Queued(), await session.SendAsync(key.Turn, "Do it differently", true, default));
            await Until(() => File.Exists(FileAt("interrupted")));
            _clock.Advance(TimeSpan.FromSeconds(2));
            Assert.Equal(AnswerOutcome.Stale, (await session.AnswerAsync(key, Local, default)).Outcome);
            Assert.Empty(Events(runs.Latest[Node.Id]).OfType<AttemptEvent.RequestDeferred>());
            Install(_fakes, ClientId.ClaudeCode, Resuming(ClientId.ClaudeCode, Session)
                .CapturePrompt(FileAt("prompt.txt")).Print(ReplyLines(ClientId.ClaudeCode, "Done")));
            File.WriteAllText(FileAt("go"), "go");
            var record = await Settled(runs);
            Assert.Equal((AttemptStatus.Succeeded, 2, "Do it differently"), (record.Status, record.Turns.Count, record.Turns[1].Message));
            Assert.Empty(Events(record).OfType<AttemptEvent.RequestDeferred>());
            Assert.Equal("Do it differently", File.ReadAllText(FileAt("prompt.txt")));
        }
        finally
        {
            File.WriteAllText(FileAt("go"), "go");
            _clock.Advance(TimeSpan.FromSeconds(60));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Stopping_mid_question_closes_it_with_the_stop_reason_before_provider_cancellation(bool stopAndSend)
    {
        InstallQuestion(FakeRule.On().WaitForLine("\"subtype\":\"interrupt\"")
            .Print("""{"type":"control_cancel_request","request_id":"q1"}""").Print(Aborted));
        await using var runs = await Open();
        using var session = runs.OpenConversation(Node.Id);
        var key = await OpenRequest(session);
        if (stopAndSend)
        {
            Assert.Equal(new SendResult.Queued(), await session.SendAsync(key.Turn, "Do it differently", true, default));
        }
        else
        {
            Assert.Equal(CommandOutcome.Applied, (await session.CancelAsync(key.Turn, default)).Outcome);
        }

        await Until(() => runs.Latest.GetValueOrDefault(Node.Id) is { } r && r.Requests.TryGetValue(key, out var q)
            && q is RequestRecord.Question { State: QuestionState.Closed });
        await Until(() => Events(runs.Latest[Node.Id]).OfType<AttemptEvent.RequestClosed>()
            .Any(closed => closed.RequestId == key.Id && closed.Reason == RequestCloseReason.Resolved));
        var state = Assert.IsType<RequestRecord.Question>(runs.Latest[Node.Id].Requests[key]).State;
        Assert.Equal(new QuestionState.Closed(stopAndSend ? RequestCloseReason.Stopped : RequestCloseReason.Cancelled, null), state);
        var events = Events(runs.Latest[Node.Id]);
        var intent = Array.FindIndex(events, e => e is AttemptEvent.MessageQueued or AttemptEvent.CancelRequested);
        Assert.Equal(new AttemptEvent.RequestClosed(_clock.GetUtcNow(), "s:q1",
            stopAndSend ? RequestCloseReason.Stopped : RequestCloseReason.Cancelled), events[intent + 1]);
        var page = Assert.IsType<HistoryResult.Page>(await session.ReadPageAsync(key.Turn.Attempt, new HistoryQuery.AroundRequest(key), 50, default));
        Assert.Equal(new ConversationContent.Request(key),
            Assert.Single(page.Entries, entry => entry.Content is ConversationContent.Request request && request.Key == key).Content);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_question_arriving_while_stopping_is_closed_with_the_stop_reason_and_declined(bool stopAndSend)
    {
        InstallQuestion(FakeRule.On().WaitForLine("\"subtype\":\"interrupt\"")
            .Print(Question.Replace("q1", "q2", StringComparison.Ordinal)).ReadLine(FileAt("decline.json"))
            .Write(FileAt("declined"), "yes").WaitForFile(FileAt("go")).Print(Aborted));
        await using var runs = await Open();
        using var session = runs.OpenConversation(Node.Id);
        var key = await OpenRequest(session);
        try
        {
            if (stopAndSend)
            {
                Assert.Equal(new SendResult.Queued(), await session.SendAsync(key.Turn, "Do it differently", true, default));
            }
            else
            {
                Assert.Equal(CommandOutcome.Applied, (await session.CancelAsync(key.Turn, default)).Outcome);
            }

            var late = key with { Id = "s:q2" };
            await Until(() => runs.Latest[Node.Id].Requests.ContainsKey(late));
            Assert.Equal(new QuestionState.Closed(stopAndSend ? RequestCloseReason.Stopped : RequestCloseReason.Cancelled, null),
                Assert.IsType<RequestRecord.Question>(await session.ReadRequestAsync(late, default)).State);
            Assert.Equal(AnswerOutcome.Stale, (await session.AnswerAsync(late, Local, default)).Outcome);
            await Until(() => File.Exists(FileAt("declined")));
            using var wire = JsonDocument.Parse(File.ReadAllText(FileAt("decline.json")));
            Assert.Equal("deny", wire.RootElement.GetProperty("response").GetProperty("response").GetProperty("behavior").GetString());
            File.WriteAllText(FileAt("go"), "go");
        }
        finally
        {
            File.WriteAllText(FileAt("go"), "go");
            _clock.Advance(TimeSpan.FromSeconds(60));
        }
    }

    [Fact]
    public async Task Leaving_closes_an_unanswered_question_as_interrupted()
    {
        InstallQuestion(InterruptAndEnd());
        var runs = await Open();
        using var session = runs.OpenConversation(Node.Id);
        var key = await OpenRequest(session);
        await runs.DisposeAsync();
        var record = runs.Latest[Node.Id];
        Assert.Equal(AttemptStatus.Interrupted, record.Status);
        Assert.Equal(new QuestionState.Closed(RequestCloseReason.Interrupted, null), Assert.IsType<RequestRecord.Question>(record.Requests[key]).State);
        Assert.Equal(RequestCloseReason.Interrupted, Assert.Single(Events(record).OfType<AttemptEvent.RequestClosed>()).Reason);
        Assert.Equal(0, AnswerFrames());
    }

    [Fact]
    public async Task Protocol_failure_closes_an_open_question_before_turn_exit()
    {
        InstallQuestion(FakeRule.On().WaitForFile(FileAt("fail"))
            .Print(Question.Replace("Fixture?", "Changed?", StringComparison.Ordinal)).Hang());
        await using var runs = await Open();
        using var session = runs.OpenConversation(Node.Id);
        var key = await OpenRequest(session);
        File.WriteAllText(FileAt("fail"), "go");
        var record = await Settled(runs);
        Assert.Equal(AttemptStatus.Failed, record.Status);
        Assert.Equal(new QuestionState.Closed(RequestCloseReason.TurnEnded, null), Assert.IsType<RequestRecord.Question>(record.Requests[key]).State);
        Assert.Equal(RequestCloseReason.TurnEnded, Assert.Single(Events(record).OfType<AttemptEvent.RequestClosed>()).Reason);
        Assert.Equal(0, AnswerFrames());
    }

    [Fact]
    public async Task An_identical_answer_after_provider_resolution_is_already_recorded_without_another_frame()
    {
        InstallQuestion(FakeRule.On().ReadLine(FileAt("answer.json"))
            .Print("""{"type":"control_cancel_request","request_id":"q1"}""").WaitForFile(FileAt("go"))
            .Print(ReplyLines(ClientId.ClaudeCode, "Done")));
        await using var runs = await Open();
        using var session = runs.OpenConversation(Node.Id);
        var key = await OpenRequest(session);
        try
        {
            Assert.Equal(AnswerOutcome.Recorded, (await session.AnswerAsync(key, Local, default)).Outcome);
            await Until(() => runs.Latest[Node.Id].Requests[key] is RequestRecord.Question { State: QuestionState.Closed });
            var closed = Assert.IsType<QuestionState.Closed>(Assert.IsType<RequestRecord.Question>(await session.ReadRequestAsync(key, default)).State);
            Assert.Equal(RequestCloseReason.Resolved, closed.Reason);
            Assert.Equal("Local", AnswerText(Assert.IsType<RequestRecord.Question>(await session.ReadRequestAsync(key, default))));
            Assert.Equal(AnswerOutcome.AlreadyRecorded, (await session.AnswerAsync(key,
                new QuestionsReply([new QuestionAnswer("q:0", ["o:0"], null)]), default)).Outcome);
            File.WriteAllText(FileAt("go"), "go");
            var record = await Settled(runs);
            Assert.Equal(1, AnswerFrames());
            Assert.Single(Events(record).OfType<AttemptEvent.RequestAnswered>());
        }
        finally
        {
            File.WriteAllText(FileAt("go"), "go");
            _clock.Advance(TimeSpan.FromSeconds(60));
        }
    }

    [Fact]
    public async Task An_identical_answer_from_an_earlier_turn_is_stale_without_another_frame()
    {
        InstallQuestion(FakeRule.On().ReadLine(FileAt("answer.json")).WaitForFile(FileAt("go"))
            .Print(ReplyLines(ClientId.ClaudeCode, "First done")));
        await using var runs = await Open();
        using var session = runs.OpenConversation(Node.Id);
        var key = await OpenRequest(session);
        try
        {
            Assert.Equal(AnswerOutcome.Recorded, (await session.AnswerAsync(key, Local, default)).Outcome);
            Assert.Equal(new SendResult.Queued(), await session.SendAsync(key.Turn, "Continue", false, default));
            Install(_fakes, ClientId.ClaudeCode, Resuming(ClientId.ClaudeCode, Session)
                .RecordFrames(FileAt("second-frames.jsonl")).CapturePrompt(FileAt("prompt.txt"))
                .Print(SessionLine(ClientId.ClaudeCode, Session))
                .Write(FileAt("second"), "yes").WaitForFile(FileAt("finish"))
                .Print(ReplyLines(ClientId.ClaudeCode, "Done")).WaitForStdinEnd());
            File.WriteAllText(FileAt("go"), "go");
            await Until(() => File.Exists(FileAt("second")));
            Assert.Equal(new TurnKey(key.Turn.Attempt, 2), session.Snapshot.Current);
            Assert.Equal(AnswerOutcome.Stale, (await session.AnswerAsync(key, Local, default)).Outcome);
            File.WriteAllText(FileAt("finish"), "go");
            var record = await Settled(runs);
            Assert.Equal((AttemptStatus.Succeeded, 2), (record.Status, record.Turns.Count));
            Assert.Equal(1, AnswerFrames());
            Assert.DoesNotContain(File.ReadLines(FileAt("second-frames.jsonl")),
                line => line.Contains("\"behavior\":\"allow\"", StringComparison.Ordinal));
            Assert.Single(Events(record).OfType<AttemptEvent.RequestAnswered>());
        }
        finally
        {
            File.WriteAllText(FileAt("go"), "go");
            File.WriteAllText(FileAt("finish"), "go");
        }
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
        using var fakes = new FakeClients(_fakes.Folder, FakeClientInstallMode.Direct);
        var command = Install(fakes, ClientId.ClaudeCode, Fresh(ClientId.ClaudeCode).RecordFrames(FileAt("frames.jsonl"))
            .Print(SessionLine(ClientId.ClaudeCode, Session)).CloseStdin().Print(Question).Hang());
        Assert.Equal(OperatingSystem.IsWindows() ? "claude.exe" : "claude", Path.GetFileName(command));
        var clients = await fakes.DiscoverAsync();
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
    public async Task Deferred_exit_keeps_Send_disabled_until_the_lock_is_released()
    {
        InstallQuestion(InterruptAndEnd());
        await using var runs = await Open();
        using var session = runs.OpenConversation(Node.Id);
        await OpenRequest(session);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runs.BeforeRelease = () =>
        {
            entered.TrySetResult();
            return release.Task;
        };

        try
        {
            _clock.Advance(TimeSpan.FromSeconds(55));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(AttemptStatus.Running, session.Snapshot.Latest!.Status);
            Assert.Equal(AttemptStatus.Running, (await session.ListAttemptsAsync(default)).Single().Status);
            Assert.Equal("Build Running", string.Join(", ", runs.Active.Select(record => $"{record.TaskTitle} {record.Status}")));
            using var held = TaskLease.TryTake(_project, Node.Id);
            var during = (session.Snapshot.Actions.Send, runs.CheckSend(Node), LockHeld: held is null, runs.Latest[Node.Id].Status);
            Assert.Equal((new ActionAvailability(false, "The turn is ending. Wait for teardown to finish."), new SendProblem.Ending("Build"), true, AttemptStatus.Running), during);
            release.TrySetResult();
            var record = await Settled(runs);
            Assert.Equal((AttemptStatus.WaitingForInput, TurnOutcome.Deferred), (record.Status, record.Turns.Single().Outcome));
            Assert.Equal(new ActionAvailability(true, "Send a message."), session.Snapshot.Actions.Send);
            Assert.Null(runs.CheckSend(Node));
            using var released = TaskLease.TryTake(_project, Node.Id);
            Assert.NotNull(released);
        }
        finally
        {
            release.TrySetResult();
        }
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

    [UnixFact]
    public async Task Session_send_does_not_hold_up_another_tasks_snapshot_while_Git_waits()
    {
        var other = TestNodes.Implement(TaskId.New(), "Other", "Build it", execution: Settings, conversation: ConversationMode.MayAsk);
        _workflow = _workflow.Must(TestNodes.Place(other, new CanvasPoint(300, 0)));
        Install(_fakes, ClientId.ClaudeCode, Fresh(ClientId.ClaudeCode)
            .Print(SessionLine(ClientId.ClaudeCode, Session)).Print(ReplyLines(ClientId.ClaudeCode, "Done")));
        await using var runs = await Open();
        await Settled(runs);
        Assert.IsType<StartResult.Started>(runs.Start(other));
        await Until(() => runs.Latest.GetValueOrDefault(other.Id) is { Status: AttemptStatus.Succeeded } && runs.Live(other.Id) is null);
        using var session = runs.OpenConversation(Node.Id);
        using var observer = runs.OpenConversation(other.Id);
        var expected = session.Snapshot.Current!.Value;
        var entered = FileAt("git-entered");
        var release = FileAt("git-release");
        _fakes.Install("git", FakeRule.On("rev-parse", "--git-path", "index")
            .Write(entered, "entered").WaitForFile(release).Exit(1));
        var previousPath = Environment.GetEnvironmentVariable("PATH");
        Task<SendResult>? send = null;
        try
        {
            Environment.SetEnvironmentVariable("PATH", _fakes.Folder + Path.PathSeparator + previousPath);
            send = Task.Run(() => session.SendAsync(expected, "Continue", false, default));
            await Until(() => File.Exists(entered));
            var snapshot = await Task.Run(() => observer.Snapshot).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal((AttemptStatus.Succeeded, "Done", true),
                (snapshot.Latest!.Status, snapshot.Latest.Result, snapshot.Actions.Send.Enabled));
            Assert.False(send.IsCompleted);
            File.WriteAllText(release, "go");
            Assert.Equal("Continue", Assert.IsType<SendResult.Continued>(await send).Attempt.Turns.Single().Message);
            await Settled(runs);
        }
        finally
        {
            File.WriteAllText(release, "go");
            if (send is not null)
            {
                await send;
                await Settled(runs);
            }

            Environment.SetEnvironmentVariable("PATH", previousPath);
        }
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
