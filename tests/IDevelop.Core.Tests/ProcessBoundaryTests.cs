using System.Diagnostics;
using System.Text.Json;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.AttemptEvents;
using static IDevelop.TestSupport.FakeRule;
using static IDevelop.TestSupport.Processes;

namespace IDevelop.Core.Tests;

[Collection(EnvironmentCollection.Name)]
public sealed class ProcessBoundaryTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);
    private readonly TempFolder _temp = new();
    private readonly Processes _spawned = new();
    private readonly FakeClients _fakes;
    private readonly string _project;
    private readonly string _evidence;
    private readonly ManualTimeProvider _clock = new();

    public ProcessBoundaryTests()
    {
        _fakes = new FakeClients(_temp.Create("bin"));
        _project = _temp.Create("project");
        _evidence = _temp.Create("evidence");
    }

    public void Dispose()
    {
        _spawned.Dispose();
        _fakes.Dispose();
        _temp.Dispose();
    }

    [UnixFact]
    public async Task Workflow_cleanup_after_root_exit_reports_unix_containment_as_incomplete()
    {
        var pidFile = Path.Combine(_evidence, "child.pid");
        using var child = Start(On().SpawnSleepingChild(pidFile).Exit(0));
        var sleeper = await Sleeper(pidFile);
        Assert.Equal(0, await child.WaitForExitAsync().WaitAsync(Patience));

        var cleanup = await child.CleanUpAsync(TimeSpan.FromSeconds(2), _clock, CancellationToken.None).WaitAsync(Patience);

        Assert.Equal(ProcessLifetime.Workflow, child.Lifetime);
        Assert.Equal(new Containment.None("No process group contains this turn's descendants."), child.Containment);
        Assert.Equal(CleanupResult.Incomplete, cleanup.Result);
        Assert.Equal("No process group contains this turn's descendants.", cleanup.Detail);
        Assert.Equal(["killTree"], cleanup.Steps.Select(step => step.Action));
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero), Assert.Single(cleanup.Steps).At);
        Assert.Equal(ProcessMatch.Same, ProcessCheck.Check(sleeper));
        Assert.Equal(ProcessMatch.Same, ProcessCheck.StopIfSame(sleeper));
    }

    [WindowsFact]
    public async Task Workflow_cleanup_terminates_the_job()
    {
        var pidFile = Path.Combine(_evidence, "child.pid");
        using var child = Start(On().SpawnSleepingChild(pidFile).Exit(0));
        var sleeper = await Sleeper(pidFile);
        Assert.Equal(0, await child.WaitForExitAsync().WaitAsync(Patience));

        var cleanup = await child.CleanUpAsync(TimeSpan.FromSeconds(2), _clock, CancellationToken.None).WaitAsync(Patience);

        Assert.Equal(new Containment.Job(), child.Containment);
        Assert.Equal(CleanupResult.Completed, cleanup.Result);
        Assert.Null(cleanup.Detail);
        Assert.Equal([new CleanupStep(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero), "terminateJob", null)], cleanup.Steps);
        AssertGone(sleeper.Id);
        Assert.Equal(ProcessMatch.Gone, ProcessCheck.Check(sleeper));
    }

    [WindowsFact]
    public async Task Workflow_cleanup_reports_job_termination_failure()
    {
        var pidFile = Path.Combine(_evidence, "child.pid");
        using var child = Start(On().SpawnSleepingChild(pidFile).Exit(0));
        var sleeper = await Sleeper(pidFile);
        Assert.Equal(0, await child.WaitForExitAsync().WaitAsync(Patience));
        ProcessJob.TerminationFailure = 5;
        Cleanup cleanup;
        try
        {
            cleanup = await child.CleanUpAsync(TimeSpan.FromSeconds(2), _clock, CancellationToken.None).WaitAsync(Patience);
        }
        finally
        {
            ProcessJob.TerminationFailure = null;
        }

        Assert.Equal(new Containment.Job(), child.Containment);
        Assert.Equal(CleanupResult.Incomplete, cleanup.Result);
        Assert.Equal("Win32 error 5", cleanup.Detail);
        Assert.Equal([new CleanupStep(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero), "terminateJob", "Win32 error 5")], cleanup.Steps);
        Assert.Equal(ProcessMatch.Same, ProcessCheck.Check(sleeper));
        var control = await child.CleanUpAsync(TimeSpan.FromSeconds(2), _clock, CancellationToken.None).WaitAsync(Patience);
        Assert.Equal(CleanupResult.Completed, control.Result);
        Assert.Null(Assert.Single(control.Steps).Failure);
        AssertGone(sleeper.Id);
    }

    [WindowsFact]
    public async Task Workflow_cleanup_reports_job_assignment_failure()
    {
        var pidFile = Path.Combine(_evidence, "child.pid");
        ProcessJob.AssignmentFailure = true;
        ChildProcess child;
        try
        {
            child = Start(On().SpawnSleepingChild(pidFile).Exit(0));
        }
        finally
        {
            ProcessJob.AssignmentFailure = false;
        }

        using (child)
        {
            var sleeper = await Sleeper(pidFile);
            Assert.Equal(0, await child.WaitForExitAsync().WaitAsync(Patience));

            var cleanup = await child.CleanUpAsync(TimeSpan.FromSeconds(2), _clock, CancellationToken.None).WaitAsync(Patience);

            Assert.Equal(new Containment.None("The client could not join a job object."), child.Containment);
            Assert.Equal(CleanupResult.Incomplete, cleanup.Result);
            Assert.Equal("The client could not join a job object.", cleanup.Detail);
            Assert.Equal(["killTree"], cleanup.Steps.Select(step => step.Action));
            Assert.Equal(ProcessMatch.Same, ProcessCheck.Check(sleeper));
            Assert.Equal(ProcessMatch.Same, ProcessCheck.StopIfSame(sleeper));
        }
    }

    [Fact]
    public async Task Cleanup_after_dispose_signals_nothing()
    {
        var pidFile = Path.Combine(_evidence, "released.pid");
        using var child = Start(On().SpawnSleepingChild(pidFile).Hang());
        var sleeper = await Sleeper(pidFile);
        child.LeaveDescendantsRunning();
        child.Dispose();

        var cleanup = await child.CleanUpAsync(TimeSpan.FromSeconds(2), _clock, CancellationToken.None).WaitAsync(Patience);

        Assert.Equal(CleanupResult.Incomplete, cleanup.Result);
        Assert.Equal("The turn's process was already released.", cleanup.Detail);
        Assert.Empty(cleanup.Steps);
        Assert.Equal(ProcessMatch.Same, ProcessCheck.Check(child.Identity));
        Assert.Equal(ProcessMatch.Same, ProcessCheck.Check(sleeper));
        Assert.Equal(ProcessMatch.Same, ProcessCheck.StopIfSame(child.Identity));
        AssertGone(sleeper.Id);

        var controlPid = Path.Combine(_evidence, "control.pid");
        using var control = Start(On().SpawnSleepingChild(controlPid).Hang());
        var controlSleeper = await Sleeper(controlPid);
        var beforeDispose = await control.CleanUpAsync(TimeSpan.FromSeconds(2), _clock, CancellationToken.None).WaitAsync(Patience);
        Assert.Contains(Assert.Single(beforeDispose.Steps).Action, new[] { "terminateJob", "killTree" });
        AssertGone(control.Identity.Id);
        AssertGone(controlSleeper.Id);
    }

    [Fact]
    public async Task A_standalone_launch_keeps_its_log_bytes()
    {
        var pidFile = Path.Combine(_evidence, "standalone.pid");
        FakeAgents.Install(_fakes, ClientId.Codex, FakeAgents.Fresh(ClientId.Codex)
            .SpawnSleepingChild(pidFile).Print(FakeAgents.ReplyLines(ClientId.Codex, "DONE")).Exit(0));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync().WaitAsync(Patience));
        var settled = NextSettled(runs);
        var started = Assert.IsType<StartResult.Started>(runs.Start(TaskDefinition()));
        var sleeper = await Sleeper(pidFile);
        var record = await settled.WaitAsync(Patience);
        Assert.Equal((AttemptStatus.Succeeded, "DONE"), (record.Status, record.Result));
        var folder = AttemptLog.FolderOf(Path.Combine(_project, ".idp", "attempts"), TestTasks.Build, started.Attempt.Id);
        var lines = File.ReadAllLines(Path.Combine(folder, "events.jsonl"));
        var launchedLine = Assert.Single(lines, line => line.Contains("\"type\":\"launched\"", StringComparison.Ordinal));
        using var json = JsonDocument.Parse(launchedLine);
        Assert.False(json.RootElement.TryGetProperty("lifetime", out _));
        Assert.False(json.RootElement.TryGetProperty("containment", out _));
        Assert.DoesNotContain(lines, line => line.Contains("\"type\":\"cleanedUp\"", StringComparison.Ordinal));
        var launched = Assert.Single(AttemptLog.Read(folder).OfType<AttemptEvent.Launched>());
        Assert.Equal($"{{\"type\":\"launched\",\"at\":{JsonSerializer.Serialize(launched.At)},\"processId\":{launched.ProcessId},\"processStarted\":{JsonSerializer.Serialize(launched.ProcessStarted)}}}", launchedLine);
        Assert.Equal(ProcessMatch.Same, ProcessCheck.StopIfSame(sleeper));

        using var log = AttemptLog.Create(_temp.Create("workflow-log"), BuildRequested(First));
        log.Append(LaunchedAt1s with { Lifetime = ProcessLifetime.Workflow, Containment = new Containment.None("x") });
        Assert.Equal("""{"type":"launched","at":"2026-10-04T05:00:01+00:00","processId":4242,"processStarted":"2026-10-04T05:00:01+00:00","lifetime":"workflow","containment":{"type":"none","reason":"x"}}""",
            File.ReadAllLines(Path.Combine(log.Folder, "events.jsonl"))[1]);
        var before = AttemptEvidence.Read(log.Folder);
        var cleanedUp = new AttemptEvent.CleanedUp(T0.AddSeconds(2), CleanupResult.Incomplete, "x", [new CleanupStep(T0.AddSeconds(2), "killTree", "root exited")]);
        log.Append(cleanedUp);
        Assert.Equal("""{"type":"cleanedUp","at":"2026-10-04T05:00:02+00:00","result":"incomplete","detail":"x","steps":[{"at":"2026-10-04T05:00:02+00:00","action":"killTree","failure":"root exited"}]}""",
            File.ReadAllLines(Path.Combine(log.Folder, "events.jsonl"))[2]);
        var roundTrip = Assert.Single(AttemptLog.Read(log.Folder).OfType<AttemptEvent.CleanedUp>());
        Assert.Equal((T0.AddSeconds(2), CleanupResult.Incomplete, "x"), (roundTrip.At, roundTrip.Result, roundTrip.Detail));
        Assert.Equal(new[] { new CleanupStep(T0.AddSeconds(2), "killTree", "root exited") }, roundTrip.Steps.ToArray());
        var after = AttemptEvidence.Read(log.Folder);
        Assert.Null(before.Rejection);
        Assert.Null(after.Rejection);
        Assert.Equal(AttemptStatus.Running, after.Record!.Status);
        Assert.Equivalent(before.Record, after.Record);
        Assert.Equal(before.Record!.AppliedEvents, after.Record.AppliedEvents);
        Assert.Equal(before.Record, AttemptReducer.Apply(before.Record, roundTrip));
        Assert.Equal(3, after.Events.Length);
        Assert.IsType<AttemptEvent.CleanedUp>(after.Events[2]);
        Assert.NotEqual(before.Checkpoint, after.Checkpoint);
    }

    [Fact]
    public async Task Launch_markers_count_runs_not_probes()
    {
        var folder = _temp.Create("launches");
        _fakes.LaunchFolder = folder;
        FakeAgents.Install(_fakes, ClientId.Codex, FakeAgents.Fresh(ClientId.Codex).Print(FakeAgents.ReplyLines(ClientId.Codex, "DONE")));
        var clients = await _fakes.DiscoverAsync().WaitAsync(Patience);
        Assert.Equal(0, LaunchMarkers.Runs(folder, ClientId.Codex));
        await using var runs = ProjectRuns.Open(_project, clients);
        var settled = NextSettled(runs);

        Assert.IsType<StartResult.Started>(runs.Start(TaskDefinition()));
        Assert.Equal(AttemptStatus.Succeeded, (await settled.WaitAsync(Patience)).Status);

        Assert.Equal(1, LaunchMarkers.Runs(folder, ClientId.Codex));
        Assert.True(Directory.GetFiles(folder, "*.json").Length > 1);
    }

    [Theory]
    [InlineData(ClientId.Codex)]
    [InlineData(ClientId.ClaudeCode)]
    public async Task Launch_marker_precedes_protocol_input(ClientId client)
    {
        var folder = _temp.Create("launches");
        var ran = Path.Combine(_evidence, "ran.txt");
        using var fakes = new FakeClients(_temp.Create("direct-bin"), FakeClientInstallMode.Direct) { LaunchFolder = folder };
        FakeAgents.Install(fakes, client, FakeAgents.Fresh(client).Write(ran, "ran"));
        var request = new LaunchRequest("m", null, "banana");
        var definition = Clients.Get(client);
        using var child = ChildProcess.Start(fakes.Resolver.Resolve(definition.Command)!, definition.Launch(request).Arguments, _project, ProcessLifetime.Standalone);
        _spawned.Add(child.Identity.Id);

        await WaitUntilAsync(() => Directory.GetFiles(folder, "*.json") is [var file] &&
            JsonSerializer.Deserialize<string[]>(File.ReadAllBytes(file)) is { Length: > 0 });
        Assert.Equal(1, LaunchMarkers.Runs(folder, client));
        var marker = Assert.Single(Directory.GetFiles(folder, "*.json"));
        Assert.StartsWith(child.Identity.Id + "-", Path.GetFileName(marker));
        Assert.Equal(definition.Launch(request).Arguments.ToArray(), JsonSerializer.Deserialize<string[]>(File.ReadAllBytes(marker)));
        Assert.Equal(ProcessMatch.Same, ProcessCheck.Check(child.Identity));
        Assert.False(File.Exists(ran));

        var protocol = definition.Protocol(request);
        await Write(protocol.Start());
        await Write(protocol.Read(client == ClientId.Codex ? """{"id":"init-1","result":{}}"""
            : """{"type":"control_response","response":{"subtype":"success","request_id":"init-1","response":{}}}"""));
        if (client == ClientId.Codex)
        {
            await Write(protocol.Read("""{"id":"thread-1","result":{"thread":{"id":"session-1"}}}"""));
        }

        Assert.Equal(0, await child.WaitForExitAsync().WaitAsync(Patience));
        Assert.Equal("ran", File.ReadAllText(ran));
        Assert.Equal(1, LaunchMarkers.Runs(folder, client));

        async Task Write(ProtocolOutput output)
        {
            foreach (var frame in output.Writes)
            {
                await child.WriteStdinAsync(frame + "\n", close: false).WaitAsync(Patience);
            }
        }
    }

    private ChildProcess Start(FakeRule rule)
    {
        _fakes.Install("client", rule);
        var child = ChildProcess.Start(_fakes.Resolver.Resolve("client")!, [], _project, ProcessLifetime.Workflow);
        _spawned.Add(child.Identity.Id);
        return child;
    }

    private async Task<ProcessIdentity> Sleeper(string file)
    {
        var pid = await _spawned.PidAsync(file).WaitAsync(Patience);
        using var process = Process.GetProcessById(pid);
        return ProcessCheck.Identify(process);
    }

    private static TaskDefinition TaskDefinition() => TestNodes.Implement(TestTasks.Build, "Build", "Say DONE.", execution: CodexHigh);

    private static Task<AttemptRecord> NextSettled(ProjectRuns runs)
    {
        var settled = new TaskCompletionSource<AttemptRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        runs.Changed += (_, _) =>
        {
            if (runs.Latest.GetValueOrDefault(TestTasks.Build) is { Status: not AttemptStatus.Running } record)
            {
                settled.TrySetResult(record);
            }
        };
        return settled.Task;
    }

    private static async Task WaitUntilAsync(Func<bool> done)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            try
            {
                if (done())
                {
                    return;
                }
            }
            catch (Exception error) when (error is IOException or JsonException)
            {
            }

            Assert.True(DateTime.UtcNow < deadline, "The condition did not become true in time.");
            await Task.Delay(50);
        }
    }
}
