using IDevelop.Execution;
using IDevelop.Workflows;
using static IDevelop.Execution.AgentEvent;
using static IDevelop.Execution.AttemptEvent;

namespace IDevelop.Core.Tests;

public class AttemptReducerTests
{
    internal static readonly DateTimeOffset T0 = new(2026, 10, 4, 5, 0, 0, TimeSpan.Zero);
    internal static readonly TaskId Build = new(Guid.Parse("019a9d2e-5b77-7e12-a4f0-7c3d9e2b5f22"));
    internal static readonly AttemptId First = new(Guid.Parse("019aa000-0000-7000-8000-000000000001"));
    internal static readonly ExecutionSettings CodexHigh = new(ClientId.Codex) { Model = "gpt-6-sol", Reasoning = "high" };

    private const string Closed = "The project was closed while this task ran.";

    private static readonly Dictionary<string, (AttemptEvent[] Events, AttemptStatus Status, string? Detail)> Endings = new()
    {
        ["a success verdict and exit code 0"] =
            ([Launched, Said(2, new Succeeded("Done.")), Exit(3, 0)], AttemptStatus.Succeeded, null),
        ["a failure verdict and exit code 0, as Pi reports a failed turn"] =
            ([Launched, Said(2, new Failed("OAuth refresh failed for openai-codex.")), Exit(3, 0)], AttemptStatus.Failed, "OAuth refresh failed for openai-codex."),
        ["a failure verdict and exit code 1, as Claude Code reports a bad model"] =
            ([Launched, Said(2, new Failed("There's an issue with the selected model.")), Exit(3, 1)], AttemptStatus.Failed, "There's an issue with the selected model."),
        ["a success verdict and a nonzero exit code"] =
            ([Launched, Said(2, new Succeeded("Done.")), Exit(3, 3)], AttemptStatus.Failed, "Codex reported success but exited with code 3."),
        ["no verdict and exit code 0"] =
            ([Launched, Exit(3, 0)], AttemptStatus.Failed, "Codex ended without a result."),
        ["no verdict and a nonzero exit code with stderr"] =
            ([Launched, Exit(3, 2, "warning: slow\nerror: unexpected status 401\n")], AttemptStatus.Failed, "Codex exited with code 2: error: unexpected status 401"),
        ["no verdict and a nonzero exit code without stderr"] =
            ([Launched, Exit(3, 1)], AttemptStatus.Failed, "Codex exited with code 1."),
        ["a cancel after the client's success"] =
            ([Launched, Said(2, new Succeeded("Done.")), new CancelRequested(T0.AddSeconds(3)), Exit(4, -1)], AttemptStatus.Cancelled, null),
        ["a cancel and then a leave"] =
            ([Launched, new CancelRequested(T0.AddSeconds(2)), new InterruptRequested(T0.AddSeconds(3), Closed), Exit(4, -1)], AttemptStatus.Interrupted, Closed),
        ["a launch that failed"] =
            ([new LaunchFailed(T0.AddSeconds(1), "codex did not start: access denied")], AttemptStatus.Failed, "codex did not start: access denied"),
        ["a crash before the launch was recorded"] =
            ([new Reconciled(T0.AddSeconds(9), null)], AttemptStatus.Interrupted,
                "iDevelop stopped while starting the client. If the client started, it may still be running."),
        ["a crash while the client kept running"] =
            ([Launched, new Reconciled(T0.AddSeconds(9), ProcessMatch.Same)], AttemptStatus.Interrupted,
                "iDevelop stopped while this task ran. Its client was still running and was stopped."),
        ["a crash after the client ended"] =
            ([Launched, new Reconciled(T0.AddSeconds(9), ProcessMatch.Gone)], AttemptStatus.Interrupted, "iDevelop stopped while this task ran."),
        ["a crash after another program took the client's process id"] =
            ([Launched, new Reconciled(T0.AddSeconds(9), ProcessMatch.Reused)], AttemptStatus.Interrupted,
                "iDevelop stopped while this task ran. Process 4242 now belongs to another program and was left alone."),
        ["a leave that gave up on a client that ended later"] =
            ([Launched, new InterruptRequested(T0.AddSeconds(2), Closed), new Reconciled(T0.AddSeconds(9), ProcessMatch.Gone)], AttemptStatus.Interrupted,
                "The project was closed while this task ran. Its client did not stop in time, and iDevelop settled it when the project was opened again."),
        ["a leave that gave up on a client that kept running"] =
            ([Launched, new InterruptRequested(T0.AddSeconds(2), Closed), new Reconciled(T0.AddSeconds(9), ProcessMatch.Same)], AttemptStatus.Interrupted,
                "The project was closed while this task ran. Its client did not stop in time, and iDevelop settled it when the project was opened again. Its client was still running and was stopped."),
    };

    internal static Launched Launched => new(T0.AddSeconds(1), 4242, T0.AddSeconds(1));

    public static TheoryData<string> EndingNames => new(Endings.Keys);

    internal static Requested Requested(AttemptId attempt) =>
        new(T0, attempt, Build, "Implement atomic save", CodexHigh, "# Implement atomic save\n", "codex", ["exec", "--json"]);

    [Fact]
    public void A_successful_attempt_records_its_session_reported_settings_activity_and_result()
    {
        var record = Replay(
            Launched,
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
        var record = Replay(Launched, Said(2, new Message("I could not reach the API.")), Exit(3, 1, "error: timeout"));

        Assert.Equal((AttemptStatus.Failed, "I could not reach the API.", "Codex exited with code 1: error: timeout"), (record.Status, record.Result, record.Detail));
    }

    [Fact]
    public void A_stop_request_shows_as_stopping_until_the_exit_settles_it()
    {
        var stopping = Replay(Launched, new CancelRequested(T0.AddSeconds(2)));

        Assert.Equal((AttemptStatus.Running, true), (stopping.Status, stopping.Stopping));
        var cancelled = AttemptReducer.Apply(stopping, Exit(3, -1));
        Assert.Equal((AttemptStatus.Cancelled, false, T0.AddSeconds(3)), (cancelled.Status, cancelled.Stopping, cancelled.EndedAt));
    }

    [Fact]
    public void A_settled_attempt_ignores_later_events_so_replays_and_races_converge()
    {
        var settled = Replay(Launched, Said(2, new Succeeded("Done.")), Exit(3, 0));

        var after = new AttemptEvent[] { Said(4, new Failed("late")), new CancelRequested(T0.AddSeconds(5)), Exit(6, 1), new Reconciled(T0.AddSeconds(7), ProcessMatch.Same) }
            .Aggregate(settled, AttemptReducer.Apply);

        Assert.Same(settled, after);
    }

    [Fact]
    public void Activity_keeps_the_latest_hundred_lines()
    {
        var record = Replay([Launched, .. Enumerable.Range(0, 120).Select(i => Said(2, new Notice($"notice {i}")))]);

        Assert.Equal(100, record.Activity.Count);
        Assert.Equal(("notice 20", "notice 119"), (record.Activity[0].Text, record.Activity[^1].Text));
    }

    [Fact]
    public void A_log_without_its_request_line_folds_to_nothing()
    {
        Assert.Null(AttemptReducer.Replay([Launched, Exit(3, 0)]));
    }

    internal static AttemptEvent Said(int seconds, AgentEvent e) => new Agent(T0.AddSeconds(seconds), e);

    internal static Exited Exit(int seconds, int code, string stderr = "") => new(T0.AddSeconds(seconds), code, stderr);

    private static AttemptRecord Replay(params AttemptEvent[] events) => AttemptReducer.Replay([Requested(First), .. events])!;
}
