using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using IDevelop.Clients;
using IDevelop.Engine;
using IDevelop.Processes;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.TestSupport.FakeRule;
using static IDevelop.Core.Tests.SpawnedProcesses;

namespace IDevelop.Core.Tests;

[Collection(EnvironmentCollection.Name)]
public sealed class ProcessBoundaryTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);
    private readonly TempFolder _temp = new();
    private readonly SpawnedProcesses _spawned = new();
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
    public async Task Workflow_cleanup_after_root_exit_terminates_the_unix_group()
    {
        var pidFile = Path.Combine(_evidence, "child.pid");
        using var child = Start(On().SpawnSleepingChild(pidFile).Exit(0));
        var sleeper = await Sleeper(pidFile);
        Assert.Equal(0, await child.WaitForExitAsync().WaitAsync(Patience));

        var cleanup = await child.CleanUpAsync(TimeSpan.FromSeconds(2), _clock, CancellationToken.None).WaitAsync(Patience);

        Assert.Equal(ProcessLifetime.Workflow, child.Lifetime);
        Assert.Equal(new Containment.Group(child.Identity.Id), child.Containment);
        Assert.Equal(CleanupResult.Completed, cleanup.Result);
        Assert.Null(cleanup.Detail);
        Assert.Equal([new CleanupStep(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero), "terminateGroup", null)], cleanup.Steps.ToArray());
        AssertGone(sleeper.Id);
        Assert.Equal(ProcessMatch.Gone, ProcessCheck.Check(sleeper));
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
        Assert.Equal([new CleanupStep(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero), "terminateJob", null)], cleanup.Steps.ToArray());
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
        Assert.Equal([new CleanupStep(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero), "terminateJob", "Win32 error 5")], cleanup.Steps.ToArray());
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
        var child = Start(On().SpawnSleepingChild(pidFile).Hang());
        var sleeper = await Sleeper(pidFile);
        child.Dispose();

        var cleanup = await child.CleanUpAsync(TimeSpan.FromSeconds(2), _clock, CancellationToken.None).WaitAsync(Patience);

        Assert.Equal(CleanupResult.Incomplete, cleanup.Result);
        Assert.Equal("The turn's process was already released.", cleanup.Detail);
        Assert.Empty(cleanup.Steps);
        ProcessCheck.StopIfSame(child.Identity);
        ProcessCheck.StopIfSame(sleeper);
        AssertGone(sleeper.Id);

        var controlPid = Path.Combine(_evidence, "control.pid");
        using var control = Start(On().SpawnSleepingChild(controlPid).Hang());
        var controlSleeper = await Sleeper(controlPid);
        var beforeDispose = await control.CleanUpAsync(TimeSpan.FromSeconds(2), _clock, CancellationToken.None).WaitAsync(Patience);
        Assert.Contains(Assert.Single(beforeDispose.Steps).Action, new[] { "terminateJob", "terminateGroup" });
        AssertGone(control.Identity.Id);
        AssertGone(controlSleeper.Id);
    }

    [Fact]
    public async Task Launch_markers_count_turns_not_probes()
    {
        var folder = _temp.Create("launches");
        _fakes.LaunchFolder = folder;
        FakeAgents.Install(_fakes, ClientId.Codex, FakeAgents.Fresh(ClientId.Codex).Print(FakeAgents.ReplyLines(ClientId.Codex, "DONE")));
        var clients = await _fakes.DiscoverAsync().WaitAsync(Patience);
        Assert.Equal(0, LaunchMarkers.Runs(folder, ClientId.Codex));

        var outcome = await new CliAgent(clients, _project).Turn(TaskDefinition(), _project, "Say DONE.", null, _ => { }, Channel.CreateUnbounded<QuestionReply>().Reader, CancellationToken.None).WaitAsync(Patience);

        Assert.Equal(new TurnOutcome.Done("DONE"), outcome);

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
        var definition = ClientCatalog.Get(client);
        using var child = ChildProcess.Start(fakes.Resolver.Resolve(definition.Command)!, definition.Launch(request).Arguments, _project, ProcessLifetime.Probe);
        _spawned.Add(child.Identity.Id);

        await WaitUntilAsync(() => Directory.GetFiles(folder, "*.json") is [var file] &&
            JsonSerializer.Deserialize<string[]>(File.ReadAllBytes(file)) is { Length: > 0 });
        Assert.Equal(1, LaunchMarkers.Runs(folder, client));
        var marker = Assert.Single(Directory.GetFiles(folder, "*.json"));
        Assert.StartsWith(child.Identity.Id + "-", Path.GetFileName(marker));
        Assert.Equal(definition.Launch(request).Arguments.ToArray(), JsonSerializer.Deserialize<string[]>(await File.ReadAllBytesAsync(marker)));
        Assert.Equal(ProcessMatch.Same, ProcessCheck.Check(child.Identity));
        Assert.False(File.Exists(ran));

        var protocol = definition.Protocol(request);
        await Write(child, protocol.Start());
        await Write(child, protocol.Read(client == ClientId.Codex ? """{"id":"init-1","result":{}}"""
            : """{"type":"control_response","response":{"subtype":"success","request_id":"init-1","response":{}}}"""));
        if (client == ClientId.Codex)
        {
            await Write(child, protocol.Read("""{"id":"thread-1","result":{"thread":{"id":"session-1"}}}"""));
        }

        Assert.Equal(0, await child.WaitForExitAsync().WaitAsync(Patience));
        Assert.Equal("ran", await File.ReadAllTextAsync(ran));
        Assert.Equal(1, LaunchMarkers.Runs(folder, client));

        static async Task Write(ChildProcess target, ProtocolOutput output)
        {
            foreach (var frame in output.Writes)
            {
                await target.WriteStdinAsync(frame + "\n", close: false).WaitAsync(Patience);
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

    private static TaskDefinition TaskDefinition() => TestNodes.Implement(TestTasks.Build, "Build", "Say DONE.",
        execution: new ExecutionSettings(ClientId.Codex) { Model = "gpt-6-sol", Reasoning = "high" });

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
