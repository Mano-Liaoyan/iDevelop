using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.TestSupport.FakeRule;
using static IDevelop.TestSupport.Processes;

namespace IDevelop.Core.Tests;

/// <summary>Runs go through real child processes: the fake agent behind on-disk shims, resolved like a real client.</summary>
[Collection(ProcessCollection.Name)]
public sealed class ProjectRunsTests : IDisposable
{
    private static readonly TaskId SayHiId = TestTasks.Design;
    private static readonly TaskId ReviewId = TestTasks.Review;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private static readonly Dictionary<ClientId, ClientRun> Runs = new()
    {
        [ClientId.ClaudeCode] = new(
            new ExecutionSettings(ClientId.ClaudeCode) { Model = "claude-haiku-4-5", Reasoning = "high" },
            "claude-success.jsonl",
            "847c08de-2ab8-4e5f-bcee-7d813def3756",
            "claude-haiku-4-5-20251001",
            null),
        [ClientId.Codex] = new(
            new ExecutionSettings(ClientId.Codex) { Model = "gpt-6-sol", Reasoning = "high" },
            "codex-success.jsonl",
            "01a104d5-d442-71a1-9b08-8938c119e5ae",
            null,
            null),
        [ClientId.Pi] = new(
            new ExecutionSettings(ClientId.Pi) { Model = "deepseek/deepseek-v4-pro", Reasoning = "high" },
            "pi-success.jsonl",
            "01a104d6-5d29-70a3-b067-4dea17388eb1",
            "deepseek/deepseek-v4-pro",
            "high"),
        [ClientId.Antigravity] = new(
            new ExecutionSettings(ClientId.Antigravity) { Model = "gemini-3.8-flash", Reasoning = "low" },
            "agy-success.jsonl",
            "88fcc1a4-0a4f-495c-a2db-b6fc830d0b4a",
            "gemini-3.8-flash",
            null),
    };

    private readonly TempFolder _temp = new();
    private readonly FakeClients _fakes;
    private readonly string _project;
    private readonly string _evidence;
    private readonly Processes _spawned = new();

    public ProjectRunsTests()
    {
        _fakes = new FakeClients(_temp.Create("bin"));
        _project = _temp.Create("project");
        _evidence = _temp.Create("evidence");
    }

    public static TheoryData<ClientId> AllClients => new(Clients.All);

    public void Dispose()
    {
        _spawned.Dispose();
        _temp.Dispose();
    }

    [Theory]
    [MemberData(nameof(AllClients))]
    public async Task Each_client_runs_a_task_in_the_project_folder_and_reports_its_result(ClientId client)
    {
        var expected = Runs[client];
        FakeAgents.Install(_fakes, client, On()
            .RecordArguments(Evidence("arguments.json"))
            .RecordWorkingDirectory(Evidence("folder.txt"))
            .CaptureStdin(Evidence("stdin.txt"))
            .Replay(Fixture.Path(expected.Fixture)));
        var clients = await _fakes.DiscoverAsync();
        await using var runs = ProjectRuns.Open(_project, clients);
        var settled = NextSettled(runs);

        var started = Assert.IsType<StartResult.Started>(runs.Start(SayHi(expected.Settings)));

        Assert.Equal(AttemptStatus.Running, started.Attempt.Status);
        var record = await settled;
        Assert.Equal((AttemptStatus.Succeeded, "DONE", null), (record.Status, record.Result, record.Detail));
        Assert.Equal((expected.Session, expected.Model, expected.Reasoning), (record.SessionId, record.ReportedModel, record.ReportedReasoning));
        var folder = AttemptLog.FolderOf(Path.Combine(_project, ".idp", "attempts"), SayHiId, record.Id);
        var requested = Assert.IsType<AttemptEvent.Requested>(AttemptLog.Read(folder)[0]);
        Assert.Equal(requested.Arguments.ToArray(), JsonSerializer.Deserialize<string[]>(File.ReadAllText(Evidence("arguments.json")))!);
        Assert.Equal(Folders.AsCurrentFolder(_project), File.ReadAllText(Evidence("folder.txt")));
        Assert.Equal(
            client == ClientId.Antigravity
                ? """{"event":"user","message":{"role":"user","content":"# Say hi\n\nCreate hello.txt containing hi. Then reply with DONE.\n"}}""" + "\n"
                : "# Say hi\n\nCreate hello.txt containing hi. Then reply with DONE.\n",
            File.ReadAllText(Evidence("stdin.txt")));
        Assert.Equal(Fixture.Text(expected.Fixture), File.ReadAllText(Path.Combine(folder, "output.jsonl")));
        Assert.Equal("*.tmp\nattempts/\n", File.ReadAllText(Path.Combine(_project, ".idp", ".gitignore")));
        Assert.Empty(runs.Active);
        await using var reopened = ProjectRuns.Open(_project, clients);
        Assert.Equal((AttemptStatus.Succeeded, "DONE"), (reopened.Latest[SayHiId].Status, reopened.Latest[SayHiId].Result));
    }

    // An npm shim runs node by its bare name when no node.exe sits beside it, and cmd.exe looks for a bare name in the
    // current folder first. For a run, that folder is the project, which anyone who shares the repository controls.
    [WindowsFact]
    public async Task A_run_never_runs_a_command_planted_in_the_project_folder()
    {
        var planted = Evidence("planted.txt");
        File.WriteAllText(Path.Combine(_project, "codex-helper.cmd"), $"@echo planted> \"{planted}\"\r\n");
        _fakes.Install("codex-helper", FakeAgents.CodexModels, FakeAgents.CodexSignedIn, On("exec", "--json").Replay(Fixture.Path("codex-success.jsonl")));
        File.WriteAllText(Path.Combine(_fakes.Folder, "codex.cmd"), "@codex-helper %*\r\n");
        // The machine that runs the tests may already set the variable that stops the search, and children inherit it.
        const string NoCurrentFolder = "NoDefaultCurrentDirectoryInExePath";
        var inherited = Environment.GetEnvironmentVariable(NoCurrentFolder);
        Environment.SetEnvironmentVariable(NoCurrentFolder, null);
        try
        {
            await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
            var settled = NextSettled(runs);

            runs.Start(SayHi(Runs[ClientId.Codex].Settings));

            var record = await settled;
            Assert.Equal((AttemptStatus.Succeeded, "DONE"), (record.Status, record.Result));
            Assert.False(File.Exists(planted), "the planted command ran");
        }
        finally
        {
            Environment.SetEnvironmentVariable(NoCurrentFolder, inherited);
        }
    }

    // A process keeps a junction in its current folder as given, as on a machine whose temporary folder was moved to
    // another drive behind one.
    [WindowsFact]
    public async Task A_run_in_a_project_opened_through_a_junction_works_in_the_folder_as_opened()
    {
        var junction = Path.Combine(Path.GetDirectoryName(_project)!, "junction");
        using (var mklink = Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec")!, ["/c", "mklink", "/J", junction, _project])
        {
            UseShellExecute = false, RedirectStandardOutput = true,
        })!)
        {
            await mklink.WaitForExitAsync().WaitAsync(Patience);
            Assert.Equal(0, mklink.ExitCode);
        }

        try
        {
            FakeAgents.Install(_fakes, ClientId.Codex, On("exec", "--json")
                .RecordWorkingDirectory(Evidence("folder.txt"))
                .Replay(Fixture.Path("codex-success.jsonl")));
            await using var runs = ProjectRuns.Open(junction, await _fakes.DiscoverAsync());
            var settled = NextSettled(runs);

            runs.Start(SayHi(Runs[ClientId.Codex].Settings));

            Assert.Equal(AttemptStatus.Succeeded, (await settled).Status);
            Assert.Equal(junction, File.ReadAllText(Evidence("folder.txt")));
        }
        finally
        {
            // A recursive delete fails on a junction, so the temporary folder's cleanup would.
            Directory.Delete(junction);
        }
    }

    // An npm install of Codex is a script that starts with "#!/usr/bin/env node". The folder that holds it and node is
    // not on this process's PATH, as for an app started from Finder whose clients only the login shell's PATH finds.
    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public async Task A_client_script_finds_node_in_the_folders_the_client_was_found_in()
    {
        var codex = Path.Combine(_fakes.Folder, "codex");
        File.WriteAllText(codex, "#!/usr/bin/env node\n");
        File.SetUnixFileMode(codex, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _fakes.Install("node",
            On(codex, "debug", "models").Replay(Fixture.Path("codex-debug-models.json")),
            On(codex, "login", "status").Print("Logged in using ChatGPT"),
            On(codex, "exec", "--json").Replay(Fixture.Path("codex-success.jsonl")));
        var clients = await _fakes.DiscoverAsync();
        Assert.Equal(
            ["gpt-6.1-sol", "gpt-6-sol", "gpt-5.5"],
            Assert.IsType<ClientStatus.Ready>(clients.Current[ClientId.Codex]).Models.Select(model => model.Id));
        await using var runs = ProjectRuns.Open(_project, clients);
        var settled = NextSettled(runs);

        runs.Start(SayHi(Runs[ClientId.Codex].Settings));

        var record = await settled;
        Assert.Equal((AttemptStatus.Succeeded, "DONE"), (record.Status, record.Result));
    }

    // Pi exits with code 0 after a failed turn, so only its event stream can fail the attempt.
    [Fact]
    public async Task A_client_that_reports_a_failure_and_exits_0_fails_the_attempt_with_its_own_reason()
    {
        var expected = Runs[ClientId.Pi];
        FakeAgents.Install(_fakes, ClientId.Pi, On().Replay(Fixture.Path("pi-auth-error.jsonl")).Exit(0));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var settled = NextSettled(runs);

        runs.Start(SayHi(expected.Settings));

        var record = await settled;
        Assert.Equal(AttemptStatus.Failed, record.Status);
        Assert.StartsWith("OAuth refresh failed for openai-codex: OpenAI Codex token refresh failed (401): {", record.Detail);
    }

    [Fact]
    public async Task Cancelling_stops_the_client_and_every_process_it_started_and_records_cancelled()
    {
        var grandchild = Evidence("grandchild.pid");
        FakeAgents.Install(_fakes, ClientId.Codex, On("exec", "--json")
            .Print("""{"type":"thread.started","thread_id":"01a104d5-d442-71a1-9b08-8938c119e5ae"}""")
            .SpawnSleepingChild(grandchild)
            .Hang());
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var settled = NextSettled(runs);
        var started = Assert.IsType<StartResult.Started>(runs.Start(SayHi(Runs[ClientId.Codex].Settings)));
        _spawned.Add(started.Attempt.Process!.Value.Id);
        var grandchildId = await _spawned.PidAsync(grandchild);

        runs.Cancel(SayHiId);
        runs.Cancel(SayHiId);

        var record = await settled;
        Assert.Equal((AttemptStatus.Cancelled, null, "01a104d5-d442-71a1-9b08-8938c119e5ae"), (record.Status, record.Detail, record.SessionId));
        AssertGone(started.Attempt.Process!.Value.Id);
        AssertGone(grandchildId);
        Assert.Empty(runs.Active);
    }

    [WindowsFact]
    public async Task Cancelling_stops_a_process_whose_parent_has_already_exited()
    {
        var sleeper = Evidence("sleeper.pid");
        FakeAgents.Install(_fakes, ClientId.Codex, On("exec", "--json").SpawnThroughCmd(sleeper).Hang());
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var settled = NextSettled(runs);
        var started = Assert.IsType<StartResult.Started>(runs.Start(SayHi(Runs[ClientId.Codex].Settings)));
        _spawned.Add(started.Attempt.Process!.Value.Id);
        var sleeperId = await _spawned.PidAsync(sleeper);

        runs.Cancel(SayHiId);

        Assert.Equal(AttemptStatus.Cancelled, (await settled).Status);
        AssertGone(started.Attempt.Process!.Value.Id);
        AssertGone(sleeperId);
    }

    // A dev server the agent started or a browser it opened is the user's now. This one also holds the client's output
    // open, as a server that inherits it does.
    [Fact]
    public async Task A_process_the_client_leaves_running_keeps_running_after_the_attempt_settles()
    {
        var sleeper = Evidence("sleeper.pid");
        FakeAgents.Install(_fakes, ClientId.Codex, On("exec", "--json").SpawnSleepingChild(sleeper).Replay(Fixture.Path("codex-success.jsonl")).Exit(0));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var settled = NextSettled(runs);

        runs.Start(SayHi(Runs[ClientId.Codex].Settings));
        var sleeperId = await _spawned.PidAsync(sleeper);

        var record = await settled;
        Assert.Equal((AttemptStatus.Succeeded, "DONE"), (record.Status, record.Result));
        using var survivor = Process.GetProcessById(sleeperId);
        Assert.False(survivor.WaitForExit(TimeSpan.FromSeconds(1)), "the process the client left running was stopped");
    }

    [Fact]
    public async Task Leaving_stops_the_client_records_interrupted_and_frees_the_folder()
    {
        var grandchild = Evidence("grandchild.pid");
        FakeAgents.Install(_fakes, ClientId.Codex, On("exec", "--json").SpawnSleepingChild(grandchild).Hang());
        var clients = await _fakes.DiscoverAsync();
        var runs = ProjectRuns.Open(_project, clients);
        var settled = NextSettled(runs);
        var started = Assert.IsType<StartResult.Started>(runs.Start(SayHi(Runs[ClientId.Codex].Settings)));
        _spawned.Add(started.Attempt.Process!.Value.Id);
        var grandchildId = await _spawned.PidAsync(grandchild);

        await runs.DisposeAsync();

        Assert.Empty(runs.Active);
        var record = await settled;
        Assert.Equal((AttemptStatus.Interrupted, "The project was closed while this task ran."), (record.Status, record.Detail));
        AssertGone(started.Attempt.Process!.Value.Id);
        AssertGone(grandchildId);
        Assert.Throws<ObjectDisposedException>(() => runs.Start(SayHi(Runs[ClientId.Codex].Settings)));
        FakeAgents.Install(_fakes, ClientId.Codex, On("exec", "--json").Replay(Fixture.Path("codex-success.jsonl")));
        await using var next = ProjectRuns.Open(_project, clients);
        Assert.Equal((AttemptStatus.Interrupted, "The project was closed while this task ran."), (next.Latest[SayHiId].Status, next.Latest[SayHiId].Detail));
        var nextSettled = NextSettled(next);
        Assert.IsType<StartResult.Started>(next.Start(SayHi(Runs[ClientId.Codex].Settings)));
        Assert.Equal(AttemptStatus.Succeeded, (await nextSettled).Status);
    }

    [Fact]
    public async Task Two_tasks_run_at_once_and_a_task_runs_once_at_a_time_across_windows()
    {
        FakeAgents.Install(_fakes, ClientId.Codex, On("exec", "--json").Hang());
        var clients = await _fakes.DiscoverAsync();
        await using var first = ProjectRuns.Open(_project, clients);
        await using var second = ProjectRuns.Open(_project, clients);
        var settled = NextSettled(first);
        var sayHi = Assert.IsType<StartResult.Started>(first.Start(SayHi(Runs[ClientId.Codex].Settings)));
        _spawned.Add(sayHi.Attempt.Process!.Value.Id);
        var review = Assert.IsType<StartResult.Started>(first.Start(Review(Runs[ClientId.Codex].Settings)));
        _spawned.Add(review.Attempt.Process!.Value.Id);

        Assert.Equal([SayHiId, ReviewId], first.Active.Select(record => record.Task));
        Assert.Equal(new StartProblem.AlreadyRunning(SayHiId, "Say hi"), first.Check(SayHi(Runs[ClientId.Codex].Settings)));
        Assert.Equal(
            new StartResult.Refused(new StartProblem.AlreadyRunning(SayHiId, "Say hi")),
            first.Start(SayHi(Runs[ClientId.Codex].Settings)));
        Assert.Equal(
            new StartResult.Refused(new StartProblem.AlreadyRunning(ReviewId, "Review")),
            second.Start(Review(Runs[ClientId.Codex].Settings)));
        await using (var third = ProjectRuns.Open(_project, clients))
        {
            Assert.Equal([AttemptStatus.Running, AttemptStatus.Running], new[] { SayHiId, ReviewId }.Select(id => third.Latest[id].Status));
        }

        Assert.False(Process.GetProcessById(sayHi.Attempt.Process!.Value.Id).HasExited);
        Assert.False(Process.GetProcessById(review.Attempt.Process!.Value.Id).HasExited);
        first.Cancel(SayHiId);
        Assert.Equal(AttemptStatus.Cancelled, (await settled).Status);
        Assert.Equal([ReviewId], first.Active.Select(record => record.Task));
        Assert.Null(first.Check(SayHi(Runs[ClientId.Codex].Settings)));
        first.Cancel(ReviewId);
        await WaitUntilAsync(() => first.Active.IsEmpty);
        Assert.Equal(AttemptStatus.Cancelled, first.Latest[ReviewId].Status);
    }

    [Fact]
    public async Task Another_process_that_holds_a_tasks_run_lock_keeps_that_task_from_starting_until_it_ends()
    {
        var gate = Evidence("release");
        var holder = _fakes.Install("holder", On()
            .LockFile(Path.Combine(Directory.CreateDirectory(Path.Combine(_project, ".idp", "attempts", SayHiId.ToString())).FullName, "run.lock"))
            .Print("locked")
            .WaitForFile(gate));
        FakeAgents.Install(_fakes, ClientId.Codex, On("exec", "--json").Replay(Fixture.Path("codex-success.jsonl")));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        using var other = Process.Start(new ProcessStartInfo(holder)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        _spawned.Add(other.Id);
        Assert.Equal("locked", await other.StandardOutput.ReadLineAsync().WaitAsync(Patience));

        Assert.Equal(new StartResult.Refused(new StartProblem.RunInAnotherWindow()), runs.Start(SayHi(Runs[ClientId.Codex].Settings)));

        File.WriteAllText(gate, "");
        await other.WaitForExitAsync().WaitAsync(Patience);
        var settled = NextSettled(runs);
        Assert.IsType<StartResult.Started>(runs.Start(SayHi(Runs[ClientId.Codex].Settings)));
        Assert.Equal(AttemptStatus.Succeeded, (await settled).Status);
    }

    // A drain that is still busy, as on a slow disk, when leaving gives up stops at its next event, so the stop request
    // stays the last line and the next open settles the attempt.
    [Fact]
    public async Task Leaving_gives_up_on_a_run_that_does_not_end_in_time_and_the_next_open_settles_it()
    {
        FakeAgents.Install(_fakes, ClientId.Codex, On("exec", "--json").Hang());
        var clients = await _fakes.DiscoverAsync();
        var runs = ProjectRuns.Open(_project, clients);
        runs.LeaveTimeout = TimeSpan.FromMilliseconds(100);
        var stalled = 0;
        var last = new TaskCompletionSource<AttemptRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        runs.Changed += (_, _) =>
        {
            var record = runs.Latest[SayHiId];
            if (record.Stopping && Interlocked.Exchange(ref stalled, 1) == 0)
            {
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }

            if (runs.Active.IsEmpty)
            {
                last.TrySetResult(record);
            }
        };
        var started = Assert.IsType<StartResult.Started>(runs.Start(SayHi(Runs[ClientId.Codex].Settings)));
        _spawned.Add(started.Attempt.Process!.Value.Id);

        await runs.DisposeAsync().AsTask().WaitAsync(Patience);

        var record = await last.Task.WaitAsync(Patience);
        Assert.Equal((AttemptStatus.Running, true), (record.Status, record.Stopping));
        AssertGone(started.Attempt.Process!.Value.Id);
        await using var next = ProjectRuns.Open(_project, clients);
        Assert.Equal(
            (AttemptStatus.Interrupted, "The project was closed while this task ran. Its client did not stop in time, and iDevelop settled it when the project was opened again."),
            (next.Latest[SayHiId].Status, next.Latest[SayHiId].Detail));
    }

    [Theory]
    [InlineData("same", "iDevelop stopped while this task ran. Its client was still running and was stopped.")]
    [InlineData("gone", "iDevelop stopped while this task ran.")]
    [InlineData("reused", "iDevelop stopped while this task ran. Process {0} now belongs to another program and was left alone.")]
    [InlineData("never launched", "iDevelop stopped while starting the client. If the client started, it may still be running.")]
    public async Task Opening_settles_an_attempt_that_a_crashed_window_left_running(string survivor, string detail)
    {
        // The stand-in starts outside any job, like a client that outlived iDevelop on Linux or macOS, or one that left
        // its job on Windows. A client still in its job stops with iDevelop.
        var shim = _fakes.Install("sleeper", On().Hang());
        using var client = Process.Start(new ProcessStartInfo(shim)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        _spawned.Add(client.Id);
        var identity = ProcessCheck.Identify(client);
        if (survivor == "gone")
        {
            client.Kill(entireProcessTree: true);
            await client.WaitForExitAsync();
        }

        var attempts = Path.Combine(_project, ".idp", "attempts");
        using (var log = AttemptLog.Create(attempts, new AttemptEvent.Requested(
            DateTimeOffset.UtcNow, AttemptId.New(), SayHiId, "Say hi", Runs[ClientId.Codex].Settings, "# Say hi\n", shim, [])))
        {
            if (survivor != "never launched")
            {
                var started = survivor == "reused" ? identity.StartedAt.AddHours(-1) : identity.StartedAt;
                log.Append(new AttemptEvent.Launched(DateTimeOffset.UtcNow, identity.Id, started));
            }
        }

        await using var runs = ProjectRuns.Open(_project, new ClientDirectory(_fakes.Resolver));

        var record = runs.Latest[SayHiId];
        Assert.Equal((AttemptStatus.Interrupted, string.Format(detail, identity.Id)), (record.Status, record.Detail));
        var events = File.ReadAllLines(Path.Combine(AttemptLog.FolderOf(attempts, SayHiId, record.Id), "events.jsonl"));
        await using (ProjectRuns.Open(_project, new ClientDirectory(_fakes.Resolver)))
        {
            Assert.Equal(events, File.ReadAllLines(Path.Combine(AttemptLog.FolderOf(attempts, SayHiId, record.Id), "events.jsonl")));
        }

        if (survivor == "same")
        {
            AssertGone(identity.Id);
        }
        else if (survivor is "reused" or "never launched")
        {
            Assert.False(Process.GetProcessById(identity.Id).HasExited);
        }
    }

    [Fact]
    public async Task A_Changed_handler_that_throws_stops_the_client_and_frees_the_folder()
    {
        FakeAgents.Install(_fakes, ClientId.Codex, On("exec", "--json").Replay(Fixture.Path("codex-success.jsonl")).Hang());
        var clients = await _fakes.DiscoverAsync();
        var runs = ProjectRuns.Open(_project, clients);
        var calls = 0;
        var freed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runs.Changed += (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                return;
            }

            if (runs.Active.IsEmpty)
            {
                freed.TrySetResult();
            }

            throw new InvalidOperationException("The handler failed.");
        };

        var started = Assert.IsType<StartResult.Started>(runs.Start(SayHi(Runs[ClientId.Codex].Settings)));
        _spawned.Add(started.Attempt.Process!.Value.Id);

        await freed.Task.WaitAsync(Patience);
        AssertGone(started.Attempt.Process!.Value.Id);
        await runs.DisposeAsync();
        await using var next = ProjectRuns.Open(_project, clients);
        Assert.Equal((AttemptStatus.Interrupted, "iDevelop stopped while this task ran."), (next.Latest[SayHiId].Status, next.Latest[SayHiId].Detail));
    }

    [Fact]
    public async Task A_client_that_cannot_launch_is_recorded_as_a_failed_attempt()
    {
        FakeAgents.Install(_fakes, ClientId.Codex);
        var clients = await _fakes.DiscoverAsync();
        var command = Assert.IsType<ClientStatus.Ready>(clients.Current[ClientId.Codex]).Command;
        File.Delete(command.Path);
        await using var runs = ProjectRuns.Open(_project, clients);

        var started = Assert.IsType<StartResult.Started>(runs.Start(SayHi(Runs[ClientId.Codex].Settings)));

        Assert.Equal(AttemptStatus.Failed, started.Attempt.Status);
        Assert.StartsWith($"{command.Path} did not start: ", started.Attempt.Detail);
        Assert.Empty(runs.Active);
        await using var reopened = ProjectRuns.Open(_project, clients);
        Assert.Equal(started.Attempt.Detail, reopened.Latest[SayHiId].Detail);
    }

    [WindowsFact]
    public async Task A_batch_shim_with_an_argument_cmd_could_misread_is_refused_before_anything_is_recorded()
    {
        _fakes.Install("agy", On("models").Print("weird%model\tWeird Model"));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());

        var result = runs.Start(SayHi(new ExecutionSettings(ClientId.Antigravity) { Model = "weird%model" }));

        Assert.Equal(new StartResult.Refused(new StartProblem.UnsafeArgument(ClientId.Antigravity, "weird%model")), result);
        Assert.False(Directory.Exists(Path.Combine(_project, ".idp")));
    }

    [Fact]
    public async Task An_unreadable_attempt_folder_becomes_a_warning_instead_of_a_failed_open()
    {
        var empty = Directory.CreateDirectory(Path.Combine(_project, ".idp", "attempts", SayHiId.ToString(), AttemptId.New().ToString())).FullName;

        await using var runs = ProjectRuns.Open(_project, new ClientDirectory(_fakes.Resolver));

        Assert.Empty(runs.Latest);
        Assert.StartsWith($"iDevelop could not read {empty}, so it skipped that attempt.", Assert.Single(runs.Warnings));
    }

    private static TaskDefinition SayHi(ExecutionSettings settings) =>
        TestNodes.Implement(SayHiId, "Say hi", "Create hello.txt containing hi. Then reply with DONE.", execution: settings);

    private static TaskDefinition Review(ExecutionSettings settings) =>
        TestNodes.Implement(ReviewId, "Review", "Review hello.txt.", execution: settings);

    private static async Task WaitUntilAsync(Func<bool> done)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not become true in time.");
            await Task.Delay(50);
        }
    }

    private static Task<AttemptRecord> NextSettled(ProjectRuns runs)
    {
        var settled = new TaskCompletionSource<AttemptRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        runs.Changed += (_, _) =>
        {
            if (runs.Latest.GetValueOrDefault(SayHiId) is { Status: not AttemptStatus.Running } record)
            {
                settled.TrySetResult(record);
            }
        };
        return settled.Task.WaitAsync(Patience);
    }

    private string Evidence(string name) => Path.Combine(_evidence, name);

    private sealed record ClientRun(ExecutionSettings Settings, string Fixture, string Session, string? Model, string? Reasoning);
}
