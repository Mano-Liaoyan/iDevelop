using IDevelop.Execution;
using IDevelop.TestSupport;
using static IDevelop.Core.Tests.AttemptEvents;
using static IDevelop.Execution.AgentEvent;
using static IDevelop.TestSupport.TestTasks;

namespace IDevelop.Core.Tests;

public sealed class AttemptLogTests : IDisposable
{
    private static readonly AttemptId Second = new(Guid.Parse("019aa000-0000-7000-8000-000000000002"));

    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void The_log_writes_one_json_line_per_event_and_reads_them_back()
    {
        var attempts = _temp.Create("attempts");

        string folder;
        using (var log = AttemptLog.Create(attempts, BuildRequested(First)))
        {
            folder = log.Folder;
            log.Append(LaunchedAt1s);
            log.Append(Said(2, new Message("DONE 审查")));
            log.Append(Said(3, new Succeeded(null)));
            log.Append(Exit(4, 0));
            log.AppendOutput("""{"type":"turn.completed"}""");
        }

        Assert.Equal(Path.Combine(attempts, Build.ToString(), First.ToString()), folder);
        Assert.Equal(
            """
            {"type":"requested","at":"2026-10-04T05:00:00+00:00","attempt":"019aa000-0000-7000-8000-000000000001","task":"019a9d2e-5b77-7e12-a4f0-7c3d9e2b5f22","taskTitle":"Implement atomic save","settings":{"client":"codex","model":"gpt-6-sol","reasoning":"high"},"prompt":"# Implement atomic save\n","command":"codex","arguments":["exec","--json"]}
            {"type":"launched","at":"2026-10-04T05:00:01+00:00","processId":4242,"processStarted":"2026-10-04T05:00:01+00:00"}
            {"type":"agent","at":"2026-10-04T05:00:02+00:00","event":{"type":"message","text":"DONE 审查"}}
            {"type":"agent","at":"2026-10-04T05:00:03+00:00","event":{"type":"succeeded","result":null}}
            {"type":"exited","at":"2026-10-04T05:00:04+00:00","exitCode":0,"stderrTail":""}

            """.Replace("\r\n", "\n"),
            File.ReadAllText(Path.Combine(folder, "events.jsonl")));
        Assert.Equal("{\"type\":\"turn.completed\"}\n", File.ReadAllText(Path.Combine(folder, "output.jsonl")));
        var record = AttemptReducer.Replay(AttemptLog.Read(folder))!;
        Assert.Equal((AttemptStatus.Succeeded, "DONE 审查", CodexHigh), (record.Status, record.Result, record.Requested));
    }

    [Fact]
    public void A_continuation_and_its_turns_are_written_as_new_event_types_and_read_back()
    {
        var attempts = _temp.Create("attempts");
        var requested = BuildRequested(Second) with { Prompt = "banana", Continues = new Continuation(First, "thread-1") };

        string folder;
        using (var log = AttemptLog.Create(attempts, requested))
        {
            folder = log.Folder;
            log.Append(LaunchedAt1s);
            log.Append(Sent(2, "Stop. Use an apple.", stopsTurn: true));
            log.Append(Exit(3, 137));
            log.Append(NextTurn(4, "Stop. Use an apple."));
            log.Append(new AttemptEvent.HandedToTerminal(T0.AddSeconds(9), "/home/me/fruit", "cd '/home/me/fruit' && codex resume thread-1"));
        }

        Assert.Equal(
            """
            {"type":"requested","at":"2026-10-04T05:00:00+00:00","attempt":"019aa000-0000-7000-8000-000000000002","task":"019a9d2e-5b77-7e12-a4f0-7c3d9e2b5f22","taskTitle":"Implement atomic save","settings":{"client":"codex","model":"gpt-6-sol","reasoning":"high"},"prompt":"banana","command":"codex","arguments":["exec","--json"],"continues":{"attempt":"019aa000-0000-7000-8000-000000000001","session":"thread-1"}}
            {"type":"launched","at":"2026-10-04T05:00:01+00:00","processId":4242,"processStarted":"2026-10-04T05:00:01+00:00"}
            {"type":"messageQueued","at":"2026-10-04T05:00:02+00:00","text":"Stop. Use an apple.","stopsTurn":true}
            {"type":"exited","at":"2026-10-04T05:00:03+00:00","exitCode":137,"stderrTail":""}
            {"type":"turnRequested","at":"2026-10-04T05:00:04+00:00","prompt":"Stop. Use an apple.","command":"codex","arguments":["exec","resume","--json","thread-1","-"]}
            {"type":"handedToTerminal","at":"2026-10-04T05:00:09+00:00","folder":"/home/me/fruit","command":"cd '/home/me/fruit' && codex resume thread-1"}

            """.Replace("\r\n", "\n"),
            File.ReadAllText(Path.Combine(folder, "events.jsonl")));
        var record = AttemptReducer.Replay(AttemptLog.Read(folder))!;
        Assert.Equal(((AttemptId?)First, "thread-1"), (record.Continues, record.SessionId));
        Assert.Equal([new TurnRecord(1, "banana", TurnOutcome.Stopped, null), new TurnRecord(2, "Stop. Use an apple.", TurnOutcome.Running, null)], record.Turns);
        Assert.Equal(new TerminalHandoff(T0.AddSeconds(9), "/home/me/fruit", "cd '/home/me/fruit' && codex resume thread-1"), record.Terminal);
    }

    [Fact]
    public void A_torn_last_line_and_an_event_from_a_newer_version_are_skipped()
    {
        var attempts = _temp.Create("attempts");
        string folder;
        using (var log = AttemptLog.Create(attempts, BuildRequested(First)))
        {
            folder = log.Folder;
            log.Append(LaunchedAt1s);
        }

        File.AppendAllText(
            Path.Combine(folder, "events.jsonl"),
            "{\"type\":\"paused\",\"at\":\"2026-10-04T05:00:05+00:00\"}\n{\"type\":\"exited\",\"at\":\"2026-10-");

        var events = AttemptLog.Read(folder);

        Assert.Equal(["requested", "launched"], events.Select(e => e.GetType().Name.ToLowerInvariant()).ToArray());
        Assert.Equal(AttemptStatus.Running, AttemptReducer.Replay(events)!.Status);
    }

    [Fact]
    public void Evidence_written_after_the_log_closes_is_dropped()
    {
        var log = AttemptLog.Create(_temp.Create("attempts"), BuildRequested(First));
        log.AppendStderr("first");
        log.Dispose();

        log.AppendStderr("late");
        log.AppendOutput("late");

        Assert.Equal("first\n", File.ReadAllText(Path.Combine(log.Folder, "stderr.log")));
        Assert.False(File.Exists(Path.Combine(log.Folder, "output.jsonl")));
    }

    [Fact]
    public void Each_task_shows_its_newest_readable_attempt_and_an_unreadable_one_becomes_a_warning()
    {
        var attempts = _temp.Create("attempts");
        using (var log = AttemptLog.Create(attempts, BuildRequested(First)))
        {
            log.Append(LaunchedAt1s);
            log.Append(Said(2, new Succeeded("Done.")));
            log.Append(Exit(3, 0));
        }

        var unreadable = Directory.CreateDirectory(AttemptLog.FolderOf(attempts, Build, Second)).FullName;
        Directory.CreateDirectory(Path.Combine(attempts, "notes"));

        var read = AttemptLog.ReadLatest(attempts);
        var latest = read.Latest;
        var warnings = read.Warnings;

        var record = Assert.Single(latest.Values);
        Assert.Equal((First, AttemptStatus.Succeeded), (record.Id, record.Status));
        Assert.StartsWith($"iDevelop could not read {unreadable}, so it skipped that attempt.", Assert.Single(warnings));
    }

    [Fact]
    public void A_log_without_its_request_line_is_skipped_with_a_warning()
    {
        var attempts = _temp.Create("attempts");
        var folder = Directory.CreateDirectory(AttemptLog.FolderOf(attempts, Build, First)).FullName;
        File.WriteAllText(
            Path.Combine(folder, "events.jsonl"),
            """{"type":"launched","at":"2026-10-04T05:00:01+00:00","processId":4242,"processStarted":"2026-10-04T05:00:01+00:00"}""" + "\n");

        var read = AttemptLog.ReadLatest(attempts);
        var latest = read.Latest;
        var warnings = read.Warnings;

        Assert.Empty(latest);
        Assert.Equal($"{folder} holds no attempt record, so iDevelop skipped it.", Assert.Single(warnings));
    }

    [Fact]
    public void A_project_without_attempts_reads_as_empty_and_writes_nothing()
    {
        var attempts = Path.Combine(_temp.Create("project"), ".idp", "attempts");

        var read = AttemptLog.ReadLatest(attempts);
        var latest = read.Latest;
        var warnings = read.Warnings;

        Assert.Empty(latest);
        Assert.Empty(warnings);
        Assert.False(Directory.Exists(attempts));
    }
}
