using System.Collections.Immutable;
using IDevelop.Execution;
using IDevelop.Projects;
using IDevelop.TestSupport;
using static IDevelop.Core.Tests.AttemptEvents;

namespace IDevelop.Core.Tests;

public class ConversationHistoryTests
{
    private const string Scope = "project/task/owner";
    private static readonly AttemptId B = new(Guid.Parse("019aa000-0000-7000-8000-000000000002"));
    private static readonly AttemptId C = new(Guid.Parse("019aa000-0000-7000-8000-000000000003"));

    [Fact]
    public void Cleanup_diagnostics_leave_the_running_conversation_unchanged()
    {
        AttemptEvent[] events = [BuildRequested(First), LaunchedAt1s, Said(2, new AgentEvent.Message("Working."))];
        var cleanedUp = new AttemptEvent.CleanedUp(T0.AddSeconds(3), CleanupResult.Incomplete, "x", [new CleanupStep(T0.AddSeconds(3), "killTree", null)]);

        var before = Project(events);
        var after = Project([.. events, cleanedUp]);

        Assert.Equal(["Working."], Messages(after, MessageAuthor.Agent).Select(message => message.Text));
        Assert.Equivalent(before.Record, after.Record);
        Assert.Equal(before.Record.AppliedEvents, after.Record.AppliedEvents);
        Assert.Equal(before.Rows.ToArray(), after.Rows.ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_consumed_queue_keeps_two_submitted_person_messages_and_omits_the_joined_prompt(bool ids)
    {
        var history = Project([BuildRequested(First), LaunchedAt1s,
            Said(2, new AgentEvent.SessionStarted("session-1")),
            Sent(3, "banana") with { Id = ids ? "banana" : null },
            Sent(4, "and an apple") with { Id = ids ? "apple" : null },
            Said(5, new AgentEvent.Succeeded("Done.")), Exit(6, 0),
            NextTurn(7, "banana\n\nand an apple") with { Consumed = ids ? ["banana", "apple"] : default }]);

        Assert.Equal(["banana", "and an apple"], Messages(history, MessageAuthor.Person).Select(message => message.Text));
        Assert.Equal([MessageState.Submitted, MessageState.Submitted], Messages(history, MessageAuthor.Person).Select(message => message.State));
    }

    [Fact]
    public void Legacy_queue_matching_is_exact_and_does_not_deduplicate_direct_person_messages()
    {
        var history = Project([BuildRequested(First), LaunchedAt1s, Sent(2, "banana"),
            NextTurn(3, "banana\n"), NextTurn(4, "banana"), NextTurn(5, "banana\n")]);

        Assert.Equal(["banana", "banana\n", "banana", "banana\n"], Messages(history, MessageAuthor.Person).Select(message => message.Text));
        Assert.Equal([MessageState.NotDelivered, MessageState.Submitted, MessageState.Submitted, MessageState.Submitted], Messages(history, MessageAuthor.Person).Select(message => message.State));
    }

    [Fact]
    public void An_antigravity_result_only_reply_is_an_agent_message()
    {
        var history = Project([BuildRequested(First), Said(1, new AgentEvent.Succeeded("Wrote banana.")), Exit(2, 0)]);

        Assert.Equal(["Wrote banana."], Messages(history, MessageAuthor.Agent).Select(message => message.Text));
    }

    [Fact]
    public void Only_the_final_result_copy_is_suppressed_and_repeated_complete_messages_remain()
    {
        var history = Project([BuildRequested(First), Said(1, new AgentEvent.Message("Done.")),
            Said(2, new AgentEvent.Message("Done.")), Said(3, new AgentEvent.Succeeded("Done.")), Exit(4, 0)]);

        Assert.Equal(["Done.", "Done."], Messages(history, MessageAuthor.Agent).Select(message => message.Text));
        Assert.Equal(["c1/019aa000-0000-7000-8000-000000000001/1/message/p1",
            "c1/019aa000-0000-7000-8000-000000000001/1/message/p2"], AgentEntries(history).Select(entry => entry.Id.Value));
        var distinctResult = Project([BuildRequested(First), Said(1, new AgentEvent.Message("Done.")),
            Said(2, new AgentEvent.Message("More.")), Said(3, new AgentEvent.Succeeded("Done."))]);
        Assert.Equal(["Done.", "More.", "Done."], Messages(distinctResult, MessageAuthor.Agent).Select(message => message.Text));
    }

    [Fact]
    public void Overlapping_buffers_keep_literal_presentation_positions_when_completed_in_reverse()
    {
        AttemptEvent[] events = [BuildRequested(First), LaunchedAt1s];
        var live = new Dictionary<string, LiveMessageBuffer>
        {
            ["z"] = new("First", 2, T0) { PresentationSequence = 1 },
            ["a"] = new("Second", 2, T0) { PresentationSequence = 2 },
        };
        var streaming = Project(events, live);
        var page = Page(streaming, new HistoryQuery.Latest(), 2);
        var complete = Project([.. events,
            new AttemptEvent.Agent(T0, new AgentEvent.Message("Second") { Id = "a" }) { Order = 2, PresentationSequence = 2 },
            new AttemptEvent.Agent(T0, new AgentEvent.Message("First") { Id = "z" }) { Order = 2, PresentationSequence = 1 }]);
        var refreshed = Page(complete, new HistoryQuery.RefreshWindow(page.Window), 2);
        string[] ids = ["c1/019aa000-0000-7000-8000-000000000001/1/message/ieg",
            "c1/019aa000-0000-7000-8000-000000000001/1/message/iYQ"];
        Assert.Equal(ids, AgentEntries(streaming).Select(entry => entry.Id.Value));
        Assert.Equal([4L, 4L], AgentEntries(streaming).Select(entry => entry.Order));
        Assert.Equal([(2L, 1), (2L, 2)], streaming.Rows.Where(row => row.Entry.Content is ConversationContent.Message { Author: MessageAuthor.Agent })
            .Select(row => (row.Position, row.Slot)));
        Assert.Equal(ids, AgentEntries(complete).Select(entry => entry.Id.Value));
        Assert.Equal([4L, 4L], AgentEntries(complete).Select(entry => entry.Order));
        Assert.Equal([(2L, 1), (2L, 2)], complete.Rows.Where(row => row.Entry.Content is ConversationContent.Message { Author: MessageAuthor.Agent })
            .Select(row => (row.Position, row.Slot)));
        Assert.Equal(ids, refreshed.Entries.Select(entry => entry.Id.Value));
        Assert.Equal(["First", "Second"], refreshed.Entries.Select(entry => Assert.IsType<ConversationContent.Message>(entry.Content).Text));
        Assert.Equal(page.Window, refreshed.Window);
        Assert.Equal(page.Before, refreshed.Before);
        Assert.Equal(page.After, refreshed.After);
    }

    [Fact]
    public void A_live_message_completes_with_the_same_identity_and_presentation_order_and_refreshes_its_window()
    {
        AttemptEvent[] events = [BuildRequested(First), LaunchedAt1s];
        var live = new Dictionary<string, LiveMessageBuffer> { ["m1"] = new("Hello", 2, T0.AddSeconds(2)) };
        var streaming = Project(events, live);
        var page = Page(streaming, new HistoryQuery.Latest(), 1);
        var complete = Project([.. events, Said(3, new AgentEvent.ToolStarted("read", null)),
            new AttemptEvent.Agent(T0.AddSeconds(4), new AgentEvent.Message("Hello") { Id = "m1" }) { Order = 2 }], live);
        var refreshed = Page(complete, new HistoryQuery.RefreshWindow(page.Window), 1);

        Assert.Equal(["Hello"], Messages(streaming, MessageAuthor.Agent).Select(message => message.Text));
        Assert.Equal(["Hello"], Messages(complete, MessageAuthor.Agent).Select(message => message.Text));
        Assert.Equal(("c1/019aa000-0000-7000-8000-000000000001/1/message/ibTE", 4L, MessageState.Streaming),
            AgentRow(streaming));
        Assert.Equal(("c1/019aa000-0000-7000-8000-000000000001/1/message/ibTE", 4L, MessageState.Complete),
            AgentRow(complete));
        Assert.Equal("Hello", Assert.IsType<ConversationContent.Message>(Assert.Single(refreshed.Entries).Content).Text);
        Assert.Equal(MessageState.Complete, Assert.IsType<ConversationContent.Message>(refreshed.Entries[0].Content).State);
        Assert.Equal(["Hello", "read"], complete.Entries.Skip(2).Select(entry => entry.Content switch
        {
            ConversationContent.Message message => message.Text,
            ConversationContent.Activity activity => activity.Text,
            _ => "unexpected",
        }));
    }

    [Fact]
    public void Claude_content_blocks_have_distinct_identities_even_with_one_wire_message_id()
    {
        var history = Project([BuildRequested(First), Said(1, new AgentEvent.Message("First") { Id = "m:0" }),
            Said(2, new AgentEvent.Message("Second") { Id = "m:1" })]);

        Assert.Equal(["First", "Second"], Messages(history, MessageAuthor.Agent).Select(message => message.Text));
        Assert.Equal(["c1/019aa000-0000-7000-8000-000000000001/1/message/ibTow",
            "c1/019aa000-0000-7000-8000-000000000001/1/message/ibTox"], AgentEntries(history).Select(entry => entry.Id.Value));
    }

    [Fact]
    public void A_stopped_turn_keeps_its_partial_message()
    {
        var history = Project([BuildRequested(First), LaunchedAt1s, new AttemptEvent.CancelRequested(T0.AddSeconds(2)),
            Said(3, new AgentEvent.Message("Hello") { Id = "m1", Partial = true }), Exit(4, 137)]);

        Assert.Equal(["Hello"], Messages(history, MessageAuthor.Agent).Select(message => message.Text));
        Assert.Equal([MessageState.Partial], Messages(history, MessageAuthor.Agent).Select(message => message.State));
        Assert.Equal(["The turn was stopped."], Markers(history, "stopped").Select(marker => marker.Text));
    }

    [Fact]
    public void Partial_text_does_not_suppress_a_result_only_complete_reply()
    {
        var history = Project([BuildRequested(First), Said(1, new AgentEvent.Message("Hello") { Partial = true }),
            Said(2, new AgentEvent.Succeeded("Hello"))]);

        Assert.Equal(["Hello", "Hello"], Messages(history, MessageAuthor.Agent).Select(message => message.Text));
        Assert.Equal([MessageState.Partial, MessageState.Complete], Messages(history, MessageAuthor.Agent).Select(message => message.State));
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("interrupt")]
    [InlineData("failure")]
    [InlineData("defer")]
    [InlineData("launch")]
    public void An_unconsumed_queue_is_not_delivered_when_a_turn_cannot_deliver_it(string end)
    {
        AttemptEvent[] terminal = end switch
        {
            "cancel" => [new AttemptEvent.CancelRequested(T0.AddSeconds(3)), Exit(4, 137)],
            "interrupt" => [new AttemptEvent.Reconciled(T0.AddSeconds(3), ProcessMatch.Gone)],
            "failure" => [Said(3, new AgentEvent.Failed("Failed to deliver.")), Exit(4, 1)],
            "defer" => [new AttemptEvent.RequestDeferred(T0.AddSeconds(3), [], "Reply later."), Exit(4, 137)],
            "launch" => [new AttemptEvent.LaunchFailed(T0.AddSeconds(3), "Could not launch.")],
            _ => throw new InvalidOperationException(),
        };
        var history = Project([BuildRequested(First), LaunchedAt1s, Sent(2, "banana"), .. terminal]);

        Assert.Equal(["banana"], Messages(history, MessageAuthor.Person).Select(message => message.Text));
        Assert.Equal([MessageState.NotDelivered], Messages(history, MessageAuthor.Person).Select(message => message.State));
    }

    [Fact]
    public async Task Attempt_enumeration_includes_independent_and_cancelled_attempts_and_chains_are_cycle_safe()
    {
        using var temp = new TempFolder();
        var project = temp.Create("project");
        var folder = DataFolder.Attempts(project);
        using (var a = AttemptLog.Create(folder, BuildRequested(First))) { a.Append(Said(1, new AgentEvent.Succeeded("A"))); a.Append(Exit(2, 0)); }
        using (var b = AttemptLog.Create(folder, BuildRequested(B))) { b.Append(new AttemptEvent.CancelRequested(T0)); b.Append(Exit(1, 137)); }
        using (var c = AttemptLog.Create(folder, BuildRequested(C) with { Continues = new Continuation(First, "session-1") })) { c.Append(Said(1, new AgentEvent.Succeeded("C"))); c.Append(Exit(2, 0)); }

        var clients = new ClientDirectory(CommandResolver.Create([], []));
        await using var runs = ProjectRuns.Open(project, clients);
        using var session = runs.OpenConversation(TestTasks.Build);
        var list = await session.ListAttemptsAsync(default);
        Assert.Equal(["019aa000-0000-7000-8000-000000000001", "019aa000-0000-7000-8000-000000000002", "019aa000-0000-7000-8000-000000000003"], list.Select(attempt => attempt.Id.ToString()));
        Assert.Equal([AttemptStatus.Succeeded, AttemptStatus.Cancelled, AttemptStatus.Succeeded], list.Select(attempt => attempt.Status));
        Assert.Equal(["019aa000-0000-7000-8000-000000000001", "019aa000-0000-7000-8000-000000000003"], (Assert.IsType<HistoryResult.Page>(await session.ReadPageAsync(C, new HistoryQuery.Latest(), 100, default))).Entries
            .Select(entry => entry.Turn.Attempt.ToString()).Distinct());

        File.Delete(Path.Combine(AttemptLog.FolderOf(folder, TestTasks.Build, First), "events.jsonl"));
        using (var a = AttemptLog.Create(folder, BuildRequested(First) with { Continues = new Continuation(C, "session-1") })) { a.Append(Said(1, new AgentEvent.Succeeded("A"))); a.Append(Exit(2, 0)); }
        await using var reopened = ProjectRuns.Open(project, clients);
        using var cycle = reopened.OpenConversation(TestTasks.Build);
        Assert.Equal(["019aa000-0000-7000-8000-000000000001", "019aa000-0000-7000-8000-000000000003"],
            (Assert.IsType<HistoryResult.Page>(await cycle.ReadPageAsync(C, new HistoryQuery.Latest(), 100, default))).Entries
                .Select(entry => entry.Turn.Attempt.ToString()).Distinct());
        Assert.Equal("The selected attempt has no readable history.",
            Assert.IsType<HistoryResult.Unavailable>(await cycle.ReadPageAsync(B with { Value = Guid.Empty }, new HistoryQuery.Latest(), 100, default)).Reason);
    }

    [Fact]
    public void Five_hundred_messages_page_backwards_to_the_start_without_duplicates()
    {
        var history = Project([BuildRequested(First), .. ManyMessages(500)]);
        var page = Page(history, new HistoryQuery.Latest(), 50);
        Assert.Equal("m499", Assert.IsType<ConversationContent.Message>(page.Entries[^1].Content).Text);
        Assert.False(page.HasLater);
        var entries = page.Entries.ToList();
        while (page.HasEarlier)
        {
            page = Page(history, new HistoryQuery.Before(page.Before), 50);
            entries.InsertRange(0, page.Entries);
        }

        Assert.Equal(Enumerable.Range(0, 500).Select(i => $"m{i}"), entries.Select(entry => entry.Content).OfType<ConversationContent.Message>()
            .Where(message => message.Author == MessageAuthor.Agent).Select(message => message.Text));
        Assert.Equal(502, entries.Select(entry => entry.Id).Distinct().Count());
        Assert.False(page.HasEarlier);
    }

    [Fact]
    public void After_an_exhausted_tail_reads_a_later_log_append()
    {
        using var temp = new TempFolder();
        var folder = temp.Create("attempts");
        using var log = AttemptLog.Create(folder, BuildRequested(First));
        foreach (var e in ManyMessages(500)) log.Append(e);
        var original = Assert.IsType<AttemptHistory>(AttemptLog.ReadHistory(folder, TestTasks.Build, First));
        var tail = Page(original, new HistoryQuery.Latest(), 50);
        var empty = Page(original, new HistoryQuery.After(tail.After), 50);
        Assert.False(tail.HasLater);
        Assert.Empty(empty.Entries);
        log.Append(Said(501, new AgentEvent.Message("m500") { Id = "m500" }));

        var appended = Assert.IsType<AttemptHistory>(AttemptLog.ReadHistory(folder, TestTasks.Build, First));
        var page = Page(appended, new HistoryQuery.After(empty.After), 50);
        Assert.Equal(["m500"], page.Entries.Select(entry => Assert.IsType<ConversationContent.Message>(entry.Content).Text));
        Assert.False(page.HasLater);
    }

    [Fact]
    public async Task Seeking_an_old_closed_question_finds_its_persisted_request_row_and_fold()
    {
        using var temp = new TempFolder();
        var project = temp.Create("project");
        var folder = DataFolder.Attempts(project);
        using var log = AttemptLog.Create(folder, BuildRequested(First));
        log.Append(Question("q7"));
        log.Append(new AttemptEvent.RequestClosed(T0.AddSeconds(2), "q7", RequestCloseReason.Resolved));
        foreach (var e in ManyMessages(500)) log.Append(e);
        log.Append(Said(502, new AgentEvent.Succeeded("m499")));
        log.Append(Exit(503, 0));
        log.Dispose();
        await using var runs = ProjectRuns.Open(project, new ClientDirectory(CommandResolver.Create([], [])));
        using var session = runs.OpenConversation(TestTasks.Build);
        var tail = Assert.IsType<HistoryResult.Page>(await session.ReadPageAsync(First, new HistoryQuery.Latest(), 50, default));
        var page = Assert.IsType<HistoryResult.Page>(await session.ReadPageAsync(First, new HistoryQuery.AroundRequest(new RequestKey(new TurnKey(First, 1), "q7")), 10, default));

        Assert.Equal("m499", Assert.IsType<ConversationContent.Message>(tail.Entries[^1].Content).Text);
        Assert.Equal("q7", Assert.Single(page.Entries.Select(entry => entry.Content).OfType<ConversationContent.Request>()).Key.Id);
        var question = Assert.IsType<RequestRecord.Question>(await session.ReadRequestAsync(new RequestKey(new TurnKey(First, 1), "q7"), default));
        Assert.Equal(new QuestionState.Closed(RequestCloseReason.Resolved, null), question.State);
        Assert.Equal("Fixture?", Assert.Single(question.Questions).Text);
        var foreign = Assert.IsType<HistoryResult.Unavailable>(await session.ReadPageAsync(First,
            new HistoryQuery.AroundRequest(new RequestKey(new TurnKey(B, 1), "q7")), 10, default));
        Assert.Equal("The request has no persisted entry in the selected attempt chain. Select its attempt to read it.", foreign.Reason);
    }

    [Fact]
    public void Refreshing_a_loaded_queue_window_updates_its_state_without_changing_its_identity()
    {
        using var temp = new TempFolder();
        var folder = temp.Create("attempts");
        using var log = AttemptLog.Create(folder, BuildRequested(First));
        log.Append(Sent(1, "banana") with { Id = "banana" });
        var history = Assert.IsType<AttemptHistory>(AttemptLog.ReadHistory(folder, TestTasks.Build, First));
        var page = Page(history, new HistoryQuery.Latest(), 1);
        log.Append(NextTurn(2, "banana") with { Consumed = ["banana"] });
        var next = Assert.IsType<AttemptHistory>(AttemptLog.ReadHistory(folder, TestTasks.Build, First));
        var refreshed = Page(next, new HistoryQuery.RefreshWindow(page.Window), 50, 2);

        Assert.Equal(("banana", MessageState.Queued), MessageValue(Assert.Single(page.Entries)));
        Assert.Equal(("banana", MessageState.Submitted), MessageValue(Assert.Single(refreshed.Entries)));
        Assert.Equal("c1/019aa000-0000-7000-8000-000000000001/1/queued/iYmFuYW5h", Assert.Single(page.Entries).Id.Value);
        Assert.Equal("c1/019aa000-0000-7000-8000-000000000001/1/queued/iYmFuYW5h", Assert.Single(refreshed.Entries).Id.Value);
        Assert.Equal(2L, refreshed.Revision);
    }

    [Theory]
    [MemberData(nameof(Phase3LogTests.Logs), MemberType = typeof(Phase3LogTests))]
    public void Every_phase3_fixture_projects_with_literal_legacy_instructions(string fixture)
    {
        var history = Assert.IsType<AttemptHistory>(ConversationHistory.Project(AttemptLog.ReadPositioned(Fixture.Path(Path.Combine("phase3-attempts", fixture)))));
        Assert.Equal(["# Say hi\n\nCreate hello.txt containing hi. Then reply with DONE.\n"], Messages(history, MessageAuthor.Application).Select(message => message.Text));
        Assert.Single(Markers(history, "attempt"));
        var page = Page(history, new HistoryQuery.Latest(), 50);
        Assert.False(page.HasEarlier);
        Assert.False(page.HasLater);
        if (fixture == "agy-succeeded") Assert.Equal(["DONE"], Messages(history, MessageAuthor.Agent).Select(message => message.Text));
        if (fixture == "pi-failed-exit-0") Assert.Equal(["OAuth refresh failed for openai-codex: OpenAI Codex token refresh failed (401): {\n  \"error\": {\n    \"message\": \"Could not validate your refresh token. Please try signing in again.\",\n    \"type\": \"invalid_request_error\",\n    \"param\": null,\n    \"code\": \"invalid_refresh_token\"\n  }\n}"], Markers(history, "failure").Select(marker => marker.Text));
        if (fixture == "codex-crashed") Assert.Equal(["iDevelop stopped while this task ran."], Markers(history, "interruption").Select(marker => marker.Text));
    }

    [Fact]
    public void Torn_lines_preserve_physical_positions_for_legacy_identity_and_order()
    {
        using var temp = new TempFolder();
        var folder = temp.Create("attempts");
        string logFolder;
        using (var log = AttemptLog.Create(folder, BuildRequested(First))) { logFolder = log.Folder; }
        File.AppendAllText(Path.Combine(logFolder, "events.jsonl"), "{broken\n{\"type\":\"future\"}\n");
        using (var log = AttemptLog.Open(logFolder)) log.Append(Said(1, new AgentEvent.Message("Hello")));

        var history = Assert.IsType<AttemptHistory>(AttemptLog.ReadHistory(folder, TestTasks.Build, First));
        Assert.Equal(("c1/019aa000-0000-7000-8000-000000000001/1/message/p3", 7L, MessageState.Complete), AgentRow(history));
    }

    [Fact]
    public void A_legacy_queue_after_a_skipped_line_matches_the_consumed_identity_in_the_existing_reducer()
    {
        using var temp = new TempFolder();
        var folder = temp.Create("attempts");
        string logFolder;
        using (var log = AttemptLog.Create(folder, BuildRequested(First))) { logFolder = log.Folder; }
        File.AppendAllText(Path.Combine(logFolder, "events.jsonl"), "{broken\n");
        using (var log = AttemptLog.Open(logFolder))
        {
            log.Append(Sent(1, "banana"));
            log.Append(NextTurn(2, "banana") with { Consumed = ["legacy:019aa000-0000-7000-8000-000000000001:1"] });
        }

        var history = Assert.IsType<AttemptHistory>(AttemptLog.ReadHistory(folder, TestTasks.Build, First));
        Assert.Equal(["banana"], Messages(history, MessageAuthor.Person).Select(message => message.Text));
        Assert.Equal([MessageState.Submitted], Messages(history, MessageAuthor.Person).Select(message => message.State));
        Assert.Equal("c1/019aa000-0000-7000-8000-000000000001/1/queued/p2", Assert.Single(history.Entries.Where(entry => entry.Content is ConversationContent.Message { Author: MessageAuthor.Person })).Id.Value);
    }

    [Fact]
    public void Instructions_review_and_fix_prompts_configuration_and_terminal_handoffs_remain_visible()
    {
        var review = Project([BuildRequested(First) with { Subject = TestTasks.Build, Continues = new Continuation(B, "session-1"), Prompt = "Review this." },
            new AttemptEvent.GuidanceAdded(T0.AddSeconds(1), "Check nulls."),
            Said(2, new AgentEvent.Reported("observed", "low")),
            NextTurn(3, "Review the fix.") with { Report = new FixReport(B, "Fixed.", 1), Conversation = IDevelop.Workflows.ConversationMode.Chat },
            new AttemptEvent.HandedToTerminal(T0.AddSeconds(4), "/project", "codex resume session-1")]);
        var fix = Project([BuildRequested(C) with { Continues = new Continuation(B, "session-1"),
            Fix = new ReviewLink(TestTasks.Build, First, 1, 0), Prompt = "Fix these findings." }]);

        Assert.Equal(["Review this.", "Review the fix."], Messages(review, MessageAuthor.Application).Select(message => message.Text));
        Assert.Equal(["Check nulls."], Messages(review, MessageAuthor.Person).Select(message => message.Text));
        Assert.Equal(["Fix these findings."], Messages(fix, MessageAuthor.Application).Select(message => message.Text));
        Assert.Equal(["Client reported model observed, reasoning low.", "Conversation mode changed to Chat."], Markers(review, "configuration").Select(marker => marker.Text));
        Assert.Equal(["Opened in terminal in /project\ncodex resume session-1"], Markers(review, "terminal").Select(marker => marker.Text));
    }

    [Fact]
    public void A_complete_message_replaces_partial_text_and_named_identity_is_scoped_by_turn()
    {
        var history = Project([BuildRequested(First), Said(1, new AgentEvent.Message("Hel") { Id = "m1", Partial = true }),
            Said(2, new AgentEvent.Message("Hello") { Id = "m1" }), NextTurn(3, "Next"),
            Said(4, new AgentEvent.Message("Again") { Id = "m1" })]);

        Assert.Equal(["Hello", "Again"], Messages(history, MessageAuthor.Agent).Select(message => message.Text));
        Assert.Equal([MessageState.Complete, MessageState.Complete], Messages(history, MessageAuthor.Agent).Select(message => message.State));
        Assert.Equal(["c1/019aa000-0000-7000-8000-000000000001/1/message/ibTE", "c1/019aa000-0000-7000-8000-000000000001/2/message/ibTE"], AgentEntries(history).Select(entry => entry.Id.Value));
    }

    [Fact]
    public void Refresh_keeps_both_window_bounds_after_appends_and_returns_all_rows_inside_them()
    {
        var original = Project([BuildRequested(First), .. ManyMessages(500)]);
        var window = Page(original, new HistoryQuery.Latest(), 50).Window;
        var appended = Project([BuildRequested(First), .. ManyMessages(501)]);
        var refresh = Page(appended, new HistoryQuery.RefreshWindow(window), 1);

        Assert.Equal(50, refresh.Entries.Length);
        Assert.Equal("m450", Assert.IsType<ConversationContent.Message>(refresh.Entries[0].Content).Text);
        Assert.Equal("m499", Assert.IsType<ConversationContent.Message>(refresh.Entries[^1].Content).Text);
        Assert.True(refresh.HasLater);
        Assert.Equal(["m500"], Page(appended, new HistoryQuery.After(refresh.After), 50).Entries
            .Select(entry => Assert.IsType<ConversationContent.Message>(entry.Content).Text));
    }

    [Fact]
    public void History_tokens_reject_malformed_values_foreign_owners_and_different_heads()
    {
        var a = Project([BuildRequested(First), Said(1, new AgentEvent.Message("A"))]);
        var b = Project([BuildRequested(B), Said(1, new AgentEvent.Message("B"))]);
        var page = Page(a, new HistoryQuery.Latest(), 1);
        Assert.Equal("The history cursor is invalid.", Assert.IsType<HistoryResult.Unavailable>(ConversationPager.Read(Scope, [a], 1, new HistoryQuery.After(new HistoryCursor("bad")), 1)).Reason);
        Assert.Equal("The history window is invalid.", Assert.IsType<HistoryResult.Unavailable>(ConversationPager.Read(Scope, [a], 1, new HistoryQuery.RefreshWindow(new HistoryWindow(page.After.Value)), 1)).Reason);
        Assert.Equal("The history cursor belongs to another conversation or attempt chain.", Assert.IsType<HistoryResult.Unavailable>(ConversationPager.Read("other", [a], 1, new HistoryQuery.After(page.After), 1)).Reason);
        Assert.Equal("The history window belongs to another conversation or attempt chain.", Assert.IsType<HistoryResult.Unavailable>(ConversationPager.Read(Scope, [a, b], 1, new HistoryQuery.RefreshWindow(page.Window), 1)).Reason);
        Assert.Equal("The history page size must be positive.", Assert.IsType<HistoryResult.Unavailable>(ConversationPager.Read(Scope, [a], 1, new HistoryQuery.Latest(), 0)).Reason);
        Assert.Equal("A", Assert.IsType<ConversationContent.Message>(Assert.Single(page.Entries).Content).Text);
    }

    [Fact]
    public void Paging_crosses_attempt_boundaries_and_tied_sort_keys_once_each()
    {
        var a = Project([BuildRequested(First), new AttemptEvent.Agent(T0, new AgentEvent.Message("First") { Id = "a" }) { Order = 1 },
            new AttemptEvent.Agent(T0, new AgentEvent.Message("Second") { Id = "b" }) { Order = 1 }]);
        var c = Project([BuildRequested(C) with { Continues = new Continuation(First, "session-1"), Prompt = "Continue" }, Said(1, new AgentEvent.Message("Third"))]);
        var tail = Assert.IsType<HistoryResult.Page>(ConversationPager.Read(Scope, [a, c], 1, new HistoryQuery.Latest(), 3));
        var before = Assert.IsType<HistoryResult.Page>(ConversationPager.Read(Scope, [a, c], 1, new HistoryQuery.Before(tail.Before), 3));
        var start = Assert.IsType<HistoryResult.Page>(ConversationPager.Read(Scope, [a, c], 1, new HistoryQuery.Before(before.Before), 3));
        Assert.Equal(["First", "Second", "Third"], start.Entries.Concat(before.Entries).Concat(tail.Entries)
            .Select(entry => entry.Content).OfType<ConversationContent.Message>().Where(message => message.Author == MessageAuthor.Agent).Select(message => message.Text));
        Assert.False(start.HasEarlier);
        var firstMessage = Assert.IsType<HistoryResult.Page>(ConversationPager.Read(Scope, [a, c], 1, new HistoryQuery.Before(before.After), 1));
        Assert.Equal("First", Assert.IsType<ConversationContent.Message>(Assert.Single(firstMessage.Entries).Content).Text);
        var after = Assert.IsType<HistoryResult.Page>(ConversationPager.Read(Scope, [a, c], 1, new HistoryQuery.After(firstMessage.After), 3));
        Assert.Equal(["Second", "Continue"], after.Entries.Select(entry => entry.Content).OfType<ConversationContent.Message>().Select(message => message.Text));
    }

    private static AttemptEvent.QuestionRecorded Question(string id) => new(T0.AddSeconds(1), id,
        [new AskedQuestion("fixture", "Fixture", "Fixture?", [new QuestionOption("local", "Local", null)], false, true)],
        new QuestionState.Open(new RequestDeadline(T0.AddSeconds(55), T0.AddSeconds(60))));
    private static IEnumerable<AttemptEvent> ManyMessages(int count) => Enumerable.Range(0, count)
        .Select(i => Said(i + 1, new AgentEvent.Message($"m{i}") { Id = $"m{i}" }));
    private static AttemptHistory Project(IEnumerable<AttemptEvent> events, IReadOnlyDictionary<string, LiveMessageBuffer>? live = null) =>
        Assert.IsType<AttemptHistory>(ConversationHistory.Project(events, live));
    private static HistoryResult.Page Page(AttemptHistory history, HistoryQuery query, int count, long revision = 1) =>
        Assert.IsType<HistoryResult.Page>(ConversationPager.Read(Scope, [history], revision, query, count));
    private static IEnumerable<ConversationContent.Message> Messages(AttemptHistory history, MessageAuthor author) => history.Entries
        .Select(entry => entry.Content).OfType<ConversationContent.Message>().Where(message => message.Author == author);
    private static IEnumerable<ConversationEntry> AgentEntries(AttemptHistory history) => history.Entries
        .Where(entry => entry.Content is ConversationContent.Message { Author: MessageAuthor.Agent });
    private static IEnumerable<ConversationContent.Marker> Markers(AttemptHistory history, string kind) => history.Entries
        .Select(entry => entry.Content).OfType<ConversationContent.Marker>().Where(marker => marker.Kind == kind);
    private static (string Id, long Order, MessageState State) AgentRow(AttemptHistory history)
    {
        var entry = Assert.Single(AgentEntries(history));
        return (entry.Id.Value, entry.Order, Assert.IsType<ConversationContent.Message>(entry.Content).State);
    }
    private static (string Text, MessageState State) MessageValue(ConversationEntry entry)
    {
        var message = Assert.IsType<ConversationContent.Message>(entry.Content);
        return (message.Text, message.State);
    }
}
