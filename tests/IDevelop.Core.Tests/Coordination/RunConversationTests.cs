using IDevelop.Core.Tests.Turns;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Coordination.CoordinatorFixture;

namespace IDevelop.Core.Tests.Coordination;

/// <summary>A person's messages to the tasks of a workflow run go through the run's coordinator (E3c.1).</summary>
public sealed class RunConversationTests
{
    private static Workflow Chat(params TaskId[] tasks) =>
        Graph([.. tasks.Select(task => RunConversationFixture.Agent(task, ConversationMode.Chat))]);

    private static async Task<SendResult> Send(IConversationSession session, string text, bool stopTurn = false) =>
        await session.SendAsync(session.Snapshot.Current!.Value, text, stopTurn, default).WaitAsync(Bound);

    [Fact]
    public async Task A_waiting_reply_starts_the_next_turn_in_the_prepared_checkout()
    {
        await using var f = new RunConversationFixture(Chat(A));
        f.Answer(A, f.Says(A, 1, "Which fixture?"), f.Says(A, 2, "Done.")).Route("Use the fixture", A);
        await f.Open();
        await f.Resume();
        var waiting = await f.UntilWaiting(A, 1);
        using var session = f.Session(A);
        Assert.Equal((0, 1, AttemptStatus.WaitingForInput), (waiting.Slots, f.TotalLaunches, session.Snapshot.Latest!.Status));
        Assert.True(session.Snapshot.Actions.Send.Enabled, session.Snapshot.Actions.Send.Reason);
        var terminal = Assert.IsType<TerminalResult.Refused>(await session.OpenInTerminalAsync(session.Snapshot.Current!.Value, default));
        Assert.Equal("Workflow", Assert.IsType<StartProblem.RunOwned>(Assert.IsType<TerminalProblem.Blocked>(terminal.Problem).Problem).Workflow);
        Assert.IsType<SendProblem.EmptyMessage>(Assert.IsType<SendResult.Refused>(await Send(session, " ")).Problem);
        Assert.IsType<SendProblem.MissingTask>(Assert.IsType<SendResult.Refused>(
            await f.Coordinator.Send(f.Address, B, session.Snapshot.Current!.Value, "Use the fixture", false).WaitAsync(Bound)).Problem);

        Assert.IsType<SendResult.Queued>(await Send(session, "Use the fixture"));
        await f.UntilWaiting(A, 2);
        Assert.Equal(2, f.Launches(A));
        Assert.Equal("Use the fixture", f.Prompt(A, 2));
        Assert.Equal("session-1", f.Resumed(A, 2));
        Assert.Equal(f.Checkout(A), f.WorkingFolder(A, 2));
        Assert.Single(f.Read().Attempts);
        Assert.Equal(new TurnKey(f.Attempt(A), 2), session.Snapshot.Current);
        var page = Assert.IsType<HistoryResult.Page>(await session.ReadPageAsync(f.Attempt(A), new HistoryQuery.Latest(), 50, default));
        Assert.Equal(["Which fixture?", "Use the fixture", "Done."], page.Entries.Select(entry => entry.Content)
            .OfType<ConversationContent.Message>().Select(message => message.Text).Skip(1));
    }

    [Fact]
    public async Task Text_sent_during_a_turn_follows_it_and_stop_and_send_closes_the_turn_first()
    {
        await using var f = new RunConversationFixture(Chat(A));
        f.Answer(A, f.Says(A, 1, "First.", gate: f.Gate("first")), f.Says(A, 2, "Added.", gate: f.Gate("never")), f.Says(A, 3, "Stopped and sent."))
            .Route("Also add tests", A).Route("Stop and use the fixture", A);
        await f.Open();
        await f.Resume();
        await f.Until(view => view.Tasks[A].State == TaskState.Running);
        using var session = f.Session(A);
        await TurnFixture.WaitUntilAsync(() => session.Snapshot.Latest?.SessionId == "session-1");

        Assert.IsType<SendResult.Queued>(await Send(session, "Also add tests"));
        f.Open("first");
        await TurnFixture.WaitUntilAsync(() => f.Launches(A) == 2 && f.View.Tasks[A].State == TaskState.Running);
        Assert.Equal("Also add tests", f.Prompt(A, 2));
        Assert.Equal("session-1", f.Resumed(A, 2));
        Assert.IsType<SendProblem.StaleTarget>(Assert.IsType<SendResult.Refused>(
            await session.SendAsync(new TurnKey(f.Attempt(A), 1), "Late", true, default).WaitAsync(Bound)).Problem);

        Assert.IsType<SendResult.Queued>(await Send(session, "Stop and use the fixture", stopTurn: true));
        await f.UntilWaiting(A, 3);
        Assert.Equal("Stop and use the fixture", f.Prompt(A, 3));
        var record = f.Read();
        var attempt = f.Attempt(A);
        long Sequence(Func<RunEvent, bool> match) => record.Receipts.Values.Single(entry => match(entry.Event)).Sequence;
        Assert.True(Sequence(e => e is RunEvent.TurnClosed closed && closed.Key == new LaunchKey(attempt, 2)) <
            Sequence(e => e is RunEvent.TurnClaimed claimed && claimed.Key == new LaunchKey(attempt, 3)));
        Assert.Equal(3, f.Launches(A));
        Assert.Empty(f.Log(A).Record!.Queued);
    }

    [Fact]
    public async Task A_second_window_reads_the_conversation_but_cannot_command_it()
    {
        await using var f = new RunConversationFixture(Chat(A));
        f.Answer(A, f.Says(A, 1, "Which fixture?"), f.Says(A, 2, "Done.")).Route("Use the fixture", A);
        await f.Open();
        await f.Resume();
        await f.UntilWaiting(A, 1);
        var (runs, coordinator) = await f.SecondWindow();
        await using (runs)
        {
            using var elsewhere = runs.OpenConversation(coordinator, A);
            var snapshot = elsewhere.Snapshot;
            var turn = snapshot.Current!.Value;
            Assert.Equal(AttemptStatus.WaitingForInput, snapshot.Latest!.Status);
            Assert.Equal((false, WorkflowRunCoordinator.ElsewhereMessage), (snapshot.Actions.Send.Enabled, snapshot.Actions.Send.Reason));
            Assert.Equal((false, WorkflowRunCoordinator.ElsewhereMessage), (snapshot.Actions.MarkDone.Enabled, snapshot.Actions.MarkDone.Reason));
            var refused = Assert.IsType<SendResult.Refused>(await elsewhere.SendAsync(turn, "Use the fixture", false, default).WaitAsync(Bound));
            Assert.Equal(WorkflowRunCoordinator.ElsewhereMessage, Assert.IsType<SendProblem.RunUnavailable>(refused.Problem).Reason);
            Assert.Equal((CommandOutcome.Unavailable, WorkflowRunCoordinator.ElsewhereMessage),
                await Outcome(elsewhere.MarkDoneAsync(turn, default)));
            Assert.Equal((CommandOutcome.Unavailable, WorkflowRunCoordinator.ElsewhereMessage),
                await Outcome(elsewhere.CancelAsync(turn, default)));
            Assert.Equal(AnswerOutcome.Unavailable, (await elsewhere.AnswerAsync(new(turn, "s:q1"), new([]), default).WaitAsync(Bound)).Outcome);
            Assert.Equal((0, 0), (f.Lines(A, "messageQueued"), f.Lines(A, "turnRequested")));
            var page = Assert.IsType<HistoryResult.Page>(await elsewhere.ReadPageAsync(turn.Attempt, new HistoryQuery.Latest(), 50, default));
            Assert.Contains(page.Entries, entry => entry.Content is ConversationContent.Message { Text: "Which fixture?" });
        }

        using var session = f.Session(A);
        Assert.IsType<SendResult.Queued>(await Send(session, "Use the fixture"));
        await f.UntilWaiting(A, 2);
        Assert.Equal((1, 1, 2), (f.Lines(A, "messageQueued"), f.Lines(A, "turnRequested"), f.Launches(A)));
        Assert.Equal("Use the fixture", f.Prompt(A, 2));
    }

    private const string Question = """{"type":"control_request","request_id":"q1","request":{"subtype":"can_use_tool","tool_name":"AskUserQuestion","input":{"questions":[{"question":"Fixture?","header":"Fixture","options":[{"label":"Local"},{"label":"Remote"}],"multiSelect":false}]}}}""";
    private const string Aborted = """{"type":"result","subtype":"error_during_execution","is_error":true,"terminal_reason":"aborted_streaming"}""";

    [Fact]
    public async Task A_deferred_question_keeps_queued_text_until_the_person_replies()
    {
        await using var f = new RunConversationFixture(
            Graph([RunConversationFixture.Agent(A, ConversationMode.Chat, ClientId.ClaudeCode)]), ClientId.ClaudeCode, new HostQuestions.DeferImmediately());
        f.Answer(A,
            FakeRule.On().Print(FakeAgents.SessionLine(ClientId.ClaudeCode, "session-1")).WaitForFile(f.Gate("ask")).Print(Question)
                .WaitForLine("\"subtype\":\"interrupt\"").Print(Aborted),
            f.Says(A, 2, "Using it."))
            .Route("Also add tests", A);
        await f.Open();
        await f.Resume();
        await f.Until(view => view.Tasks[A].State == TaskState.Running);
        using var session = f.Session(A);
        await TurnFixture.WaitUntilAsync(() => session.Snapshot.Latest?.SessionId == "session-1");
        Assert.IsType<SendResult.Queued>(await Send(session, "Also add tests"));
        f.Open("ask");

        var waiting = await f.UntilWaiting(A, 1);
        Assert.Equal(AttemptStatus.WaitingForInput, waiting.Tasks[A].Status);
        var record = f.Log(A).Record!;
        Assert.Equal(["Also add tests"], record.Queued.Select(message => message.Text));
        var turn = session.Snapshot.Current!.Value;
        Assert.Equal(new QuestionState.Closed(RequestCloseReason.Deferred, null),
            Assert.IsType<RequestRecord.Question>(await session.ReadRequestAsync(new RequestKey(turn, "s:q1"), default)).State);
        Assert.Equal(AnswerOutcome.Stale, (await session.AnswerAsync(new RequestKey(turn, "s:q1"), new([]), default)).Outcome);
        await f.Decided();
        await f.Decided();
        Assert.Equal((1, 0), (f.Launches(A), f.Lines(A, "turnRequested")));

        Assert.IsType<SendResult.Queued>(await Send(session, "Use the fixture"));
        await f.UntilWaiting(A, 2);
        Assert.Equal("Also add tests\n\nUse the fixture", f.Prompt(A, 2));
        Assert.Equal("session-1", f.Resumed(A, 2));
        Assert.Equal([new TurnRequestId(1, "s:q1")], f.Log(A).Record!.Turns[1].Replies.ToArray());
        Assert.Equal(2, f.Launches(A));
    }

    [Fact]
    public async Task A_reply_waits_for_the_run_s_slot_and_survives_a_restart()
    {
        await using var f = new RunConversationFixture(Graph([RunConversationFixture.Agent(A, ConversationMode.Chat),
            RunConversationFixture.Agent(X, ConversationMode.Autonomous, readOnly: true)]));
        f.Answer(A, f.Says(A, 1, "Which fixture?"), f.Says(A, 2, "Done.")).Route("Use the fixture", A)
            .Answer(X, f.Says(X, 1, "X ready.\n", "session-X", gate: f.Gate("x")));
        await f.Open();
        await f.Resume();
        await f.Until(view => view.Tasks[A].State == TaskState.Waiting && view.Tasks[X].State == TaskState.Running);
        using (var session = f.Session(A))
        {
            Assert.IsType<SendResult.Queued>(await Send(session, "Use the fixture"));
            await f.Decided();
            Assert.Equal((TaskState.Waiting, 1, 1), (f.View.Tasks[A].State, f.Launches(A), f.Lines(A, "messageQueued")));
            Assert.Equal(TaskState.Running, f.View.Tasks[X].State);
        }

        await f.Reopen();
        await f.Decided();
        Assert.Equal((RunStatus.Paused, 1), (f.View.Status, f.Launches(A)));
        await f.Resume();
        await f.UntilWaiting(A, 2);
        Assert.Equal((2, 1, 1), (f.Launches(A), f.Lines(A, "messageQueued"), f.Lines(A, "turnRequested")));
        Assert.Equal("Use the fixture", f.Prompt(A, 2));
        Assert.Equal(1, f.Launches(X));
    }

    [Fact]
    public async Task A_reply_takes_the_slot_before_a_task_that_has_not_started()
    {
        await using var f = new RunConversationFixture(Graph([RunConversationFixture.Agent(A, ConversationMode.Chat),
            RunConversationFixture.Agent(C, ConversationMode.Autonomous, readOnly: true), RunConversationFixture.Agent(X, ConversationMode.Autonomous, readOnly: true)]));
        f.Answer(A, f.Says(A, 1, "Which fixture?"), f.Says(A, 2, "Done.", gate: f.Gate("a2"))).Route("Use the fixture", A)
            .Answer(C, f.Says(C, 1, "C ready.\n", "session-C", gate: f.Gate("c")))
            .Answer(X, f.Says(X, 1, "X ready.\n", "session-X"));
        await f.Open();
        await f.Resume();
        await f.Until(view => view.Tasks[A].State == TaskState.Waiting && view.Tasks[C].State == TaskState.Running);
        using var session = f.Session(A);
        Assert.IsType<SendResult.Queued>(await Send(session, "Use the fixture"));
        Assert.Equal(TaskState.Ready, f.View.Tasks[X].State);

        f.Open("c");
        await f.Until(view => view.Tasks[A].State == TaskState.Running);
        await TurnFixture.WaitUntilAsync(() => f.Launches(A) == 2);
        await f.Decided();
        Assert.Equal((TaskState.Ready, 0), (f.View.Tasks[X].State, f.Launches(X)));
        f.Open("a2");
        await f.UntilWaiting(A, 2);
        await TurnFixture.WaitUntilAsync(() => f.Launches(X) == 1);
    }

    [Fact]
    public async Task History_labels_each_attempt_with_its_owner()
    {
        await using var f = new RunConversationFixture(Chat(A));
        var standalone = new AttemptId(Guid.Parse("019aa000-0000-7000-8000-0000000000a1"));
        var definition = f.Read().Revision.Snapshot.Tasks[A];
        using (var log = AttemptLog.Create(IDevelop.Projects.DataFolder.Attempts(f.Preparation.Git.Folder),
            new AttemptEvent.Requested(new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero), standalone, A, "A", definition.Execution!, "Build A alone.", "codex", [])))
        {
            log.Append(new AttemptEvent.Exited(new(2026, 10, 1, 9, 1, 0, TimeSpan.Zero), 1, ""));
        }
        f.Answer(A, FakeRule.On().Print(FakeAgents.SessionLine(ClientId.Codex, "session-1")).Exit(1), f.Says(A, 2, "Which fixture?"));
        await f.Open();
        await f.Resume();
        await f.UntilStatus(RunStatus.NeedsAttention);
        var first = f.Attempt(A);
        var cause = new AttemptCause.Retry(first, f.Preparation.Op());
        var retry = Assert.IsType<TurnStart.Started>(await f.Runs.StartTurn(f.Coordinator.Permit!,
            new TurnIntent.First(RunOperations.First(f.Read().Id, A, cause), A, cause)).WaitAsync(Bound)).Turn;
        await retry.Settlement.WaitAsync(Bound);
        f.Coordinator.Refresh();
        await f.Until(view => view.Tasks[A] is { State: TaskState.Waiting } state && state.Attempt == retry.Address.Launch.Attempt);

        using var session = f.Session(A);
        var attempts = await session.ListAttemptsAsync(default);
        Assert.Equal(["Standalone", "Run 1", "Run 1 retry"], attempts.Select(attempt => attempt.Label));
        Assert.Equal([standalone, first, retry.Address.Launch.Attempt], attempts.Select(attempt => attempt.Id));
        using var standaloneSession = f.Runs.OpenConversation(A);
        Assert.Equal(["Standalone", "Run 1", "Run 1 retry"], (await standaloneSession.ListAttemptsAsync(default)).Select(attempt => attempt.Label));
        Assert.Equal(standalone, standaloneSession.Snapshot.Latest!.Id);
        Assert.Equal(retry.Address.Launch.Attempt, session.Snapshot.Latest!.Id);
        var earlier = Assert.IsType<HistoryResult.Page>(await standaloneSession.ReadPageAsync(first, new HistoryQuery.Latest(), 50, default));
        Assert.Equal(first, Assert.Single(earlier.Entries.Select(entry => entry.Turn.Attempt).Distinct()));
        var page = Assert.IsType<HistoryResult.Page>(await session.ReadPageAsync(retry.Address.Launch.Attempt, new HistoryQuery.Latest(), 50, default));
        Assert.Contains(page.Entries, entry => entry.Content is ConversationContent.Message { Text: "Which fixture?" });
        var foreign = Assert.IsType<HistoryResult.Unavailable>(await session.ReadPageAsync(retry.Address.Launch.Attempt,
            new HistoryQuery.Before(Assert.IsType<HistoryResult.Page>(await standaloneSession.ReadPageAsync(retry.Address.Launch.Attempt,
                new HistoryQuery.Latest(), 50, default)).Before), 50, default));
        Assert.Equal("The history cursor belongs to another conversation or attempt chain.", foreign.Reason);
    }

    [Fact]
    public async Task A_reply_reaches_only_the_addressed_project()
    {
        await using var local = new RunConversationFixture(Chat(A));
        await using var other = new RunConversationFixture(Chat(A));
        foreach (var f in new[] { local, other })
        {
            f.Answer(A, f.Says(A, 1, "Fixture?"), f.Says(A, 2, "Done.")).Route("Local", A);
            await f.Open();
            await f.Resume();
            await f.UntilWaiting(A, 1);
        }
        Assert.Equal(local.Read().Id, other.Read().Id);
        Assert.Equal(local.Attempt(A), other.Attempt(A));
        using var session = local.Session(A);
        var turn = session.Snapshot.Current!.Value;

        var misrouted = Assert.IsType<SendResult.Refused>(await local.Coordinator.Send(other.Address, A, turn, "Local", false).WaitAsync(Bound));
        Assert.Equal("The command names another workflow run.", Assert.IsType<SendProblem.RunUnavailable>(misrouted.Problem).Reason);
        Assert.Equal((CommandOutcome.Refused, "The command names another workflow run."),
            await Outcome(local.Coordinator.MarkDone(other.Address, A, turn)));
        Assert.Equal((0, 0), (local.Lines(A, "messageQueued"), other.Lines(A, "messageQueued")));

        Assert.IsType<SendResult.Queued>(await Send(session, "Local"));
        await local.UntilWaiting(A, 2);
        Assert.Equal(("Local", 2), (local.Prompt(A, 2), local.Launches(A)));
        await other.Decided();
        Assert.Equal((0, 0, 1), (other.Lines(A, "messageQueued"), other.Lines(A, "turnRequested"), other.Launches(A)));
        Assert.Equal(TaskState.Waiting, other.View.Tasks[A].State);
    }

    [Fact]
    public async Task Mark_done_publishes_a_waiting_chat_task_and_hands_on()
    {
        await using var f = new RunConversationFixture(Graph([RunConversationFixture.Agent(A, ConversationMode.Chat),
            RunConversationFixture.Agent(B, ConversationMode.Autonomous)], (A, B)));
        f.Answer(A, FakeRule.On().Print(FakeAgents.SessionLine(ClientId.Codex, "session-1")).Write("a.txt", "A\n")
                .Print(FakeAgents.ReplyLines(ClientId.Codex, "A ready.\n")))
            .Answer(B, f.Says(B, 1, "B ready.\n", "session-B"));
        await f.Open();
        await f.Resume();
        await f.UntilWaiting(A, 1);
        Assert.Equal(0, f.Launches(B));
        using var session = f.Session(A);
        Assert.True(session.Snapshot.Actions.MarkDone.Enabled, session.Snapshot.Actions.MarkDone.Reason);
        Assert.Equal(CommandOutcome.Stale, (await session.MarkDoneAsync(new TurnKey(f.Attempt(A), 2), default).WaitAsync(Bound)).Outcome);

        Assert.Equal((CommandOutcome.Applied, "The task was marked done."), await Outcome(session.MarkDoneAsync(session.Snapshot.Current!.Value, default)));
        await f.UntilStatus(RunStatus.Completed);
        Assert.Equal(1, f.Launches(B));
        Assert.Equal(1, f.Lines(A, "markedDone"));
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(f.Checkout(B), "a.txt")));
        Assert.Contains("A ready.\n", f.Prompt(B, 1));
    }

    [Fact]
    public async Task Stop_and_Cancel_close_a_waiting_attempt_without_delivering_its_reply()
    {
        foreach (var stop in new[] { true, false })
        {
            await using var f = new RunConversationFixture(Graph([RunConversationFixture.Agent(A, ConversationMode.Chat),
                RunConversationFixture.Agent(X, ConversationMode.Autonomous, readOnly: true)]));
            f.Answer(A, f.Says(A, 1, "Which fixture?"), f.Says(A, 2, "Done.")).Route("Use the fixture", A)
                .Answer(X, f.Says(X, 1, "X ready.\n", "session-X", gate: f.Gate("x")));
            await f.Open();
            await f.Resume();
            await f.Until(view => view.Tasks[A].State == TaskState.Waiting && view.Tasks[X].State == TaskState.Running);
            using var session = f.Session(A);
            Assert.IsType<SendResult.Queued>(await Send(session, "Use the fixture"));
            Assert.Equal((CommandOutcome.Refused, WorkflowRunCoordinator.ReplyPendingMessage), await Outcome(session.MarkDoneAsync(session.Snapshot.Current!.Value, default)));
            // Only a cancellation leaves a reply unsent; Mark done never closes over one.
            Assert.Equal(RunProblem.InvalidClaim, Assert.IsType<RestingClose.Refused>(await f.Runs.CloseResting(f.Coordinator.Permit!, f.Preparation.Op(),
                f.Attempt(A), new RestingEnd.MarkDone()).WaitAsync(Bound)).Reason.Problem);
            if (stop)
            {
                Assert.IsType<RunCommand.Accepted>(await f.Coordinator.Stop(f.Address, f.Preparation.Op()).WaitAsync(Bound));
                await f.UntilStatus(RunStatus.Stopped);
            }
            else
            {
                Assert.Equal(CommandOutcome.Stale, (await session.CancelAsync(new TurnKey(f.Attempt(A), 2), default).WaitAsync(Bound)).Outcome);
                Assert.Equal((CommandOutcome.Applied, "The cancellation was recorded."), await Outcome(session.CancelAsync(session.Snapshot.Current!.Value, default)));
                await f.Until(view => view.Tasks[A].State == TaskState.Failed);
                // A repeated closure finds the recorded one, with the unsent reply before its cancellation.
                var again = Assert.IsType<RestingClose.Closed>(await f.Runs.CloseResting(f.Coordinator.Permit!, f.Preparation.Op(), f.Attempt(A),
                    new RestingEnd.Cancel()).WaitAsync(Bound)).Attempt;
                Assert.IsType<Release.Released>(again.Release());
                f.Open("x");
                await f.UntilStatus(RunStatus.NeedsAttention);
            }
            Assert.Equal(TerminalAttemptOutcome.Cancelled, Assert.IsType<AttemptEnd.Logged>(f.Read().Closures[f.Attempt(A)]).Outcome);
            Assert.Equal((1, 0), (f.Launches(A), f.Lines(A, "turnRequested")));
            var page = Assert.IsType<HistoryResult.Page>(await session.ReadPageAsync(f.Attempt(A), new HistoryQuery.Latest(), 50, default));
            Assert.Equal(MessageState.NotDelivered, page.Entries.Select(entry => entry.Content).OfType<ConversationContent.Message>()
                .Single(message => message.Text == "Use the fixture").State);
        }
    }

    [Fact]
    public async Task A_message_is_refused_while_a_turn_starts_or_settles_or_for_another_turn()
    {
        await using var f = new RunConversationFixture(Chat(A));
        f.Answer(A, f.Says(A, 1, "Which fixture?"), f.Says(A, 2, "Done."), f.Says(A, 3, "Again.")).Route("Use the fixture", A).Route("Again", A);
        await f.Open();
        using var session = f.Session(A);
        Assert.Equal(WorkflowRunCoordinator.NotStartedMessage, session.Snapshot.Actions.Send.Reason);
        using (var settling = new TurnFixture.ProbeBarrier("runner.checkpoint.before"))
        {
            f.Runs.Probe = settling.Probe;
            await f.Resume();
            await settling.Reached.Task.WaitAsync(Bound);
            await f.Until(view => view.Tasks[A].State == TaskState.Settling);
            var turn = session.Snapshot.Current!.Value;
            Assert.Equal("A", Assert.IsType<SendProblem.Ending>(Assert.IsType<SendResult.Refused>(
                await session.SendAsync(turn, "Use the fixture", false, default).WaitAsync(Bound)).Problem).Title);
        }
        f.Runs.Probe = null;
        await f.UntilWaiting(A, 1);
        var first = session.Snapshot.Current!.Value;
        using (var starting = new TurnFixture.ProbeBarrier("runner.claim.before"))
        {
            f.Runs.Probe = starting.Probe;
            Assert.IsType<SendResult.Queued>(await Send(session, "Use the fixture"));
            await starting.Reached.Task.WaitAsync(Bound);
            Assert.Equal(TaskState.Starting, f.View.Tasks[A].State);
            Assert.Equal(WorkflowRunCoordinator.StartingMessage, Assert.IsType<SendProblem.RunUnavailable>(Assert.IsType<SendResult.Refused>(
                await session.SendAsync(first, "Again", false, default).WaitAsync(Bound)).Problem).Reason);
        }
        f.Runs.Probe = null;
        await f.UntilWaiting(A, 2);
        Assert.IsType<SendProblem.StaleTarget>(Assert.IsType<SendResult.Refused>(await session.SendAsync(first, "Again", false, default).WaitAsync(Bound)).Problem);
        Assert.Equal((1, 2), (f.Lines(A, "messageQueued"), f.Launches(A)));
        Assert.IsType<RunCommand.Accepted>(await f.Coordinator.Stop(f.Address, f.Preparation.Op()).WaitAsync(Bound));
        await f.UntilStatus(RunStatus.Stopped);
        Assert.Equal(WorkflowRunCoordinator.EndedMessage, Assert.IsType<SendProblem.RunUnavailable>(Assert.IsType<SendResult.Refused>(
            await session.SendAsync(session.Snapshot.Current!.Value, "Again", false, default).WaitAsync(Bound)).Problem).Reason);
        Assert.Equal(WorkflowRunCoordinator.EndedMessage, session.Snapshot.Actions.Send.Reason);
    }

    /// <summary>
    /// A resting closure whose log line was written but whose journal closure was refused: the run finishes it from the log,
    /// in the same window once the journal takes writes again, or in the next window.
    /// </summary>
    [Theory]
    [InlineData("cancel", false)]
    [InlineData("cancel", true)]
    [InlineData("cancel-reply", false)]
    [InlineData("cancel-reply", true)]
    [InlineData("mark-done", false)]
    [InlineData("mark-done", true)]
    [InlineData("stop", false)]
    [InlineData("stop", true)]
    public async Task A_closure_the_journal_refused_after_its_log_line_is_finished_from_the_log(string end, bool reopen)
    {
        await using var f = new RunConversationFixture(Graph([RunConversationFixture.Agent(A, ConversationMode.Chat),
            RunConversationFixture.Agent(X, ConversationMode.Autonomous, readOnly: true)]));
        f.Answer(A, FakeRule.On().Print(FakeAgents.SessionLine(ClientId.Codex, "session-1")).Write("a.txt", "A\n")
                .Print(FakeAgents.ReplyLines(ClientId.Codex, "A ready.\n")), f.Says(A, 2, "Done.")).Route("Use the fixture", A)
            .Answer(X, f.Says(X, 1, "X ready.\n", "session-X", gate: f.Gate("x")));
        await f.Open();
        await f.Resume();
        await f.Until(view => view.Tasks[A].State == TaskState.Waiting && view.Tasks[X].State == TaskState.Running);
        using (var session = f.Session(A))
        {
            if (end == "cancel-reply") Assert.IsType<SendResult.Queued>(await Send(session, "Use the fixture"));
            var refusals = 0;
            f.Runs.Probe = point =>
            {
                if (point == "runner.close.attempt.before" && (reopen || Interlocked.Increment(ref refusals) == 1))
                    throw new IOException("The journal refused the closure.");
            };
            var turn = session.Snapshot.Current!.Value;
            switch (end)
            {
                case "mark-done":
                    Assert.Equal((CommandOutcome.Applied, "The task was marked done."), await Outcome(session.MarkDoneAsync(turn, default)));
                    break;
                case "stop":
                    Assert.IsType<RunCommand.Accepted>(await f.Coordinator.Stop(f.Address, f.Preparation.Op()).WaitAsync(Bound));
                    break;
                default:
                    Assert.Equal((CommandOutcome.Applied, "The cancellation was recorded."), await Outcome(session.CancelAsync(turn, default)));
                    break;
            }
            var line = end == "mark-done" ? "markedDone" : "cancelRequested";
            await TurnFixture.WaitUntilAsync(() => f.Lines(A, line) == 1);
            if (reopen)
            {
                // While the journal keeps refusing it, the pending closure is retried after the run's delay, not at once.
                var held = await f.Until(view => view.Tasks[A] is { State: TaskState.Refused, Refusal.Problem: RunProblem.StorageUnavailable });
                Assert.Equal(end == "stop" ? RunStatus.Stopping : RunStatus.Running, held.Status);
                Assert.False(f.Read().Closures.ContainsKey(f.Attempt(A)));
                f.Open("x");
            }
        }
        if (reopen)
        {
            await f.Reopen();
            if (end != "stop") await f.Resume();
        }
        else if (end != "stop") f.Open("x");

        var settled = await f.Until(view => view.Tasks[A].State is TaskState.Done or TaskState.Failed &&
            view.Tasks[X].State is TaskState.Done or TaskState.Failed && view.Status is RunStatus.Completed or RunStatus.NeedsAttention or RunStatus.Stopped);
        var closure = Assert.IsType<AttemptEnd.Logged>(f.Read().Closures[f.Attempt(A)]);
        Assert.Equal(end == "mark-done" ? TerminalAttemptOutcome.Succeeded : TerminalAttemptOutcome.Cancelled, closure.Outcome);
        Assert.Equal((1, 0), (f.Lines(A, end == "mark-done" ? "markedDone" : "cancelRequested"), f.Lines(A, "turnRequested")));
        Assert.Equal(end == "mark-done" ? TaskState.Done : TaskState.Failed, settled.Tasks[A].State);
        if (end == "mark-done") Assert.Equal("A\n", f.Preparation.Git.Git("show", Assert.IsType<CodeOutput.Produced>(f.Read().CurrentResults[A].Code).Code.Commit.Hex + ":a.txt"));
        if (end == "stop") Assert.Equal(RunStatus.Stopped, settled.Status);
        if (end == "cancel-reply") Assert.Equal(1, f.Lines(A, "messageQueued"));
    }

    [Fact]
    public async Task Cancel_stops_a_running_turn_and_its_dependents_wait()
    {
        await using var f = new RunConversationFixture(Graph([RunConversationFixture.Agent(A, ConversationMode.Chat),
            RunConversationFixture.Agent(B, ConversationMode.Autonomous)], (A, B)));
        f.Answer(A, f.Says(A, 1, "Never.", gate: f.Gate("never"))).Answer(B, f.Says(B, 1, "B ready.\n", "session-B"));
        await f.Open();
        await f.Resume();
        await f.Until(view => view.Tasks[A].State == TaskState.Running);
        using var session = f.Session(A);
        await TurnFixture.WaitUntilAsync(() => session.Snapshot.Latest?.SessionId == "session-1");
        Assert.Equal(CommandOutcome.Stale, (await session.CancelAsync(new TurnKey(f.Attempt(A), 2), default).WaitAsync(Bound)).Outcome);
        Assert.Equal((CommandOutcome.Refused, WorkflowRunCoordinator.NotWaitingMessage), await Outcome(session.MarkDoneAsync(session.Snapshot.Current!.Value, default)));

        Assert.Equal((CommandOutcome.Applied, "The cancellation was recorded."), await Outcome(session.CancelAsync(session.Snapshot.Current!.Value, default)));
        var stopped = await f.UntilStatus(RunStatus.NeedsAttention);
        Assert.Equal(TerminalAttemptOutcome.Cancelled, Assert.IsType<AttemptEnd.Logged>(stopped.Tasks[A].End).Outcome);
        Assert.Equal((TaskState.Pending, 0), (stopped.Tasks[B].State, f.Launches(B)));
        Assert.Empty(f.Read().Results);
    }

    [Fact]
    public async Task A_stop_recorded_while_a_reply_is_written_keeps_the_reply_out()
    {
        await using var f = new RunConversationFixture(Chat(A));
        f.Answer(A, f.Says(A, 1, "Which fixture?"), f.Says(A, 2, "Done.")).Route("Use the fixture", A);
        await f.Open();
        await f.Resume();
        await f.UntilWaiting(A, 1);
        using var session = f.Session(A);
        Task<SendResult> send;
        using (var writing = new TurnFixture.ProbeBarrier("coordinator.queue.before"))
        {
            f.Runs.Probe = writing.Probe;
            send = Send(session, "Use the fixture");
            await writing.Reached.Task.WaitAsync(Bound);
            Assert.IsType<RunCommand.Accepted>(await f.Coordinator.Stop(f.Address, f.Preparation.Op()).WaitAsync(Bound));
        }
        Assert.Equal(WorkflowRunCoordinator.StoppingMessage, Assert.IsType<SendProblem.RunUnavailable>(Assert.IsType<SendResult.Refused>(await send).Problem).Reason);
        await f.UntilStatus(RunStatus.Stopped);
        Assert.Equal((0, 1), (f.Lines(A, "messageQueued"), f.Launches(A)));
    }

    [Fact]
    public async Task A_prepared_next_turn_keeps_its_prompt_after_a_restart()
    {
        await using var f = new RunConversationFixture(Chat(A));
        f.Answer(A, f.Says(A, 1, "Which fixture?"), f.Says(A, 2, "Done.")).Route("Use the fixture", A).Route("Again", A);
        await f.Open();
        await f.Resume();
        await f.UntilWaiting(A, 1);
        using (var session = f.Session(A))
        {
            f.Runs.Probe = point => { if (point == "runner.prepared") throw new IOException("The next turn could not start."); };
            Assert.IsType<SendResult.Queued>(await Send(session, "Use the fixture"));
            await TurnFixture.WaitUntilAsync(() => f.Read().Preparations.ContainsKey(new(f.Attempt(A), 2)));
        }

        await f.Reopen();
        await f.Decided();
        using var reopened = f.Session(A);
        Assert.Equal(WorkflowRunCoordinator.StartingMessage, Assert.IsType<SendProblem.RunUnavailable>(Assert.IsType<SendResult.Refused>(
            await Send(reopened, "Again")).Problem).Reason);
        await f.Resume();
        await f.UntilWaiting(A, 2);
        Assert.Equal(("Use the fixture", 2, 1), (f.Prompt(A, 2), f.Launches(A), f.Lines(A, "messageQueued")));
    }

    [Fact]
    public async Task A_continuation_waits_for_the_release_this_decision_takes_up_again()
    {
        await using var f = new RunConversationFixture(Chat(A));
        f.Answer(A, f.Says(A, 1, "First.", gate: f.Gate("first")), f.Says(A, 2, "Added.")).Route("Also add tests", A);
        var releases = 0;
        var continued = 0;
        var heldRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var letGo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await f.Open();
        f.Runs.Probe = point =>
        {
            if (point == "coordinator.continue") Interlocked.Increment(ref continued);
            if (point != "coordinator.release.before") return;
            switch (Interlocked.Increment(ref releases))
            {
                case 1: throw new IOException("The release could not read the journal.");
                case 2:
                    heldRelease.TrySetResult();
                    letGo.Task.WaitAsync(Bound).GetAwaiter().GetResult();
                    break;
            }
        };
        await f.Resume();
        await f.Until(view => view.Tasks[A].State == TaskState.Running);
        using var session = f.Session(A);
        await TurnFixture.WaitUntilAsync(() => session.Snapshot.Latest?.SessionId == "session-1");
        Assert.IsType<SendResult.Queued>(await Send(session, "Also add tests"));
        f.Open("first");

        // The held release keeps the turn's lease. Its retry takes the release up again, and the next turn waits for it.
        await heldRelease.Task.WaitAsync(Bound);
        await f.Decided();
        Assert.Equal((0, 1, TaskState.Settling), (Volatile.Read(ref continued), f.Launches(A), f.View.Tasks[A].State));
        letGo.TrySetResult();
        await f.UntilWaiting(A, 2);
        Assert.Equal((1, 2, "Also add tests"), (Volatile.Read(ref continued), f.Launches(A), f.Prompt(A, 2)));
    }

    [Fact]
    public async Task History_reads_a_run_journal_again_only_after_it_grew()
    {
        await using var f = new RunConversationFixture(Chat(A));
        f.Answer(A, f.Says(A, 1, "Which fixture?"), f.Says(A, 2, "Done.")).Route("Use the fixture", A);
        await f.Open();
        await f.Resume();
        await f.UntilWaiting(A, 1);
        var journals = 0;
        f.Runs.Probe = point => { if (point == "history.journal.read") Interlocked.Increment(ref journals); };
        using var session = f.Runs.OpenConversation(A);
        Assert.Equal(["Run 1"], (await session.ListAttemptsAsync(default)).Select(attempt => attempt.Label));
        Assert.Equal(["Run 1"], (await session.ListAttemptsAsync(default)).Select(attempt => attempt.Label));
        Assert.Equal(1, Volatile.Read(ref journals));
        using (var run = f.Session(A)) Assert.IsType<SendResult.Queued>(await Send(run, "Use the fixture"));
        await f.UntilWaiting(A, 2);
        Assert.Equal(AttemptStatus.WaitingForInput, Assert.Single(await session.ListAttemptsAsync(default)).Status);
        Assert.Equal(2, Volatile.Read(ref journals));
    }

    [Fact]
    public async Task A_snapshot_of_a_turn_running_here_reads_no_log()
    {
        await using var f = new RunConversationFixture(Chat(A));
        f.Answer(A, f.Says(A, 1, "Never.", gate: f.Gate("never")));
        await f.Open();
        await f.Resume();
        await f.Until(view => view.Tasks[A].State == TaskState.Running);
        using var session = f.Session(A);
        await TurnFixture.WaitUntilAsync(() => session.Snapshot.Latest?.SessionId == "session-1");
        var reads = 0;
        f.Runs.Probe = point => { if (point == "history.attempt.read") Interlocked.Increment(ref reads); };
        for (var i = 0; i < 5; i++) Assert.Equal((AttemptStatus.Running, "session-1"), (session.Snapshot.Latest!.Status, session.Snapshot.Latest.SessionId));
        Assert.Equal(0, Volatile.Read(ref reads));
        Assert.IsType<HistoryResult.Page>(await session.ReadPageAsync(f.Attempt(A), new HistoryQuery.Latest(), 50, default));
        Assert.Equal(1, Volatile.Read(ref reads));
    }

    [Fact]
    public async Task The_run_s_decisions_notify_the_task_s_conversation()
    {
        await using var f = new RunConversationFixture(Chat(A));
        f.Answer(A, f.Says(A, 1, "Which fixture?"));
        await f.Open();
        await f.Resume();
        await f.UntilWaiting(A, 1);
        using var session = f.Session(A);
        var changes = 0;
        session.Changed += _ => Interlocked.Increment(ref changes);
        await f.Decided();
        await f.Decided();
        await Task.Delay(300);
        Assert.Equal(0, Volatile.Read(ref changes));

        Assert.IsType<RunCommand.Accepted>(await f.Coordinator.Stop(f.Address, f.Preparation.Op()).WaitAsync(Bound));
        await f.UntilStatus(RunStatus.Stopped);
        await TurnFixture.WaitUntilAsync(() => Volatile.Read(ref changes) > 0);
        Assert.Equal(WorkflowRunCoordinator.EndedMessage, session.Snapshot.Actions.Send.Reason);
    }

    [Fact]
    public async Task A_run_turn_in_this_window_streams_into_its_conversation()
    {
        await using var f = new RunConversationFixture(Chat(A));
        f.Answer(A, FakeRule.On().Print(FakeAgents.SessionLine(ClientId.Codex, "session-1")).WaitForFile(f.Gate("delta"))
            .Print("""{"method":"item/agentMessage/delta","params":{"itemId":"item_1","delta":"Hel"}}""").WaitForFile(f.Gate("rest"))
            .Print(FakeAgents.ReplyLines(ClientId.Codex, "Hello.")));
        await f.Open();
        using var session = f.Session(A);
        await f.Resume();
        await f.Until(view => view.Tasks[A].State == TaskState.Running);
        await TurnFixture.WaitUntilAsync(() => session.Snapshot.Latest?.SessionId == "session-1");
        // Only the turn itself changes now: the run decides nothing until its root exits.
        var changes = 0;
        session.Changed += _ => Interlocked.Increment(ref changes);
        f.Open("delta");
        ConversationEntry? streamed = null;
        await TurnFixture.WaitUntilAsync(() =>
        {
            var page = session.ReadPageAsync(f.Attempt(A), new HistoryQuery.Latest(), 50, default).GetAwaiter().GetResult();
            streamed = (page as HistoryResult.Page)?.Entries.LastOrDefault(entry => entry.Content is ConversationContent.Message { Author: MessageAuthor.Agent });
            return streamed is not null;
        });
        Assert.Equal(new ConversationContent.Message(MessageAuthor.Agent, "Hel", MessageState.Streaming), streamed!.Content);
        await TurnFixture.WaitUntilAsync(() => Volatile.Read(ref changes) > 0);
        f.Open("rest");
        await f.UntilWaiting(A, 1);
        var settled = Assert.IsType<HistoryResult.Page>(await session.ReadPageAsync(f.Attempt(A), new HistoryQuery.Latest(), 50, default));
        Assert.Equal(new ConversationContent.Message(MessageAuthor.Agent, "Hello.", MessageState.Complete), settled.Entries[^1].Content);
    }

    private static async Task<(CommandOutcome, string)> Outcome(Task<ConversationCommandResult> command)
    {
        var result = await command.WaitAsync(Bound);
        return (result.Outcome, result.Detail);
    }

    [Fact]
    public async Task A_continuation_whose_start_was_refused_takes_text_queued_meanwhile()
    {
        await using var f = new RunConversationFixture(Chat(A, B));
        f.Answer(A, f.Says(A, 1, "Which fixture?"), f.Says(A, 2, "Done."))
            .Answer(B, f.Says(B, 1, "Which file?", session: "session-b"), f.Says(B, 2, "Done B.", session: "session-b", gate: "b-2"))
            .Route("Use the fixture", A).Route("Use b.txt", B);
        await f.Open();
        await f.Resume();
        await f.UntilWaiting(A, 1);
        await f.UntilWaiting(B, 1);
        // A's continuation finds its task busy, before it records anything, and waits to be tried again.
        RunLease? held = null;
        f.Runs.Probe = point =>
        {
            if (point == "coordinator.continue" && held is null) held = ((LeaseTake.Taken)f.Coordinator.Permit!.TakeTask(A)).Lease;
        };
        Assert.IsType<SendResult.Queued>(await f.Coordinator.Send(f.Address, A, new TurnKey(f.Attempt(A), 1), "Use the fixture", false).WaitAsync(Bound));
        await f.Until(view => view.Tasks[A].State == TaskState.Refused);
        // B takes the slot meanwhile, and once A's task is free the person writes to A again.
        Assert.IsType<SendResult.Queued>(await f.Coordinator.Send(f.Address, B, new TurnKey(f.Attempt(B), 1), "Use b.txt", false).WaitAsync(Bound));
        await f.Until(view => view.Tasks[B].State == TaskState.Running);
        held!.Dispose();
        await f.Until(view => view.Tasks[A].State == TaskState.Waiting);
        Assert.IsType<SendResult.Queued>(await f.Coordinator.Send(f.Address, A, new TurnKey(f.Attempt(A), 1), "Also add tests", false).WaitAsync(Bound));
        f.Open("b-2");

        await f.UntilWaiting(A, 2);
        Assert.Equal("Use the fixture\n\nAlso add tests", f.Prompt(A, 2));
        Assert.Equal((2, 2), (f.Launches(A), f.Launches(B)));
    }
}
