using System.Diagnostics;
using System.Globalization;
using IDevelop.Execution;
using IDevelop.TestSupport;
using static IDevelop.TestSupport.Processes;

namespace IDevelop.Core.Tests;

/// <summary>The native Unix launcher behind <see cref="ChildProcess"/>. The seams are static, so the class runs alone.</summary>
[Collection(EnvironmentCollection.Name)]
public sealed class ProcessGroupTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);
    private static readonly DateTimeOffset T0 = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private readonly TempFolder _temp = new();
    private readonly Processes _spawned = new();
    private readonly ManualTimeProvider _clock = new();
    private readonly string _bin;
    private readonly string _folder;
    private readonly string _evidence;

    public ProcessGroupTests()
    {
        _bin = _temp.Create("bin");
        _folder = _temp.Create("folder");
        _evidence = _temp.Create("evidence");
    }

    public void Dispose()
    {
        ProcessGroup.LaunchFailure = false;
        ProcessGroup.SignalFailure = null;
        _spawned.Dispose();
        _temp.Dispose();
    }

    [UnixFact]
    public async Task A_workflow_root_leads_its_own_group_and_gets_the_launch_a_standalone_root_gets()
    {
        var client = Script("client", """
            printf 'cwd=%s\n' "$(pwd -P)"
            for argument in "$@"; do printf 'arg=%s\n' "$argument"; done
            printf 'path=%s\n' "$PATH"
            printf 'group=%s\n' "$(ps -o pgid= -p $$ | tr -d ' ')"
            while IFS= read -r line; do printf 'in=%s\n' "$line"; done
            printf 'err=%s\n' "é" >&2
            exit 7
            """);
        var command = new ResolvedCommand(client, IsBatchShim: false) { SearchPath = "/usr/bin:/bin" };
        string[] arguments = ["one", "two words", "é", ""];

        var standalone = await Run(command, arguments, ProcessLifetime.Standalone);
        var workflow = await Run(command, arguments, ProcessLifetime.Workflow);

        Assert.Equal(new Containment.Group(workflow.Id), workflow.Containment);
        Assert.Equal(new Containment.None("No process group contains this turn's descendants."), standalone.Containment);
        Assert.Equal($"group={workflow.Id}", Assert.Single(workflow.Output, line => line.StartsWith("group=", StringComparison.Ordinal)));
        Assert.NotEqual($"group={standalone.Id}", Assert.Single(standalone.Output, line => line.StartsWith("group=", StringComparison.Ordinal)));
        string[] expected =
        [
            $"cwd={RealPath(_folder)}",
            "arg=one", "arg=two words", "arg=é", "arg=", "path=/usr/bin:/bin", "in=héllo", "in=wörld",
        ];
        Assert.Equal(expected, standalone.Output.Where(line => !line.StartsWith("group=", StringComparison.Ordinal)));
        Assert.Equal(expected, workflow.Output.Where(line => !line.StartsWith("group=", StringComparison.Ordinal)));
        Assert.Equal(["err=é"], standalone.Errors);
        Assert.Equal(["err=é"], workflow.Errors);
        Assert.Equal((7, 7), (standalone.Exit, workflow.Exit));
    }

    [UnixFact]
    public async Task A_root_ended_by_a_signal_reports_128_plus_the_signal_as_a_standalone_root_does()
    {
        var command = new ResolvedCommand(Script("client", "kill -9 $$\n"), IsBatchShim: false);

        var standalone = await Run(command, [], ProcessLifetime.Standalone);
        var workflow = await Run(command, [], ProcessLifetime.Workflow);

        Assert.Equal((137, 137), (standalone.Exit, workflow.Exit));
        Assert.IsType<Containment.Group>(workflow.Containment);
    }

    [UnixFact]
    public void A_failed_workflow_launch_reads_as_a_failed_standalone_launch()
    {
        var missingFolder = Path.Combine(_folder, "missing");
        var notExecutable = Path.Combine(_bin, "plain");
        File.WriteAllText(notExecutable, "#!/bin/sh\n");
        var client = new ResolvedCommand(Script("client", "exit 0\n"), IsBatchShim: false);
        foreach (var (command, folder) in new[] { (client, missingFolder), (new ResolvedCommand(notExecutable, false), _folder) })
        {
            var standalone = Assert.Throws<LaunchException>(() => ChildProcess.Start(command, [], folder, ProcessLifetime.Standalone));
            var workflow = Assert.Throws<LaunchException>(() => ChildProcess.Start(command, [], folder, ProcessLifetime.Workflow));
            Assert.StartsWith($"{command.Path} did not start: ", workflow.Message);
            Assert.Equal(standalone.Message, workflow.Message);
        }
    }

    [LinuxFact]
    public async Task A_workflow_root_keeps_the_signal_mask_and_ignored_signals_a_standalone_root_gets()
    {
        var command = new ResolvedCommand(Script("client", "grep -E '^Sig(Blk|Ign):' /proc/self/status\n"), IsBatchShim: false);

        var standalone = await Run(command, [], ProcessLifetime.Standalone);
        var workflow = await Run(command, [], ProcessLifetime.Workflow);

        Assert.Equal(2, standalone.Output.Length);
        Assert.Equal(standalone.Output, workflow.Output);
    }

    [UnixFact]
    public async Task Workflow_cleanup_stops_a_descendant_whose_parent_exited()
    {
        var orphan = Path.Combine(_evidence, "orphan.pid");
        using var child = Start(OrphanMaker(orphan));
        var orphanId = await _spawned.PidAsync(orphan).WaitAsync(Patience);
        Assert.Equal(0, await child.WaitForExitAsync().WaitAsync(Patience));
        using (var parent = Process.GetProcessById(orphanId))
        {
            Assert.False(parent.HasExited);
        }

        var cleanup = await child.CleanUpAsync(TimeSpan.FromSeconds(2), _clock, CancellationToken.None).WaitAsync(Patience);

        Assert.Equal((CleanupResult.Completed, null), (cleanup.Result, cleanup.Detail));
        Assert.Equal([new CleanupStep(T0, "terminateGroup", null)], cleanup.Steps.ToArray());
        AssertGone(orphanId);
    }

    [UnixFact]
    public async Task Without_the_launcher_a_workflow_root_has_no_group_and_cleanup_misses_an_orphan()
    {
        var orphan = Path.Combine(_evidence, "orphan.pid");
        ProcessGroup.LaunchFailure = true;
        ChildProcess child;
        try
        {
            child = Start(OrphanMaker(orphan));
        }
        finally
        {
            ProcessGroup.LaunchFailure = false;
        }

        using (child)
        {
            var orphanId = await _spawned.PidAsync(orphan).WaitAsync(Patience);
            Assert.Equal(0, await child.WaitForExitAsync().WaitAsync(Patience));

            var cleanup = await child.CleanUpAsync(TimeSpan.FromSeconds(2), _clock, CancellationToken.None).WaitAsync(Patience);

            const string reason = "No process group contains this turn's descendants. The process group launcher is switched off.";
            Assert.Equal(new Containment.None(reason), child.Containment);
            Assert.Equal((CleanupResult.Incomplete, reason), (cleanup.Result, cleanup.Detail));
            Assert.Equal(["killTree"], cleanup.Steps.Select(step => step.Action));
            using var survivor = Process.GetProcessById(orphanId);
            Assert.False(survivor.HasExited);
        }
    }

    [UnixFact]
    public async Task Stopping_a_workflow_tree_stops_a_descendant_whose_parent_exited()
    {
        var orphan = Path.Combine(_evidence, "orphan.pid");
        using var child = Start(Script("client", $"(sleep 300 >/dev/null 2>&1 </dev/null & echo $! > {Quote(orphan)})\nexec sleep 300\n"));
        var orphanId = await _spawned.PidAsync(orphan).WaitAsync(Patience);

        child.StopTree();

        Assert.Equal(137, await child.WaitForExitAsync().WaitAsync(Patience));
        AssertGone(orphanId);
    }

    [UnixFact]
    public async Task Stopping_a_workflow_tree_also_stops_a_child_that_left_the_group_while_its_parent_lives()
    {
        var pidFile = Path.Combine(_evidence, "escaped.pid");
        using var fakes = new FakeClients(_temp.Create("fakes"));
        fakes.Install("client", FakeRule.On().SpawnEscapedWriter(pidFile, Path.Combine(_evidence, "gate"), "late.txt", "late\n").Hang());
        using var child = ChildProcess.Start(fakes.Resolver.Resolve("client")!, [], _folder, ProcessLifetime.Workflow);
        _spawned.Add(child.Identity.Id);
        var escaped = await _spawned.PidAsync(pidFile).WaitAsync(Patience);
        Assert.NotEqual(child.Identity.Id, GroupOf(escaped));

        child.StopTree();

        AssertGone(escaped);
        Assert.Equal(137, await child.WaitForExitAsync().WaitAsync(Patience));
    }

    [UnixFact]
    public async Task Workflow_cleanup_waits_the_grace_on_its_clock_before_it_kills_a_descendant_that_ignores_SIGTERM()
    {
        var pidFile = Path.Combine(_evidence, "stubborn.pid");
        var terminated = Path.Combine(_evidence, "terminated");
        using var child = Start(Script("client",
            $"sh -c 'trap \"echo term >> {Quote(terminated)}\" TERM; echo $$ > {Quote(pidFile)}; while :; do sleep 0.1; done' >/dev/null 2>&1 </dev/null &\nexit 0\n"));
        var stubborn = await _spawned.PidAsync(pidFile).WaitAsync(Patience);
        Assert.Equal(0, await child.WaitForExitAsync().WaitAsync(Patience));

        // A grace longer than the wait below shows that only the clock ends it.
        var cleanup = child.CleanUpAsync(TimeSpan.FromMinutes(2), _clock, CancellationToken.None);
        await WaitUntilAsync(() => File.Exists(terminated));
        _clock.Advance(TimeSpan.FromSeconds(119));
        await Task.Delay(200);
        Assert.False(cleanup.IsCompleted);
        using (var survivor = Process.GetProcessById(stubborn))
        {
            Assert.False(survivor.HasExited);
        }

        _clock.Advance(TimeSpan.FromSeconds(1));
        var result = await cleanup.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal((CleanupResult.Completed, null), (result.Result, result.Detail));
        Assert.Equal([new CleanupStep(T0, "terminateGroup", null), new CleanupStep(T0.AddMinutes(2), "killGroup", null)], result.Steps.ToArray());
        AssertGone(stubborn);
    }

    [UnixFact]
    public async Task Workflow_cleanup_reports_a_refused_signal_as_incomplete_and_a_later_cleanup_completes()
    {
        var orphan = Path.Combine(_evidence, "orphan.pid");
        using var child = Start(OrphanMaker(orphan));
        var orphanId = await _spawned.PidAsync(orphan).WaitAsync(Patience);
        Assert.Equal(0, await child.WaitForExitAsync().WaitAsync(Patience));
        ProcessGroup.SignalFailure = 1;
        Cleanup refused;
        try
        {
            var cleanup = child.CleanUpAsync(TimeSpan.FromSeconds(2), _clock, CancellationToken.None);
            while (!cleanup.IsCompleted)
            {
                _clock.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(50);
            }

            refused = await cleanup;
        }
        finally
        {
            ProcessGroup.SignalFailure = null;
        }

        Assert.Equal((CleanupResult.Incomplete, $"Process group {child.Identity.Id} still had processes after cleanup."), (refused.Result, refused.Detail));
        Assert.Equal(["terminateGroup", "killGroup"], refused.Steps.Select(step => step.Action));
        Assert.All(refused.Steps, step => Assert.Equal("Operation not permitted", step.Failure));
        using (var survivor = Process.GetProcessById(orphanId))
        {
            Assert.False(survivor.HasExited);
        }

        var control = await child.CleanUpAsync(TimeSpan.FromSeconds(2), _clock, CancellationToken.None).WaitAsync(Patience);
        Assert.Equal(CleanupResult.Completed, control.Result);
        Assert.Equal(["terminateGroup"], control.Steps.Select(step => step.Action));
        Assert.Null(Assert.Single(control.Steps).Failure);
        AssertGone(orphanId);
    }

    [UnixTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_exited_root_keeps_its_group_ID_until_cleanup_or_dispose_reaps_it(bool cleanUp)
    {
        var orphan = Path.Combine(_evidence, "orphan.pid");
        var child = Start(OrphanMaker(orphan));
        var orphanId = await _spawned.PidAsync(orphan).WaitAsync(Patience);
        Assert.Equal(0, await child.WaitForExitAsync().WaitAsync(Patience));
        await Task.Delay(100);

        using (var zombie = Process.GetProcessById(child.Identity.Id))
        {
            if (OperatingSystem.IsLinux())
            {
                var stat = File.ReadAllText($"/proc/{child.Identity.Id}/stat");
                Assert.Equal('Z', stat[stat.LastIndexOf(')') + 2]);
            }
        }

        if (cleanUp)
        {
            Assert.Equal(CleanupResult.Completed, (await child.CleanUpAsync(TimeSpan.FromSeconds(2), _clock, CancellationToken.None).WaitAsync(Patience)).Result);
            AssertGone(child.Identity.Id);
            child.Dispose();
            AssertGone(orphanId);
        }
        else
        {
            child.Dispose();
            AssertGone(child.Identity.Id);
            using var survivor = Process.GetProcessById(orphanId);
            Assert.False(survivor.HasExited);
            var released = await child.CleanUpAsync(TimeSpan.FromSeconds(2), _clock, CancellationToken.None).WaitAsync(Patience);
            Assert.Equal((CleanupResult.Incomplete, "The turn's process was already released."), (released.Result, released.Detail));
            Assert.Empty(released.Steps);
            child.StopTree();
            Assert.False(survivor.WaitForExit(TimeSpan.FromMilliseconds(200)));
        }
    }

    private string OrphanMaker(string pidFile) =>
        Script("client", $"(sleep 300 >/dev/null 2>&1 </dev/null & echo $! > {Quote(pidFile)})\nexit 0\n");

    private ChildProcess Start(string script)
    {
        var child = ChildProcess.Start(new ResolvedCommand(script, IsBatchShim: false), [], _folder, ProcessLifetime.Workflow);
        _spawned.Add(child.Identity.Id);
        return child;
    }

    private async Task<Launch> Run(ResolvedCommand command, string[] arguments, ProcessLifetime lifetime)
    {
        using var child = ChildProcess.Start(command, arguments, _folder, lifetime);
        _spawned.Add(child.Identity.Id);
        List<string> output = [];
        List<string> errors = [];
        child.ReadStdout(output.Add);
        child.ReadStderr(errors.Add);
        // A root that ends before it reads its input refuses the write, and its exit tells the story.
        await child.WriteInputAsync("héllo\nwörld\n", close: true, Patience, CancellationToken.None);
        var exit = await child.WaitForExitAsync().WaitAsync(Patience);
        await child.WaitForOutputAsync(Patience);
        if (lifetime == ProcessLifetime.Workflow)
        {
            await child.CleanUpAsync(TimeSpan.FromSeconds(2), _clock, CancellationToken.None).WaitAsync(Patience);
        }

        return new Launch(child.Identity.Id, child.Containment, [.. output], [.. errors], exit);
    }

    private string Script(string name, string body)
    {
        var path = Path.Combine(_bin, name);
        Executable.Write(path, "#!/bin/sh\n" + body);
        return path;
    }

    private static string RealPath(string folder)
    {
        using var process = Process.Start(new ProcessStartInfo("/bin/sh", ["-c", "cd \"$1\" && pwd -P", "sh", folder]) { RedirectStandardOutput = true })!;
        var path = process.StandardOutput.ReadToEnd().TrimEnd('\n');
        process.WaitForExit();
        return path;
    }

    private static int GroupOf(int id)
    {
        using var process = Process.Start(new ProcessStartInfo("/bin/sh", ["-c", "ps -o pgid= -p \"$1\" | tr -d ' '", "sh", id.ToString(CultureInfo.InvariantCulture)])
            { RedirectStandardOutput = true })!;
        var group = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return int.Parse(group, CultureInfo.InvariantCulture);
    }

    private static string Quote(string text) => "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static async Task WaitUntilAsync(Func<bool> done)
    {
        var deadline = Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(deadline.Elapsed < Patience, "The condition did not become true in time.");
            await Task.Delay(20);
        }
    }

    private sealed record Launch(int Id, Containment Containment, string[] Output, string[] Errors, int Exit);
}
