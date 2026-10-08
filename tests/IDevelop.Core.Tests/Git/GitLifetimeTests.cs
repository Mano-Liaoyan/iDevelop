using System.Text.Json;
using IDevelop.Core.Tests.Runs;
using IDevelop.Execution;
using IDevelop.TestSupport;
using static IDevelop.TestSupport.FakeRule;
using static IDevelop.TestSupport.Processes;

namespace IDevelop.Core.Tests.Git;

/// <summary>
/// What a Git call's containment stops and what it leaves running. Windows finds a bare git through the app's own PATH,
/// which these tests change, and the direct fake changes DOTNET_ROOT, so the class runs alone.
/// </summary>
[Collection(EnvironmentCollection.Name)]
public sealed class GitLifetimeTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    [WindowsFact]
    public async Task A_timed_out_call_stops_a_hook_child_whose_parent_exited()
    {
        using var temp = new TempFolder();
        using var spawned = new Processes();
        using var fakes = new FakeClients(temp.Create("bin"), FakeClientInstallMode.Direct);
        var sleeper = Path.Combine(temp.Create("evidence"), "sleeper.pid");
        // The fake waits for the sleeper's pid, so the limit only has to outlast a cold start.
        fakes.Install("git", On("status").SpawnThroughCmd(sleeper).WaitForFile(sleeper).Hang());
        var path = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", fakes.Folder + Path.PathSeparator + path);
        GitResult result;
        try
        {
            var limits = new GitLimits(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
            result = GitRepository.Run(["status"], temp.Create("repository"), GitOperation.Metadata, limits);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
        }

        Assert.True(File.Exists(sleeper), "The fake git was stopped before its hook child started.");
        var sleeperId = await spawned.PidAsync(sleeper);
        Assert.Equal((-1, "Git timed out."), (result.ExitCode, result.Stderr));
        AssertGone(sleeperId);
    }

    [Fact]
    public async Task A_call_cut_off_by_iDevelop_exiting_leaves_Git_and_its_hooks_to_finish()
    {
        using var temp = new TempFolder();
        using var spawned = new Processes();
        var launches = temp.Create("launches");
        using var fakes = new FakeClients(temp.Create("bin"), FakeClientInstallMode.Direct) { LaunchFolder = launches };
        var evidence = temp.Create("evidence");
        var hook = Path.Combine(evidence, "hook.pid");
        var gate = Path.Combine(evidence, "gate");
        fakes.Install("git", On("status").SpawnSleepingChild(hook).WaitForFile(gate));
        var repository = temp.Create("repository");
        using var racer = new Racer(new Dictionary<string, string>
        {
            ["PATH"] = fakes.Folder + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
        }, "git-exit", repository, hook);

        Assert.Equal("exiting", await racer.Line());
        await racer.Exit();
        Assert.Equal(73, racer.ExitCode);
        var marker = Assert.Single(Directory.GetFiles(launches, "*.json"));
        Assert.Contains("status", JsonSerializer.Deserialize<string[]>(File.ReadAllBytes(marker))!);
        var git = int.Parse(Path.GetFileName(marker).Split('-')[0]);
        spawned.Add(git);
        var child = await spawned.PidAsync(hook).WaitAsync(Patience);
        await Task.Delay(500);

        using (var survivor = System.Diagnostics.Process.GetProcessById(git))
        {
            Assert.False(survivor.HasExited, "Git died with the app that started it.");
        }

        using (var survivor = System.Diagnostics.Process.GetProcessById(child))
        {
            Assert.False(survivor.HasExited, "The hook's child died with the app that started Git.");
        }

        File.WriteAllText(gate, "go");
        AssertGone(git);
    }
}
