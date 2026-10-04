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
[Collection(ProcessTests.Name)]
public sealed class ProjectRunsTests : IDisposable
{
    private static readonly TaskId SayHiId = new(Guid.Parse("019a9d2e-5a02-7c41-9d3e-2b8f6a1c0e11"));
    private static readonly TaskId ReviewId = new(Guid.Parse("019a9d2e-5c9a-7f05-b1c8-4e6a0d3f8c33"));
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private static readonly Dictionary<ClientId, ClientRun> Runs = new()
    {
        [ClientId.ClaudeCode] = new(
            new ExecutionSettings(ClientId.ClaudeCode) { Model = "claude-haiku-4-5", Reasoning = "high" },
            ["-p", "--output-format", "stream-json", "--verbose", "--model", "claude-haiku-4-5", "--effort", "high", "--permission-mode", "acceptEdits"],
            "claude-success.jsonl",
            "847c08de-2ab8-4e5f-bcee-7d813def3756",
            "claude-haiku-4-5-20251001",
            null),
        [ClientId.Codex] = new(
            new ExecutionSettings(ClientId.Codex) { Model = "gpt-6-sol", Reasoning = "high" },
            ["exec", "--json", "-m", "gpt-6-sol", "-c", "model_reasoning_effort=high", "--sandbox", "workspace-write", "--skip-git-repo-check", "-"],
            "codex-success.jsonl",
            "01a104d5-d442-71a1-9b08-8938c119e5ae",
            null,
            null),
        [ClientId.Pi] = new(
            new ExecutionSettings(ClientId.Pi) { Model = "deepseek/deepseek-v4-pro", Reasoning = "high" },
            ["-p", "--mode", "json", "--model", "deepseek/deepseek-v4-pro", "--thinking", "high"],
            "pi-success.jsonl",
            "01a104d6-5d29-70a3-b067-4dea17388eb1",
            "deepseek/deepseek-v4-pro",
            "high"),
        [ClientId.Antigravity] = new(
            new ExecutionSettings(ClientId.Antigravity) { Model = "gemini-3.8-flash", Reasoning = "low" },
            ["--input-format", "stream-json", "--output-format", "stream-json", "--model", "gemini-3.8-flash", "--effort", "low", "--mode", "accept-edits", "--print="],
            "agy-success.jsonl",
            "88fcc1a4-0a4f-495c-a2db-b6fc830d0b4a",
            "gemini-3.8-flash",
            null),
    };

    private readonly TempFolder _temp = new();
    private readonly FakeClients _fakes;
    private readonly string _project;
    private readonly string _evidence;
    private readonly List<int> _spawned = [];

    public ProjectRunsTests()
    {
        _fakes = new FakeClients(_temp.Create("bin"));
        _project = _temp.Create("project");
        _evidence = _temp.Create("evidence");
    }

    public static TheoryData<ClientId> AllClients => new(Clients.All);

    // A process that outlives a failed test holds the test host's output open, and dotnet test would wait for it.
    public void Dispose()
    {
        foreach (var pid in _spawned)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.Kill(entireProcessTree: true);
                process.WaitForExit(Patience);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
            }
        }

        _temp.Dispose();
    }

    [Theory]
    [MemberData(nameof(AllClients))]
    public async Task Each_client_runs_a_task_in_the_project_folder_and_reports_its_result(ClientId client)
    {
        var expected = Runs[client];
        FakeAgents.Install(_fakes, client, On(expected.Arguments[0], expected.Arguments[1])
            .RecordArguments(Evidence("arguments.json"))
            .RecordWorkingDirectory(Evidence("folder.txt"))
            .CaptureStdin(Evidence("stdin.txt"))
            .Replay(Fixture.Path(expected.Fixture)));
        var clients = await DiscoverAsync();
        await using var runs = ProjectRuns.Open(_project, clients);
        var settled = NextSettled(runs);

        var started = Assert.IsType<StartResult.Started>(runs.Start(SayHi(expected.Settings)));

        Assert.Equal(AttemptStatus.Running, started.Attempt.Status);
        var record = await settled;
        Assert.Equal((AttemptStatus.Succeeded, "DONE", null), (record.Status, record.Result, record.Detail));
        Assert.Equal((expected.Session, expected.Model, expected.Reasoning), (record.SessionId, record.ReportedModel, record.ReportedReasoning));
        Assert.Equal(expected.Arguments, JsonSerializer.Deserialize<string[]>(File.ReadAllText(Evidence("arguments.json")))!);
        Assert.Equal(Folders.AsCurrentFolder(_project), File.ReadAllText(Evidence("folder.txt")));
        Assert.Equal(
            client == ClientId.Antigravity
                ? """{"event":"user","message":{"role":"user","content":"# Say hi\n\nCreate hello.txt containing hi. Then reply with DONE.\n"}}""" + "\n"
                : "# Say hi\n\nCreate hello.txt containing hi. Then reply with DONE.\n",
            File.ReadAllText(Evidence("stdin.txt")));
        var folder = AttemptLog.FolderOf(Path.Combine(_project, ".idp", "attempts"), SayHiId, record.Id);
        Assert.Equal(Fixture.Text(expected.Fixture), File.ReadAllText(Path.Combine(folder, "output.jsonl")));
        Assert.Equal("*.tmp\nattempts/\n", File.ReadAllText(Path.Combine(_project, ".idp", ".gitignore")));
        Assert.Null(runs.Active);
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
            await using var runs = ProjectRuns.Open(_project, await DiscoverAsync());
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
        var clients = await DiscoverAsync();
        Assert.Equal(
            ["gpt-6.1-sol", "gpt-6-sol", "gpt-5.5"],
            Assert.IsType<ClientStatus.Ready>(clients.Current[ClientId.Codex]).Models.Select(model => model.Id));
        await using var runs = ProjectRuns.Open(_project, clients);
        var settled = NextSettled(runs);

        runs.Start(SayHi(Runs[ClientId.Codex].Settings));

        var record = await settled;
        Assert.Equal((AttemptStatus.Succeeded, "DONE"), (record.Status, record.Result));
    }

    [Theory]
    [InlineData(ClientId.ClaudeCode, "claude-bad-model.jsonl", 1,
        "There's an issue with the selected model (claude-bogus-9). It may not exist or you may not have access to it. Run --model to pick a different model.")]
    [InlineData(ClientId.Codex, "codex-bad-model.jsonl", 1, "The 'gpt-bogus-9' model is not supported when using Codex with a ChatGPT account.")]
    [InlineData(ClientId.Pi, "pi-auth-error.jsonl", 0, "OAuth refresh failed for openai-codex: OpenAI Codex token refresh failed (401): {")]
    [InlineData(ClientId.Antigravity, "agy-bad-model.jsonl", 1, "invalid model selection (--model \"gemini-bogus-9\" --effort \"low\"): --effort is not supported")]
    public async Task A_client_that_reports_a_failure_fails_the_attempt_with_its_own_reason(ClientId client, string fixture, int exitCode, string reason)
    {
        var expected = Runs[client];
        FakeAgents.Install(_fakes, client, On(expected.Arguments[0], expected.Arguments[1]).Replay(Fixture.Path(fixture)).Exit(exitCode));
        await using var runs = ProjectRuns.Open(_project, await DiscoverAsync());
        var settled = NextSettled(runs);

        runs.Start(SayHi(expected.Settings));

        var record = await settled;
        Assert.Equal(AttemptStatus.Failed, record.Status);
        Assert.StartsWith(reason, record.Detail);
    }

    [Fact]
    public async Task Cancelling_stops_the_client_and_every_process_it_started_and_records_cancelled()
    {
        var grandchild = Evidence("grandchild.pid");
        FakeAgents.Install(_fakes, ClientId.Codex, On("exec", "--json")
            .Print("""{"type":"thread.started","thread_id":"01a104d5-d442-71a1-9b08-8938c119e5ae"}""")
            .SpawnSleepingChild(grandchild)
            .Hang());
        await using var runs = ProjectRuns.Open(_project, await DiscoverAsync());
        var settled = NextSettled(runs);
        var started = Assert.IsType<StartResult.Started>(runs.Start(SayHi(Runs[ClientId.Codex].Settings)));
        _spawned.Add(started.Attempt.Process!.Value.Id);
        var grandchildId = await PidAsync(grandchild);

        runs.Cancel(SayHiId);
        runs.Cancel(SayHiId);

        var record = await settled;
        Assert.Equal((AttemptStatus.Cancelled, null, "01a104d5-d442-71a1-9b08-8938c119e5ae"), (record.Status, record.Detail, record.SessionId));
        AssertGone(started.Attempt.Process!.Value.Id);
        AssertGone(grandchildId);
        Assert.Null(runs.Active);
    }

    [WindowsFact]
    public async Task Cancelling_stops_a_process_whose_parent_has_already_exited()
    {
        var sleeper = Evidence("sleeper.pid");
        FakeAgents.Install(_fakes, ClientId.Codex, On("exec", "--json").SpawnThroughCmd(sleeper).Hang());
        await using var runs = ProjectRuns.Open(_project, await DiscoverAsync());
        var settled = NextSettled(runs);
        var started = Assert.IsType<StartResult.Started>(runs.Start(SayHi(Runs[ClientId.Codex].Settings)));
        _spawned.Add(started.Attempt.Process!.Value.Id);
        var sleeperId = await PidAsync(sleeper);

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
        await using var runs = ProjectRuns.Open(_project, await DiscoverAsync());
        var settled = NextSettled(runs);

        runs.Start(SayHi(Runs[ClientId.Codex].Settings));
        var sleeperId = await PidAsync(sleeper);

        var record = await settled;
        Assert.Equal((AttemptStatus.Succeeded, "DONE"), (record.Status, record.Result));
        using var survivor = Process.GetProcessById(sleeperId);
        Assert.False(survivor.WaitForExit(TimeSpan.FromSeconds(1)), "the process the client left running was stopped");
    }

    [GitBashFact]
    public async Task Stopping_a_client_stops_the_commands_Git_Bash_runs_for_it()
    {
        var sleeper = Evidence("sleep.pid");
        var shim = Path.Combine(_fakes.Folder, "bash-client.cmd");
        File.WriteAllText(shim, $"@\"{GitBashFactAttribute.Bash}\" -c \"bash -c 'sleep 300 & cat /proc/$!/winpid > {sleeper.Replace('\\', '/')}; wait'\"\r\n");
        var client = ChildProcess.Start(new ResolvedCommand(shim, IsBatchShim: true), [], _project);
        _spawned.Add(client.Identity.Id);
        var sleepId = await PidAsync(sleeper);

        client.StopTree();

        AssertGone(client.Identity.Id);
        AssertGone(sleepId);
        client.Dispose();
    }

    [WindowsFact]
    public async Task Closing_a_childs_job_as_Windows_does_when_iDevelop_exits_stops_everything_it_started()
    {
        var sleeper = Evidence("sleeper.pid");
        var shim = _fakes.Install("client", On().SpawnThroughCmd(sleeper).Hang());
        var client = ChildProcess.Start(new ResolvedCommand(shim, IsBatchShim: true), [], _project);
        _spawned.Add(client.Identity.Id);
        var sleeperId = await PidAsync(sleeper);

        client.Dispose();

        AssertGone(client.Identity.Id);
        AssertGone(sleeperId);
    }

    [Fact]
    public async Task Leaving_stops_the_client_records_interrupted_and_frees_the_folder()
    {
        var grandchild = Evidence("grandchild.pid");
        FakeAgents.Install(_fakes, ClientId.Codex, On("exec", "--json").SpawnSleepingChild(grandchild).Hang());
        var clients = await DiscoverAsync();
        var runs = ProjectRuns.Open(_project, clients);
        var settled = NextSettled(runs);
        var started = Assert.IsType<StartResult.Started>(runs.Start(SayHi(Runs[ClientId.Codex].Settings)));
        _spawned.Add(started.Attempt.Process!.Value.Id);
        var grandchildId = await PidAsync(grandchild);

        await runs.DisposeAsync();

        var events = Path.Combine(AttemptLog.FolderOf(Path.Combine(_project, ".idp", "attempts"), SayHiId, started.Attempt.Id), "events.jsonl");
        Assert.StartsWith("{\"type\":\"exited\"", File.ReadLines(events).Last());
        Assert.Null(runs.Active);
        var record = await settled;
        Assert.Equal((AttemptStatus.Interrupted, "The project was closed while this task ran."), (record.Status, record.Detail));
        AssertGone(started.Attempt.Process!.Value.Id);
        AssertGone(grandchildId);
        Assert.Throws<ObjectDisposedException>(() => runs.Start(SayHi(Runs[ClientId.Codex].Settings)));
        FakeAgents.Install(_fakes, ClientId.Codex, On("exec", "--json").Replay(Fixture.Path("codex-success.jsonl")));
        await using var next = ProjectRuns.Open(_project, clients);
        Assert.Equal(AttemptStatus.Interrupted, next.Latest[SayHiId].Status);
        var nextSettled = NextSettled(next);
        Assert.IsType<StartResult.Started>(next.Start(SayHi(Runs[ClientId.Codex].Settings)));
        Assert.Equal(AttemptStatus.Succeeded, (await nextSettled).Status);
    }

    [Fact]
    public async Task A_second_window_on_the_folder_cannot_start_while_a_task_runs_and_leaves_that_run_alone()
    {
        FakeAgents.Install(_fakes, ClientId.Codex, On("exec", "--json").Hang());
        var clients = await DiscoverAsync();
        await using var first = ProjectRuns.Open(_project, clients);
        await using var second = ProjectRuns.Open(_project, clients);
        var settled = NextSettled(first);
        var started = Assert.IsType<StartResult.Started>(first.Start(SayHi(Runs[ClientId.Codex].Settings)));
        _spawned.Add(started.Attempt.Process!.Value.Id);

        Assert.Equal(new StartProblem.AlreadyRunning(SayHiId, "Say hi"), first.Check(Review(Runs[ClientId.Codex].Settings)));
        Assert.Equal(
            new StartResult.Refused(new StartProblem.AlreadyRunning(SayHiId, "Say hi")),
            first.Start(Review(Runs[ClientId.Codex].Settings)));
        Assert.Equal(
            new StartResult.Refused(new StartProblem.AlreadyRunning(SayHiId, "Say hi")),
            second.Start(Review(Runs[ClientId.Codex].Settings)));
        await using (var third = ProjectRuns.Open(_project, clients))
        {
            Assert.Equal(AttemptStatus.Running, third.Latest[SayHiId].Status);
        }

        Assert.False(Process.GetProcessById(started.Attempt.Process!.Value.Id).HasExited);
        first.Cancel(SayHiId);
        Assert.Equal(AttemptStatus.Cancelled, (await settled).Status);
        Assert.Null(first.Check(Review(Runs[ClientId.Codex].Settings)));
    }

    [Fact]
    public async Task Another_process_that_holds_the_run_lock_keeps_the_folder_from_starting_until_it_ends()
    {
        var gate = Evidence("release");
        var holder = _fakes.Install("holder", On()
            .LockFile(Path.Combine(Directory.CreateDirectory(Path.Combine(_project, ".idp", "attempts")).FullName, "run.lock"))
            .Print("locked")
            .WaitForFile(gate));
        FakeAgents.Install(_fakes, ClientId.Codex, On("exec", "--json").Replay(Fixture.Path("codex-success.jsonl")));
        await using var runs = ProjectRuns.Open(_project, await DiscoverAsync());
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
        var clients = await DiscoverAsync();
        var runs = ProjectRuns.Open(_project, clients);
        runs.LeaveTimeout = TimeSpan.FromMilliseconds(100);
        var stalled = 0;
        var last = new TaskCompletionSource<AttemptRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        runs.Changed += (_, record) =>
        {
            if (record.Stopping && Interlocked.Exchange(ref stalled, 1) == 0)
            {
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }

            if (runs.Active is null)
            {
                last.TrySetResult(record);
            }
        };
        var started = Assert.IsType<StartResult.Started>(runs.Start(SayHi(Runs[ClientId.Codex].Settings)));
        _spawned.Add(started.Attempt.Process!.Value.Id);

        await runs.DisposeAsync().AsTask().WaitAsync(Patience);

        var record = await last.Task.WaitAsync(Patience);
        Assert.Equal((AttemptStatus.Running, true), (record.Status, record.Stopping));
        var events = Path.Combine(AttemptLog.FolderOf(Path.Combine(_project, ".idp", "attempts"), SayHiId, started.Attempt.Id), "events.jsonl");
        Assert.StartsWith("{\"type\":\"interruptRequested\"", File.ReadLines(events).Last());
        AssertGone(started.Attempt.Process!.Value.Id);
        await using var next = ProjectRuns.Open(_project, clients);
        Assert.Equal((AttemptStatus.Interrupted, "iDevelop stopped while this task ran."), (next.Latest[SayHiId].Status, next.Latest[SayHiId].Detail));
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
        var clients = await DiscoverAsync();
        var runs = ProjectRuns.Open(_project, clients);
        var calls = 0;
        var freed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runs.Changed += (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                return;
            }

            if (runs.Active is null)
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
        var clients = await DiscoverAsync();
        var command = Assert.IsType<ClientStatus.Ready>(clients.Current[ClientId.Codex]).Command;
        File.Delete(command.Path);
        await using var runs = ProjectRuns.Open(_project, clients);

        var started = Assert.IsType<StartResult.Started>(runs.Start(SayHi(Runs[ClientId.Codex].Settings)));

        Assert.Equal(AttemptStatus.Failed, started.Attempt.Status);
        Assert.StartsWith($"{command.Path} did not start: ", started.Attempt.Detail);
        Assert.Null(runs.Active);
        await using var reopened = ProjectRuns.Open(_project, clients);
        Assert.Equal(started.Attempt.Detail, reopened.Latest[SayHiId].Detail);
    }

    [WindowsFact]
    public async Task A_batch_shim_with_an_argument_cmd_could_misread_is_refused_before_anything_is_recorded()
    {
        _fakes.Install("agy", On("models").Print("weird%model\tWeird Model"));
        await using var runs = ProjectRuns.Open(_project, await DiscoverAsync());

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
        new(SayHiId) { Title = "Say hi", Instructions = "Create hello.txt containing hi. Then reply with DONE.", Execution = settings };

    private static TaskDefinition Review(ExecutionSettings settings) =>
        new(ReviewId) { Title = "Review", Instructions = "Review hello.txt.", Execution = settings };

    private static Task<AttemptRecord> NextSettled(ProjectRuns runs)
    {
        var settled = new TaskCompletionSource<AttemptRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        runs.Changed += (_, record) =>
        {
            if (record.Status != AttemptStatus.Running)
            {
                settled.TrySetResult(record);
            }
        };
        return settled.Task.WaitAsync(Patience);
    }

    /// <summary>Waits until the fake agent has written the pid of the process it started.</summary>
    private async Task<int> PidAsync(string file)
    {
        using var timeout = new CancellationTokenSource(Patience);
        while (true)
        {
            try
            {
                if (File.Exists(file) && int.TryParse(File.ReadAllText(file), out var pid))
                {
                    _spawned.Add(pid);
                    return pid;
                }
            }
            catch (IOException)
            {
            }

            await Task.Delay(50, timeout.Token);
        }
    }

    private async Task<ClientDirectory> DiscoverAsync()
    {
        var clients = new ClientDirectory(_fakes.Resolver);
        await clients.RefreshAsync();
        return clients;
    }

    private string Evidence(string name) => Path.Combine(_evidence, name);

    private sealed record ClientRun(ExecutionSettings Settings, string[] Arguments, string Fixture, string Session, string? Model, string? Reasoning);
}
