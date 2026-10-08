using IDevelop.Execution;
using IDevelop.TestSupport;
using static IDevelop.TestSupport.FakeRule;
using static IDevelop.TestSupport.Processes;

namespace IDevelop.Core.Tests;

[Collection(ProcessCollection.Name)]
public sealed class ChildProcessTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Processes _spawned = new();
    private readonly FakeClients _fakes;
    private readonly string _folder;
    private readonly string _evidence;

    public ChildProcessTests()
    {
        _fakes = new FakeClients(_temp.Create("bin"));
        _folder = _temp.Create("folder");
        _evidence = _temp.Create("evidence");
    }

    public void Dispose()
    {
        _spawned.Dispose();
        _temp.Dispose();
    }

    [GitBashFact]
    public async Task Stopping_a_client_stops_the_commands_Git_Bash_runs_for_it()
    {
        var sleeper = Path.Combine(_evidence, "sleep.pid");
        var shim = Path.Combine(_fakes.Folder, "bash-client.cmd");
        File.WriteAllText(shim, $"@\"{GitBashFactAttribute.Bash}\" -c \"bash -c 'sleep 300 & cat /proc/$!/winpid > {sleeper.Replace('\\', '/')}; wait'\"\r\n");
        var client = ChildProcess.Start(new ResolvedCommand(shim, IsBatchShim: true), [], _folder, ProcessLifetime.Standalone);
        _spawned.Add(client.Identity.Id);
        var sleepId = await _spawned.PidAsync(sleeper);

        client.StopTree();

        AssertGone(client.Identity.Id);
        AssertGone(sleepId);
        client.Dispose();
    }

    [WindowsFact]
    public async Task Closing_a_childs_job_as_Windows_does_when_iDevelop_exits_stops_everything_it_started()
    {
        var sleeper = Path.Combine(_evidence, "sleeper.pid");
        var shim = _fakes.Install("client", On().SpawnThroughCmd(sleeper).Hang());
        var client = ChildProcess.Start(new ResolvedCommand(shim, IsBatchShim: true), [], _folder, ProcessLifetime.Standalone);
        _spawned.Add(client.Identity.Id);
        var sleeperId = await _spawned.PidAsync(sleeper);

        client.Dispose();

        AssertGone(client.Identity.Id);
        AssertGone(sleeperId);
    }
}
