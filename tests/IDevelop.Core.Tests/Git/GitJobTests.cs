using IDevelop.Execution;
using IDevelop.TestSupport;
using static IDevelop.TestSupport.FakeRule;
using static IDevelop.TestSupport.Processes;

namespace IDevelop.Core.Tests.Git;

/// <summary>Windows finds a bare git through the app's own PATH, which these tests change, so the class runs alone.</summary>
[Collection(EnvironmentCollection.Name)]
public sealed class GitJobTests
{
    [WindowsFact]
    public async Task A_timed_out_call_stops_a_hook_child_whose_parent_exited()
    {
        using var temp = new TempFolder();
        using var spawned = new Processes();
        using var fakes = new FakeClients(temp.Create("bin"), FakeClientInstallMode.Direct);
        var sleeper = Path.Combine(temp.Create("evidence"), "sleeper.pid");
        fakes.Install("git", On("status").SpawnThroughCmd(sleeper).Hang());
        var path = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", fakes.Folder + Path.PathSeparator + path);
        GitResult result;
        try
        {
            var limits = new GitLimits(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            result = GitRepository.Run(["status"], temp.Create("repository"), GitOperation.Metadata, limits);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
        }

        var sleeperId = await spawned.PidAsync(sleeper);
        Assert.Equal((-1, "Git timed out."), (result.ExitCode, result.Stderr));
        AssertGone(sleeperId);
    }
}
