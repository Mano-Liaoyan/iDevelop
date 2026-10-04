using IDevelop.Execution;
using static IDevelop.Core.Tests.AttemptReducerTests;
using static IDevelop.Execution.AgentEvent;

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
        using (var log = AttemptLog.Create(attempts, Requested(First)))
        {
            folder = log.Folder;
            log.Append(Launched);
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
    public void A_torn_last_line_and_an_event_from_a_newer_version_are_skipped()
    {
        var attempts = _temp.Create("attempts");
        string folder;
        using (var log = AttemptLog.Create(attempts, Requested(First)))
        {
            folder = log.Folder;
            log.Append(Launched);
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
        var log = AttemptLog.Create(_temp.Create("attempts"), Requested(First));
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
        using (var log = AttemptLog.Create(attempts, Requested(First)))
        {
            log.Append(Launched);
            log.Append(Said(2, new Succeeded("Done.")));
            log.Append(Exit(3, 0));
        }

        var unreadable = Directory.CreateDirectory(AttemptLog.FolderOf(attempts, Build, Second)).FullName;
        Directory.CreateDirectory(Path.Combine(attempts, "notes"));
        File.WriteAllText(Path.Combine(attempts, "run.lock"), "");

        var (latest, warnings) = AttemptLog.ReadLatest(attempts);

        var record = Assert.Single(latest.Values);
        Assert.Equal((First, AttemptStatus.Succeeded), (record.Id, record.Status));
        Assert.StartsWith($"iDevelop could not read {unreadable}, so it skipped that attempt.", Assert.Single(warnings));
    }

    [Fact]
    public void A_project_without_attempts_reads_as_empty_and_writes_nothing()
    {
        var attempts = Path.Combine(_temp.Create("project"), ".idp", "attempts");

        var (latest, warnings) = AttemptLog.ReadLatest(attempts);

        Assert.Empty(latest);
        Assert.Empty(warnings);
        Assert.False(Directory.Exists(attempts));
    }
}
