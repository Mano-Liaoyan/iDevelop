using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Tests;

public sealed class FakeConversationSessionTests
{
    [Fact]
    public async Task FakeConversationSession_can_be_driven_by_a_view_model()
    {
        var attempt = new AttemptId(Guid.Parse("019a9d2e-1111-7000-8000-000000000001"));
        var turn = new TurnKey(attempt, 1);
        var key = new RequestKey(turn, "s:q1");
        var reply = new QuestionsReply([new QuestionAnswer("q:0", ["o:0"], null)]);
        var enabled = new ActionAvailability(true, "Send a message.");
        using var session = new FakeConversationSession(TestTasks.Build)
        {
            Snapshot = new ConversationSnapshot(1, 1, turn, null, new ClientCapabilities(true, true, "Permissions are declined."),
                new ConversationActions(enabled, enabled, enabled, enabled, enabled)),
            Attempts = [new AttemptSummary(attempt, null, DateTimeOffset.UnixEpoch, AttemptStatus.Running, new ExecutionSettings(ClientId.ClaudeCode))],
        };
        session.Requests[key] = new RequestRecord.Question(key, [new AskedQuestion("q:0", "Fixture", "Fixture?", [new QuestionOption("o:0", "Local", null)], false, true)],
            new QuestionState.Open(new RequestDeadline(DateTimeOffset.UnixEpoch.AddSeconds(55), DateTimeOffset.UnixEpoch.AddSeconds(60))));
        var page = new HistoryResult.Page(1, [new ConversationEntry(new EntryId("q1"), turn, 1, DateTimeOffset.UnixEpoch, new ConversationContent.Request(key))],
            new HistoryWindow("window"), new HistoryCursor("before"), new HistoryCursor("after"), false, false);
        session.Pages[(attempt, new HistoryQuery.Latest(), 20)] = page;
        long refreshed = 0;
        session.Changed += revision => refreshed = revision;
        Assert.Equal([attempt], (await session.ListAttemptsAsync(default)).Select(summary => summary.Id));
        Assert.Equal(["q1"], Assert.IsType<HistoryResult.Page>(await session.ReadPageAsync(attempt, new HistoryQuery.Latest(), 20, default)).Entries.Select(entry => entry.Id.Value));
        Assert.Equal("Fixture?", Assert.IsType<RequestRecord.Question>(await session.ReadRequestAsync(key, default)).Questions.Single().Text);
        Assert.Equal(AnswerOutcome.Recorded, (await session.AnswerAsync(key, reply, default)).Outcome);
        session.Snapshot = session.Snapshot with { Revision = 2, LogRevision = 2 };
        session.Requests[key] = ((RequestRecord.Question)session.Requests[key]) with { State = new QuestionState.AnswerRecorded(reply) };
        session.RaiseChanged();
        Assert.Equal((2L, 2L, 2L), (refreshed, session.Snapshot.Revision, session.Snapshot.LogRevision));
        Assert.IsType<QuestionState.AnswerRecorded>(Assert.IsType<RequestRecord.Question>(await session.ReadRequestAsync(key, default)).State);
        var draft = "Use the fixture";
        if (await session.SendAsync(turn, draft, false, default) is SendResult.Queued) draft = "";
        Assert.Equal("", draft);
        Assert.Equal(CommandOutcome.Applied, (await session.CancelAsync(turn, default)).Outcome);
        Assert.Equal(CommandOutcome.Applied, (await session.MarkDoneAsync(turn, default)).Outcome);
        session.Terminal = _ => new TerminalResult.HandedOff("/project", "claude --resume session-1");
        Assert.Equal(new TerminalResult.HandedOff("/project", "claude --resume session-1"), await session.OpenInTerminalAsync(turn, default));
        Assert.Equal([new FakeConversationSession.Call.ListAttempts(default), new FakeConversationSession.Call.Page(attempt, new HistoryQuery.Latest(), 20, default),
            new FakeConversationSession.Call.Request(key, default), new FakeConversationSession.Call.Answer(key, reply, default), new FakeConversationSession.Call.Request(key, default),
            new FakeConversationSession.Call.Send(turn, "Use the fixture", false, default), new FakeConversationSession.Call.Cancel(turn, default),
            new FakeConversationSession.Call.MarkDone(turn, default), new FakeConversationSession.Call.Terminal(turn, default)], session.Calls);
        session.Dispose();
        Assert.Equal(AnswerOutcome.Unavailable, (await session.AnswerAsync(key, reply, default)).Outcome);
    }
}
