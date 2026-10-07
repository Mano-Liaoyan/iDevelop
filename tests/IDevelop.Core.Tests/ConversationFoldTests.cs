using System.Collections.Immutable;
using System.Text.Json;
using IDevelop.Execution;
using IDevelop.Nodes;
using IDevelop.TestSupport;
using static IDevelop.Core.Tests.AttemptEvents;

namespace IDevelop.Core.Tests;

public class ConversationFoldTests
{
    private const string Fallback = "Fixture?\nLocal\nRecorded\n\nCoverage?\nCore\nDesktop";
    private static readonly RequestKey FixtureKey = new(new TurnKey(First, 1), "s:fixture");
    private static readonly RequestKey CoverageKey = new(new TurnKey(First, 1), "s:coverage");

    [Fact]
    public void A_policy_denied_question_is_closed_and_does_not_prevent_success()
    {
        AttemptEvent[] events =
        [
            BuildRequested(First), LaunchedAt1s,
            new AttemptEvent.QuestionRecorded(T0.AddSeconds(2), "s:fixture", FixtureQuestions(),
                new QuestionState.Closed(RequestCloseReason.PolicyDenied, null)),
        ];

        var denied = Fold(events);
        var done = Fold([.. events, Said(3, new AgentEvent.Succeeded("Done.")), Exit(4, 0)]);

        var question = Assert.IsType<RequestRecord.Question>(Assert.Single(denied.Requests).Value);
        Assert.Equal(FixtureKey, question.Key);
        Assert.Equal("Fixture?", Assert.Single(question.Questions).Text);
        Assert.Equal(new QuestionState.Closed(RequestCloseReason.PolicyDenied, null), question.State);
        Assert.Equal((AttemptStatus.Succeeded, "Done.", 0), (done.Status, done.Result, OpenQuestions(done)));
    }

    [Theory]
    [InlineData(RequestCloseReason.PolicyDenied, PermissionState.Denied)]
    [InlineData(RequestCloseReason.DeliveryUnknown, PermissionState.DeliveryUnknown)]
    public void A_permission_declines_and_then_records_the_denial_delivery(RequestCloseReason reason, PermissionState state)
    {
        AttemptEvent[] events =
        [
            BuildRequested(First), LaunchedAt1s,
            Said(2, new AgentEvent.PermissionRequested("n:0", new PermissionAction("command", "{\"command\":\"dotnet test\"}", "/project"))),
        ];

        var declining = Fold(events);
        var closed = Fold([.. events, new AttemptEvent.RequestClosed(T0.AddSeconds(3), "n:0", reason)]);

        var permission = Assert.IsType<RequestRecord.Permission>(Assert.Single(declining.Requests).Value);
        Assert.Equal(new RequestKey(new TurnKey(First, 1), "n:0"), permission.Key);
        Assert.Equal(new PermissionAction("command", "{\"command\":\"dotnet test\"}", "/project"), permission.Action);
        Assert.Equal(PermissionState.Declining, permission.State);
        Assert.Equal(state, Assert.IsType<RequestRecord.Permission>(Assert.Single(closed.Requests).Value).State);
    }

    [Fact]
    public void Deferral_waits_after_exit_and_keeps_queued_text_without_starting_a_turn()
    {
        var stopping = Fold(Deferred());
        var waiting = Fold([.. Deferred(), Said(7, new AgentEvent.Succeeded("Done.")), Exit(8, 0)]);

        Assert.Equal((AttemptStatus.Running, true, 0), (stopping.Status, stopping.Stopping, OpenQuestions(stopping)));
        Assert.Equal(new QuestionState.Closed(RequestCloseReason.Deferred, null), Assert.IsType<RequestRecord.Question>(waiting.Requests[FixtureKey]).State);
        Assert.Equal(new QuestionState.Closed(RequestCloseReason.Deferred, null), Assert.IsType<RequestRecord.Question>(waiting.Requests[CoverageKey]).State);
        Assert.Equal((AttemptStatus.WaitingForInput, false, "session-1", 2), (waiting.Status, waiting.Stopping, waiting.SessionId, waiting.Requests.Count));
        Assert.Equal(new Pending.Question("Fixture?\nLocal\nRecorded\n\nCoverage?\nCore\nDesktop"), waiting.Pending);
        Assert.Equal([new TurnRecord(1, null, TurnOutcome.Deferred, "Done.")], waiting.Turns);
        Assert.Equal(["Also add tests"], waiting.Queued.Select(message => message.Text));
        Assert.Equal(["m:1"], waiting.Queued.Select(message => message.Id));
    }

    [Fact]
    public void Deferral_without_a_session_fails_with_the_resume_reason()
    {
        var record = Fold([.. Deferred(session: false), Said(7, new AgentEvent.Succeeded("Done.")), Exit(8, 0)]);

        Assert.Equal((AttemptStatus.Failed, "The client reported no session; this question cannot be resumed."), (record.Status, record.Detail));
        Assert.Equal(TurnOutcome.Failed, Assert.Single(record.Turns).Outcome);
        Assert.Equal(["Also add tests"], record.Queued.Select(message => message.Text));
        Assert.Equal(new QuestionState.Closed(RequestCloseReason.Deferred, null), Assert.IsType<RequestRecord.Question>(record.Requests[FixtureKey]).State);
    }

    [Fact]
    public void Cancel_before_deferred_settlement_wins()
    {
        var record = Fold([.. Deferred(), new AttemptEvent.CancelRequested(T0.AddSeconds(7)), Said(8, new AgentEvent.Succeeded("Done.")), Exit(9, 0)]);
        var readOnly = Fold([.. Deferred().Select(e => e is AttemptEvent.Requested requested ? requested with { ReadOnly = true, Tree = "before" } : e),
            new AttemptEvent.CancelRequested(T0.AddSeconds(7)), Exit(9, 137) with { Tree = "after" }]);

        Assert.Equal((AttemptStatus.Cancelled, TurnOutcome.Stopped, false), (record.Status, Assert.Single(record.Turns).Outcome, record.Stopping));
        Assert.Equal(["Also add tests"], record.Queued.Select(message => message.Text));
        Assert.Null(record.Pending);
        Assert.Equal((AttemptStatus.Cancelled, (string?)null), (readOnly.Status, readOnly.Detail));
    }

    [Fact]
    public void A_waiting_reply_consumes_identified_messages_and_records_the_deferred_requests_it_replies_to()
    {
        AttemptEvent[] waiting = [.. Deferred(), Said(7, new AgentEvent.Succeeded("Done.")), Exit(8, 0)];
        var queued = new AttemptEvent.MessageQueued(T0.AddSeconds(9), "Use the fixture", false) { Id = "m:2" };

        var beforeTurn = Fold([.. waiting, queued]);
        var running = Fold([.. waiting, queued, NextTurn(10, "Also add tests\n\nUse the fixture") with
        {
            Consumed = ["m:1", "m:2"],
            Replies = [new TurnRequestId(1, "s:fixture"), new TurnRequestId(1, "s:coverage")],
        }]);

        Assert.Equal((AttemptStatus.WaitingForInput, 1), (beforeTurn.Status, beforeTurn.Turns.Count));
        Assert.Equal(["Also add tests", "Use the fixture"], beforeTurn.Queued.Select(message => message.Text));
        Assert.Equal((First, AttemptStatus.Running, "session-1", 2, 0), (running.Id, running.Status, running.SessionId, running.Turns.Count, running.Queued.Count));
        Assert.Equal((2, "Also add tests\n\nUse the fixture", TurnOutcome.Running, (string?)null),
            (running.Turns[^1].Number, running.Turns[^1].Message, running.Turns[^1].Outcome, running.Turns[^1].FinalText));
        Assert.Equal([new TurnRequestId(1, "s:fixture"), new TurnRequestId(1, "s:coverage")], running.Turns[^1].Replies.ToArray());
        Assert.Equal(new QuestionState.Closed(RequestCloseReason.Deferred, null), Assert.IsType<RequestRecord.Question>(running.Requests[FixtureKey]).State);
        Assert.Null(running.Pending);
    }

    [Fact]
    public void Forced_shutdown_after_success_fails_instead_of_continuing_queued_text()
    {
        var record = Fold([BuildRequested(First), LaunchedAt1s, Said(2, new AgentEvent.SessionStarted("session-1")),
            new AttemptEvent.MessageQueued(T0.AddSeconds(3), "Also add tests", false) { Id = "m:1" },
            Said(4, new AgentEvent.Succeeded("Done.")), new AttemptEvent.ShutdownForced(T0.AddSeconds(9)), Exit(10, 137)]);

        Assert.Equal((AttemptStatus.Failed, "The client reported success but did not shut down."), (record.Status, record.Detail));
        Assert.Equal((TurnOutcome.Failed, "Done.", "The client reported success but did not shut down."),
            (Assert.Single(record.Turns).Outcome, record.Result, record.Turns[0].Detail));
        Assert.Equal(["Also add tests"], record.Queued.Select(message => message.Text));
    }

    [Fact]
    public void A_legacy_queued_message_gets_the_same_literal_identity_on_every_replay()
    {
        AttemptEvent[] events = [BuildRequested(First), LaunchedAt1s, new AttemptEvent.MessageQueued(T0.AddSeconds(2), "Also add tests", false)];

        var first = Fold(events);
        var again = Fold(events);

        Assert.Equal(new QueuedMessage("legacy:019aa000-0000-7000-8000-000000000001:2", "Also add tests"), Assert.Single(first.Queued));
        Assert.Equal(new QueuedMessage("legacy:019aa000-0000-7000-8000-000000000001:2", "Also add tests"), Assert.Single(again.Queued));
    }

    [Fact]
    public void Closure_preserves_a_recorded_answer_and_later_resolution_does_not_overwrite_delivery_uncertainty()
    {
        var record = Fold([BuildRequested(First), LaunchedAt1s, OpenFixture(),
            new AttemptEvent.RequestAnswered(T0.AddSeconds(3), "s:fixture", new QuestionsReply([new QuestionAnswer("fixture", ["local"], null)])),
            new AttemptEvent.RequestClosed(T0.AddSeconds(4), "s:fixture", RequestCloseReason.DeliveryUnknown),
            new AttemptEvent.RequestClosed(T0.AddSeconds(5), "s:fixture", RequestCloseReason.Resolved), Exit(6, 1)]);

        var state = Assert.IsType<QuestionState.Closed>(Assert.IsType<RequestRecord.Question>(record.Requests[FixtureKey]).State);
        Assert.Equal(RequestCloseReason.DeliveryUnknown, state.Reason);
        var answer = Assert.Single(Assert.IsType<QuestionsReply>(state.RecordedReply).Answers);
        Assert.Equal("fixture", answer.QuestionId);
        Assert.Equal(["local"], answer.OptionIds.ToArray());
        Assert.Equal(AttemptStatus.Failed, record.Status);
    }

    [Fact]
    public void Delivery_uncertainty_overrides_earlier_resolution_and_keeps_the_recorded_reply()
    {
        var record = Fold([BuildRequested(First), LaunchedAt1s, OpenFixture(),
            new AttemptEvent.RequestAnswered(T0.AddSeconds(3), "s:fixture", new QuestionsReply([new QuestionAnswer("fixture", ["local"], null)])),
            new AttemptEvent.RequestClosed(T0.AddSeconds(4), "s:fixture", RequestCloseReason.Resolved),
            new AttemptEvent.RequestClosed(T0.AddSeconds(5), "s:fixture", RequestCloseReason.DeliveryUnknown), Exit(6, 1)]);

        var state = Assert.IsType<QuestionState.Closed>(Assert.IsType<RequestRecord.Question>(record.Requests[FixtureKey]).State);
        Assert.Equal(RequestCloseReason.DeliveryUnknown, state.Reason);
        var answer = Assert.Single(Assert.IsType<QuestionsReply>(state.RecordedReply).Answers);
        Assert.Equal("fixture", answer.QuestionId);
        Assert.Equal(["local"], answer.OptionIds.ToArray());
        Assert.Equal(AttemptStatus.Failed, record.Status);
    }

    [Fact]
    public void Deferral_closes_only_unanswered_questions_and_keeps_an_already_recorded_reply()
    {
        var record = Fold([BuildRequested(First), LaunchedAt1s, Said(2, new AgentEvent.SessionStarted("session-1")), OpenFixture(),
            new AttemptEvent.QuestionRecorded(T0.AddSeconds(3), "s:coverage", CoverageQuestions(),
                new QuestionState.Open(new RequestDeadline(T0.AddSeconds(57), T0.AddSeconds(62)))),
            new AttemptEvent.RequestAnswered(T0.AddSeconds(4), "s:fixture", new QuestionsReply([new QuestionAnswer("fixture", ["local"], null)])),
            new AttemptEvent.RequestDeferred(T0.AddSeconds(6), ["s:coverage"], "Coverage?\nCore\nDesktop"),
            Said(7, new AgentEvent.Failed("Interrupted")), Exit(8, 137)]);

        Assert.Equal((AttemptStatus.WaitingForInput, TurnOutcome.Deferred), (record.Status, Assert.Single(record.Turns).Outcome));
        Assert.Equal(new Pending.Question("Coverage?\nCore\nDesktop"), record.Pending);
        Assert.Equal(new QuestionState.Closed(RequestCloseReason.Deferred, null), Assert.IsType<RequestRecord.Question>(record.Requests[CoverageKey]).State);
        var answered = Assert.IsType<QuestionState.Closed>(Assert.IsType<RequestRecord.Question>(record.Requests[FixtureKey]).State);
        Assert.Equal(RequestCloseReason.TurnEnded, answered.Reason);
        Assert.Equal(["local"], Assert.Single(Assert.IsType<QuestionsReply>(answered.RecordedReply).Answers).OptionIds.ToArray());
    }

    [Fact]
    public void Deferral_does_not_override_the_read_only_tree_check_or_leaving_and_reconciles_after_a_crash()
    {
        var changed = Fold([.. Deferred().Select(e => e is AttemptEvent.Requested requested ? requested with { ReadOnly = true, Tree = "before" } : e),
            Said(7, new AgentEvent.Succeeded("Done.")), Exit(8, 0) with { Tree = "after" }]);
        var left = Fold([.. Deferred(), new AttemptEvent.InterruptRequested(T0.AddSeconds(7), "The project was closed while this task ran."), Exit(8, 137)]);
        var crashed = Fold([.. Deferred(), new AttemptEvent.Reconciled(T0.AddSeconds(7), ProcessMatch.Gone)]);

        Assert.Equal((AttemptStatus.Failed, "The turn changed files in the project, although this node's agent may only read."), (changed.Status, changed.Detail));
        Assert.Equal((AttemptStatus.Interrupted, "The project was closed while this task ran."), (left.Status, left.Detail));
        Assert.Equal((AttemptStatus.Interrupted, "iDevelop stopped while this task ran."), (crashed.Status, crashed.Detail));
    }

    [Fact]
    public void Turn_request_consumes_only_the_named_messages_and_request_ids_are_scoped_by_turn()
    {
        var record = Fold([.. Deferred(), Said(7, new AgentEvent.Succeeded("Done.")), Exit(8, 0),
            NextTurn(9, "Also add tests") with { Consumed = ["m:1"] }, OpenFixture(),
            new AttemptEvent.MessageQueued(T0.AddSeconds(10), "Keep this", false) { Id = "m:2" }]);

        Assert.Equal((AttemptStatus.Running, 2, 3), (record.Status, record.Turns.Count, record.Requests.Count));
        Assert.Equal(new QuestionState.Closed(RequestCloseReason.Deferred, null), Assert.IsType<RequestRecord.Question>(record.Requests[FixtureKey]).State);
        Assert.Equal(new QuestionState.Open(new RequestDeadline(T0.AddSeconds(57), T0.AddSeconds(62))),
            Assert.IsType<RequestRecord.Question>(record.Requests[new RequestKey(new TurnKey(First, 2), "s:fixture")]).State);
        Assert.Equal([new QueuedMessage("m:2", "Keep this")], record.Queued);
    }

    [Fact]
    public void The_log_round_trips_question_states_message_metadata_and_reply_references()
    {
        using var temp = new TempFolder();
        string folder;
        using (var log = AttemptLog.Create(temp.Create("attempts"), BuildRequested(First)))
        {
            folder = log.Folder;
            log.Append(LaunchedAt1s);
            log.Append(Said(2, new AgentEvent.SessionStarted("session-1")));
            log.Append(OpenFixture());
            log.Append(new AttemptEvent.RequestAnswered(T0.AddSeconds(3), "s:fixture", new QuestionsReply([new QuestionAnswer("fixture", ["local"], null)])));
            log.Append(new AttemptEvent.RequestClosed(T0.AddSeconds(4), "s:fixture", RequestCloseReason.Resolved));
            log.Append(new AttemptEvent.QuestionRecorded(T0.AddSeconds(4), "s:coverage", CoverageQuestions(),
                new QuestionState.Open(new RequestDeadline(T0.AddSeconds(57), T0.AddSeconds(62)))));
            log.Append(new AttemptEvent.QuestionRecorded(T0.AddSeconds(4), "s:denied", FixtureQuestions(), new QuestionState.Closed(RequestCloseReason.PolicyDenied, null)));
            log.Append(new AttemptEvent.MessageQueued(T0.AddSeconds(5), "Also add tests", false) { Id = "m:1" });
            log.Append(new AttemptEvent.RequestDeferred(T0.AddSeconds(6), ["s:coverage"], "Coverage?\nCore\nDesktop"));
            log.Append(new AttemptEvent.Agent(T0.AddSeconds(7), new AgentEvent.Message("Done.") { Id = "message-1:0", Partial = true }) { Order = 3 });
            log.Append(Said(7, new AgentEvent.Succeeded("Done.")));
            log.Append(Exit(8, 0));
            log.Append(new AttemptEvent.MessageQueued(T0.AddSeconds(9), "Use the fixture", false) { Id = "m:2" });
            log.Append(NextTurn(10, "Also add tests\n\nUse the fixture") with { Consumed = ["m:1", "m:2"], Replies = [new TurnRequestId(1, "s:coverage")] });
            log.Append(Said(11, new AgentEvent.PermissionRequested("n:0", new PermissionAction("command", "{}", "/project"))));
            log.Append(new AttemptEvent.RequestClosed(T0.AddSeconds(12), "n:0", RequestCloseReason.PolicyDenied));
            log.Append(Said(13, new AgentEvent.Succeeded("Done again.")));
            log.Append(new AttemptEvent.ShutdownForced(T0.AddSeconds(18)));
            log.Append(Exit(19, 137));
        }

        var events = AttemptLog.Read(folder);
        var record = Fold(events);

        Assert.Equal(20, events.Length);
        Assert.Equal((AttemptStatus.Failed, "The client reported success but did not shut down.", 2), (record.Status, record.Detail, record.Turns.Count));
        Assert.Equal(new QuestionState.Closed(RequestCloseReason.PolicyDenied, null), Assert.IsType<RequestRecord.Question>(record.Requests[new RequestKey(new TurnKey(First, 1), "s:denied")]).State);
        Assert.Equal(RequestCloseReason.Resolved, Assert.IsType<QuestionState.Closed>(Assert.IsType<RequestRecord.Question>(record.Requests[FixtureKey]).State).Reason);
        Assert.Equal(["local"], Assert.Single(Assert.IsType<QuestionState.Closed>(Assert.IsType<RequestRecord.Question>(record.Requests[FixtureKey]).State).RecordedReply!.Answers).OptionIds.ToArray());
        Assert.Equal([new TurnRequestId(1, "s:coverage")], record.Turns[1].Replies.ToArray());
        var message = Assert.Single(events.OfType<AttemptEvent.Agent>(), e => e.Event is AgentEvent.Message);
        Assert.Equal((3L, "message-1:0", true, "Done."), (message.Order, ((AgentEvent.Message)message.Event).Id, ((AgentEvent.Message)message.Event).Partial, ((AgentEvent.Message)message.Event).Text));
        Assert.Equal(PermissionState.Denied, Assert.IsType<RequestRecord.Permission>(record.Requests[new RequestKey(new TurnKey(First, 2), "n:0")]).State);
        using var json = JsonDocument.Parse(File.ReadLines(Path.Combine(folder, "events.jsonl")).ElementAt(14));
        Assert.Equal(["m:1", "m:2"], json.RootElement.GetProperty("consumed").EnumerateArray().Select(value => value.GetString()));
    }

    private static ImmutableArray<AskedQuestion> FixtureQuestions() =>
        [new AskedQuestion("fixture", "Fixture", "Fixture?", [new QuestionOption("local", "Local", null), new QuestionOption("recorded", "Recorded", null)], false, true)];

    private static ImmutableArray<AskedQuestion> CoverageQuestions() =>
        [new AskedQuestion("coverage", "Coverage", "Coverage?", [new QuestionOption("core", "Core", null), new QuestionOption("desktop", "Desktop", null)], true, false)];

    private static AttemptEvent.QuestionRecorded OpenFixture() => new(T0.AddSeconds(2), "s:fixture", FixtureQuestions(),
        new QuestionState.Open(new RequestDeadline(T0.AddSeconds(57), T0.AddSeconds(62))));

    private static AttemptEvent[] Deferred(bool session = true) =>
    [
        BuildRequested(First), LaunchedAt1s,
        .. session ? new AttemptEvent[] { Said(2, new AgentEvent.SessionStarted("session-1")) } : [],
        OpenFixture(),
        new AttemptEvent.QuestionRecorded(T0.AddSeconds(3), "s:coverage", CoverageQuestions(),
            new QuestionState.Open(new RequestDeadline(T0.AddSeconds(57), T0.AddSeconds(62)))),
        new AttemptEvent.MessageQueued(T0.AddSeconds(4), "Also add tests", false) { Id = "m:1" },
        new AttemptEvent.RequestDeferred(T0.AddSeconds(6), ["s:fixture", "s:coverage"], Fallback),
    ];

    private static AttemptRecord Fold(IEnumerable<AttemptEvent> events) => Assert.IsType<AttemptRecord>(AttemptReducer.Replay(events));

    private static int OpenQuestions(AttemptRecord record) => record.Requests.Values.Count(request => request is RequestRecord.Question { State: QuestionState.Open });
}
