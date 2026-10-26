using System.Diagnostics;
using System.Text.Json;
using IDevelop.Git;
using IDevelop.TestSupport;
using static IDevelop.TestSupport.FakeRule;
using static IDevelop.Core.Tests.SpawnedProcesses;

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
        using var spawned = new SpawnedProcesses();
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
        using var spawned = new SpawnedProcesses();
        var launches = temp.Create("launches");
        using var fakes = new FakeClients(temp.Create("bin"), FakeClientInstallMode.Direct) { LaunchFolder = launches };
        var evidence = temp.Create("evidence");
        var hook = Path.Combine(evidence, "hook.pid");
        var gate = Path.Combine(evidence, "gate");
        fakes.Install("git", On("status").SpawnSleepingChild(hook).WaitForFile(gate));
        var repository = temp.Create("repository");
        using var exiting = GitExit(fakes.Folder + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"), repository, hook);

        Assert.Equal("exiting", await exiting.StandardOutput.ReadLineAsync().WaitAsync(Patience));
        await exiting.WaitForExitAsync().WaitAsync(Patience);
        Assert.Equal(73, exiting.ExitCode);
        var marker = Assert.Single(Directory.GetFiles(launches, "*.json"));
        Assert.Contains("status", JsonSerializer.Deserialize<string[]>(await File.ReadAllBytesAsync(marker))!);
        var git = int.Parse(Path.GetFileName(marker).Split('-')[0]);
        spawned.Add(git);
        var child = await spawned.PidAsync(hook).WaitAsync(Patience);
        await Task.Delay(500);

        using (var survivor = Process.GetProcessById(git))
        {
            Assert.False(survivor.HasExited, "Git died with the app that started it.");
        }

        using (var survivor = Process.GetProcessById(child))
        {
            Assert.False(survivor.HasExited, "The hook's child died with the app that started Git.");
        }

        await File.WriteAllTextAsync(gate, "go");
        AssertGone(git);
    }

    /// <summary>Starts the GitExit helper, which runs git status in <paramref name="repository"/> and exits once the hook started.</summary>
    private static Process GitExit(string path, string repository, string hook)
    {
        var dotnet = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..",
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var start = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, Environment = { ["PATH"] = path },
        };
        foreach (var arg in new[] { Path.Combine(AppContext.BaseDirectory, "IDevelop.GitExit.dll"), repository, hook })
        {
            start.ArgumentList.Add(arg);
        }

        return Process.Start(start)!;
    }
}
