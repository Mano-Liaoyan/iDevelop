using IDevelop.Execution;
using static IDevelop.Core.Tests.AttemptEvents;
using static IDevelop.Execution.AgentEvent;
using static IDevelop.Execution.AttemptEvent;
using static IDevelop.TestSupport.TestTasks;

namespace IDevelop.Core.Tests;

public class AttemptReducerTests
{
    private const string Closed = "The project was closed while this task ran.";

    private static readonly Dictionary<string, (AttemptEvent[] Events, AttemptStatus Status, string? Detail)> Endings = new()
    {
        ["a success verdict and exit code 0"] =
            ([LaunchedAt1s, Said(2, new Succeeded("Done.")), Exit(3, 0)], AttemptStatus.Succeeded, null),
        ["a failure verdict and exit code 0, as Pi reports a failed turn"] =
            ([LaunchedAt1s, Said(2, new Failed("OAuth refresh failed for openai-codex.")), Exit(3, 0)], AttemptStatus.Failed, "OAuth refresh failed for openai-codex."),
        ["a failure verdict and exit code 1, as Claude Code reports a bad model"] =
            ([LaunchedAt1s, Said(2, new Failed("There's an issue with the selected model.")), Exit(3, 1)], AttemptStatus.Failed, "There's an issue with the selected model."),
        ["a success verdict and a nonzero exit code"] =
            ([LaunchedAt1s, Said(2, new Succeeded("Done.")), Exit(3, 3)], AttemptStatus.Failed, "Codex reported success but exited with code 3."),
        ["no verdict and exit code 0"] =
            ([LaunchedAt1s, Exit(3, 0)], AttemptStatus.Failed, "Codex ended without a result."),
        ["no verdict and a nonzero exit code with stderr"] =
            ([LaunchedAt1s, Exit(3, 2, "warning: slow\nerror: unexpected status 401\n")], AttemptStatus.Failed, "Codex exited with code 2: error: unexpected status 401"),
        ["no verdict and a nonzero exit code without stderr"] =
            ([LaunchedAt1s, Exit(3, 1)], AttemptStatus.Failed, "Codex exited with code 1."),
        ["a cancel after the client's success"] =
            ([LaunchedAt1s, Said(2, new Succeeded("Done.")), new CancelRequested(T0.AddSeconds(3)), Exit(4, -1)], AttemptStatus.Cancelled, null),
        ["a cancel and then a leave"] =
            ([LaunchedAt1s, new CancelRequested(T0.AddSeconds(2)), new InterruptRequested(T0.AddSeconds(3), Closed), Exit(4, -1)], AttemptStatus.Interrupted, Closed),
        ["a launch that failed"] =
            ([new LaunchFailed(T0.AddSeconds(1), "codex did not start: access denied")], AttemptStatus.Failed, "codex did not start: access denied"),
        ["a crash before the launch was recorded"] =
            ([new Reconciled(T0.AddSeconds(9), null)], AttemptStatus.Interrupted,
                "iDevelop stopped while starting the client. If the client started, it may still be running."),
        ["a crash while the client kept running"] =
            ([LaunchedAt1s, new Reconciled(T0.AddSeconds(9), ProcessMatch.Same)], AttemptStatus.Interrupted,
                "iDevelop stopped while this task ran. Its client was still running and was stopped."),
        ["a crash after the client ended"] =
            ([LaunchedAt1s, new Reconciled(T0.AddSeconds(9), ProcessMatch.Gone)], AttemptStatus.Interrupted, "iDevelop stopped while this task ran."),
        ["a crash after another program took the client's process id"] =
            ([LaunchedAt1s, new Reconciled(T0.AddSeconds(9), ProcessMatch.Reused)], AttemptStatus.Interrupted,
                "iDevelop stopped while this task ran. Process 4242 now belongs to another program and was left alone."),
        ["a leave that gave up on a client that ended later"] =
            ([LaunchedAt1s, new InterruptRequested(T0.AddSeconds(2), Closed), new Reconciled(T0.AddSeconds(9), ProcessMatch.Gone)], AttemptStatus.Interrupted,
                "The project was closed while this task ran. Its client did not stop in time, and iDevelop settled it when the project was opened again."),
        ["a leave that gave up on a client that kept running"] =
            ([LaunchedAt1s, new InterruptRequested(T0.AddSeconds(2), Closed), new Reconciled(T0.AddSeconds(9), ProcessMatch.Same)], AttemptStatus.Interrupted,
                "The project was closed while this task ran. Its client did not stop in time, and iDevelop settled it when the project was opened again. Its client was still running and was stopped."),
    };

    public static TheoryData<string> EndingNames => new(Endings.Keys);

    [Fact]
    public void A_successful_attempt_records_its_session_reported_settings_activity_and_result()
    {
        var record = Replay(
            LaunchedAt1s,
            Said(2, new SessionStarted("thread-1")),
            Said(3, new Reported("deepseek/deepseek-v4-pro", "high")),
            Said(4, new ToolStarted("command", "dotnet test")),
            Said(5, new Message("All tests pass.\nDetails follow.")),
            Said(6, new Succeeded(null)),
            Exit(7, 0));

        Assert.Equal(AttemptStatus.Succeeded, record.Status);
        Assert.Equal("All tests pass.\nDetails follow.", record.Result);
        Assert.Null(record.Detail);
        Assert.Equal(T0.AddSeconds(7), record.EndedAt);
        Assert.Equal("thread-1", record.SessionId);
        Assert.Equal(("deepseek/deepseek-v4-pro", "high"), (record.ReportedModel, record.ReportedReasoning));
        Assert.Equal(
            [
                new ActivityLine(T0.AddSeconds(4), "command: dotnet test"),
                new ActivityLine(T0.AddSeconds(5), "All tests pass."),
            ],
            record.Activity.ToArray());
        Assert.Equal((First, Build, "Implement atomic save", CodexHigh, T0), (record.Id, record.Task, record.TaskTitle, record.Requested, record.RequestedAt));
    }

    [Theory]
    [MemberData(nameof(EndingNames))]
    public void The_exit_policy_settles_each_ending(string ending)
    {
        var (events, status, detail) = Endings[ending];

        var record = Replay(events);

        Assert.Equal((status, detail), (record.Status, record.Detail));
    }

    [Fact]
    public void A_failed_attempt_keeps_the_clients_last_message_as_its_result()
    {
        var record = Replay(LaunchedAt1s, Said(2, new Message("I could not reach the API.")), Exit(3, 1, "error: timeout"));

        Assert.Equal((AttemptStatus.Failed, "I could not reach the API.", "Codex exited with code 1: error: timeout"), (record.Status, record.Result, record.Detail));
    }

    [Fact]
    public void A_stop_request_shows_as_stopping_until_the_exit_settles_it()
    {
        var stopping = Replay(LaunchedAt1s, new CancelRequested(T0.AddSeconds(2)));

        Assert.Equal((AttemptStatus.Running, true), (stopping.Status, stopping.Stopping));
        var cancelled = AttemptReducer.Apply(stopping, Exit(3, -1));
        Assert.Equal((AttemptStatus.Cancelled, false, T0.AddSeconds(3)), (cancelled.Status, cancelled.Stopping, cancelled.EndedAt));
    }

    [Fact]
    public void A_settled_attempt_ignores_later_events_so_replays_and_races_converge()
    {
        var settled = Replay(LaunchedAt1s, Said(2, new Succeeded("Done.")), Exit(3, 0));

        var after = new AttemptEvent[] { Said(4, new Failed("late")), new CancelRequested(T0.AddSeconds(5)), Exit(6, 1), new Reconciled(T0.AddSeconds(7), ProcessMatch.Same) }
            .Aggregate(settled, AttemptReducer.Apply);

        Assert.Same(settled, after);
    }

    [Fact]
    public void Activity_keeps_the_latest_hundred_lines()
    {
        var record = Replay([LaunchedAt1s, .. Enumerable.Range(0, 120).Select(i => Said(2, new Notice($"notice {i}")))]);

        Assert.Equal(100, record.Activity.Count);
        Assert.Equal(("notice 20", "notice 119"), (record.Activity[0].Text, record.Activity[^1].Text));
    }

    [Fact]
    public void A_message_sent_during_a_turn_waits_and_then_starts_the_next_turn_instead_of_settling()
    {
        var waiting = Replay([.. AskedWhichFruit, Sent(4, "banana")]);

        Assert.Equal((AttemptStatus.Running, "banana"), (waiting.Status, Waiting(waiting)));
        var between = AttemptReducer.Apply(waiting, Exit(5, 0));
        Assert.Equal((AttemptStatus.Running, (DateTimeOffset?)null, "thread-1"), (between.Status, between.EndedAt, between.SessionId));
        Assert.Equal([new TurnRecord(1, null, TurnOutcome.Succeeded, "Which fruit?")], between.Turns);

        var second = new AttemptEvent[] { NextTurn(6, "banana"), new Launched(T0.AddSeconds(6), 4343, T0.AddSeconds(6)) }.Aggregate(between, AttemptReducer.Apply);
        Assert.Equal((AttemptStatus.Running, 0), (second.Status, second.Queued.Count));
        Assert.Equal(new TurnRecord(2, "banana", TurnOutcome.Running, null), second.Turns[^1]);

        var done = new[] { Said(7, new Message("Wrote banana.")), Said(8, new Succeeded(null)), Exit(9, 0) }.Aggregate(second, AttemptReducer.Apply);
        Assert.Equal((AttemptStatus.Succeeded, "Wrote banana.", T0.AddSeconds(9)), (done.Status, done.Result, done.EndedAt));
        Assert.Equal(
            [new TurnRecord(1, null, TurnOutcome.Succeeded, "Which fruit?"), new TurnRecord(2, "banana", TurnOutcome.Succeeded, "Wrote banana.")],
            done.Turns);
    }

    [Fact]
    public void Two_messages_sent_during_one_turn_both_wait_for_it()
    {
        var record = Replay([.. AskedWhichFruit, Sent(4, "banana"), Sent(5, "and an apple"), Exit(6, 0)]);

        Assert.Equal((AttemptStatus.Running, "banana|and an apple"), (record.Status, Waiting(record)));
    }

    [Fact]
    public void Stop_and_send_records_a_stopped_turn_and_the_attempt_goes_on()
    {
        var record = Replay(LaunchedAt1s, Said(2, new SessionStarted("thread-1")), Said(3, new ToolStarted("command", "dotnet test")), Sent(4, "Stop testing.", stopsTurn: true), Exit(5, 137));

        Assert.Equal((AttemptStatus.Running, false, (string?)null), (record.Status, record.Stopping, record.Detail));
        Assert.Equal([new TurnRecord(1, null, TurnOutcome.Stopped, null)], record.Turns);
    }

    [Fact]
    public void A_turn_that_fails_while_a_message_waits_keeps_its_reason_and_the_attempt_goes_on()
    {
        var record = Replay(LaunchedAt1s, Said(2, new SessionStarted("thread-1")), Sent(3, "Use another model."), Exit(4, 1, "error: model not found\n"));

        Assert.Equal((AttemptStatus.Running, (string?)null), (record.Status, record.Detail));
        Assert.Equal([new TurnRecord(1, null, TurnOutcome.Failed, null, "Codex exited with code 1: error: model not found")], record.Turns);
    }

    [Fact]
    public void A_stopped_turn_that_goes_on_has_no_reason()
    {
        var record = Replay(LaunchedAt1s, Said(2, new SessionStarted("thread-1")), Sent(3, "Stop testing.", stopsTurn: true), Exit(4, 137));

        Assert.Equal([new TurnRecord(1, null, TurnOutcome.Stopped, null)], record.Turns);
    }

    [Fact]
    public void Cancel_during_a_later_turn_cancels_the_whole_attempt()
    {
        var record = Replay([.. SecondTurnRunning, new CancelRequested(T0.AddSeconds(7)), Exit(8, 137)]);

        Assert.Equal((AttemptStatus.Cancelled, (string?)null, "Which fruit?"), (record.Status, record.Detail, record.Turns[0].FinalText));
        Assert.Equal([TurnOutcome.Succeeded, TurnOutcome.Stopped], record.Turns.Select(turn => turn.Outcome));
    }

    [Fact]
    public void Leaving_during_a_later_turn_records_interrupted()
    {
        var record = Replay([.. SecondTurnRunning, new InterruptRequested(T0.AddSeconds(7), Closed), Exit(8, 137)]);

        Assert.Equal((AttemptStatus.Interrupted, Closed), (record.Status, record.Detail));
        Assert.Equal([TurnOutcome.Succeeded, TurnOutcome.Interrupted], record.Turns.Select(turn => turn.Outcome));
    }

    [Fact]
    public void A_cancel_or_leave_between_turns_settles_at_once()
    {
        var between = Replay([.. AskedWhichFruit, Sent(4, "banana"), Exit(5, 0)]);

        var cancelled = AttemptReducer.Apply(between, new CancelRequested(T0.AddSeconds(6)));
        var left = AttemptReducer.Apply(between, new InterruptRequested(T0.AddSeconds(6), Closed));

        Assert.Equal((AttemptStatus.Cancelled, T0.AddSeconds(6), "Which fruit?"), (cancelled.Status, cancelled.EndedAt, cancelled.Result));
        Assert.Equal((AttemptStatus.Interrupted, Closed), (left.Status, left.Detail));
        Assert.Equal([new TurnRecord(1, null, TurnOutcome.Succeeded, "Which fruit?")], cancelled.Turns);
    }

    [Fact]
    public void A_cancel_before_the_exit_wins_over_a_waiting_message()
    {
        var record = Replay([.. AskedWhichFruit, Sent(4, "banana"), new CancelRequested(T0.AddSeconds(5)), Exit(6, 137)]);

        Assert.Equal((AttemptStatus.Cancelled, "banana"), (record.Status, Waiting(record)));
    }

    [Fact]
    public void A_crash_between_turns_reconciles_to_interrupted()
    {
        var record = Replay([.. AskedWhichFruit, Sent(4, "banana"), Exit(5, 0), new Reconciled(T0.AddSeconds(9), null)]);

        Assert.Equal((AttemptStatus.Interrupted, "iDevelop stopped before the next turn started."), (record.Status, record.Detail));
        Assert.Equal([TurnOutcome.Succeeded], record.Turns.Select(turn => turn.Outcome));
    }

    [Fact]
    public void A_crash_before_a_later_turn_launched_reconciles_without_the_earlier_turns_process()
    {
        var record = Replay([.. AskedWhichFruit, Sent(4, "banana"), Exit(5, 0), NextTurn(6, "banana"), new Reconciled(T0.AddSeconds(9), null)]);

        Assert.Equal(
            (AttemptStatus.Interrupted, "iDevelop stopped while starting the client. If the client started, it may still be running."),
            (record.Status, record.Detail));
        Assert.Equal([TurnOutcome.Succeeded, TurnOutcome.Interrupted], record.Turns.Select(turn => turn.Outcome));
    }

    [Fact]
    public void A_later_turn_that_cannot_launch_fails_the_attempt()
    {
        var record = Replay([.. AskedWhichFruit, Sent(4, "banana"), Exit(5, 0), NextTurn(6, "banana"), new LaunchFailed(T0.AddSeconds(6), "codex did not start: access denied")]);

        Assert.Equal((AttemptStatus.Failed, "codex did not start: access denied"), (record.Status, record.Detail));
        Assert.Equal([TurnOutcome.Succeeded, TurnOutcome.Failed], record.Turns.Select(turn => turn.Outcome));
    }

    [Fact]
    public void A_message_to_a_client_that_reported_no_session_stays_unsent_and_the_exit_policy_settles()
    {
        var record = Replay(LaunchedAt1s, Sent(2, "banana"), Said(3, new Succeeded("Done.")), Exit(4, 0));

        Assert.Equal((AttemptStatus.Succeeded, "banana"), (record.Status, Waiting(record)));
    }

    [Fact]
    public void A_continuation_resumes_the_earlier_session_and_its_first_turn_shows_the_message()
    {
        var requested = BuildRequested(First) with { Prompt = "banana", Continues = new Continuation(new AttemptId(Guid.Parse("019aa000-0000-7000-8000-0000000000aa")), "thread-1") };

        var record = AttemptReducer.Replay([requested, LaunchedAt1s])!;

        Assert.Equal("thread-1", record.SessionId);
        Assert.Equal(new Continuation(new AttemptId(Guid.Parse("019aa000-0000-7000-8000-0000000000aa")), "thread-1"), record.Continues);
        Assert.Equal([new TurnRecord(1, "banana", TurnOutcome.Running, null)], record.Turns);
    }

    [Fact]
    public void A_hand_off_to_the_terminal_annotates_a_settled_attempt_and_changes_nothing_else()
    {
        var settled = Replay([.. AskedWhichFruit, Exit(4, 0)]);

        var handed = AttemptReducer.Apply(settled, new HandedToTerminal(T0.AddMinutes(5), "/home/me/fruit", "cd '/home/me/fruit' && codex resume thread-1"));

        Assert.Equal(new TerminalHandoff(T0.AddMinutes(5), "/home/me/fruit", "cd '/home/me/fruit' && codex resume thread-1"), handed.Terminal);
        Assert.Equal(settled, handed with { Terminal = null });
    }

    private static AttemptEvent[] AskedWhichFruit =>
        [LaunchedAt1s, Said(2, new SessionStarted("thread-1")), Said(3, new Message("Which fruit?")), Said(3, new Succeeded(null))];

    private static AttemptEvent[] SecondTurnRunning =>
        [.. AskedWhichFruit, Sent(4, "banana"), Exit(5, 0), NextTurn(6, "banana"), new Launched(T0.AddSeconds(6), 4343, T0.AddSeconds(6))];

    private static AttemptRecord Replay(params AttemptEvent[] events) => AttemptReducer.Replay([BuildRequested(First), .. events])!;

    private static string Waiting(AttemptRecord record) => string.Join("|", record.Queued);
}
