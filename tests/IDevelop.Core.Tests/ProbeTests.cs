using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.TestSupport.FakeRule;
using static IDevelop.TestSupport.Processes;

namespace IDevelop.Core.Tests;

[Collection(ProcessCollection.Name)]
public sealed class ProbeTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly FakeClients _fakes;

    public ProbeTests() => _fakes = new FakeClients(_temp.Create("bin"));

    public void Dispose() => _temp.Dispose();

    private static CatalogSource.Probed PiCatalog => (CatalogSource.Probed)Clients.Get(ClientId.Pi).Catalog;

    [Fact]
    public async Task A_probe_that_answered_gets_the_end_of_its_input_and_exits_on_its_own()
    {
        var stdin = Path.Combine(_temp.Create("evidence"), "stdin.txt");
        _fakes.Install("pi", On("--mode", "rpc", "--no-session").Replay(Fixture.Path("pi-rpc-models.jsonl")).CaptureStdin(stdin).Exit(0));

        var output = await Probes.RunAsync(_fakes.Resolver.Resolve("pi")!, PiCatalog.Probe);

        Assert.Equal((0, false), (output.ExitCode, output.TimedOut));
        Assert.Equal("""{"id":"idevelop-models","type":"get_available_models"}""" + "\n", File.ReadAllText(stdin));
        Assert.Equal(5, Assert.IsType<CatalogParse.Models>(PiCatalog.Parse(output)).Options.Length);
    }

    [Fact]
    public async Task A_probe_runs_in_the_temporary_folder_and_never_in_a_project()
    {
        var folder = Path.Combine(_temp.Create("evidence"), "folder.txt");
        _fakes.Install("pi", On("--mode", "rpc", "--no-session").RecordWorkingDirectory(folder).Replay(Fixture.Path("pi-rpc-models.jsonl")).Exit(0));

        await Probes.RunAsync(_fakes.Resolver.Resolve("pi")!, PiCatalog.Probe);

        Assert.Equal(Folders.AsCurrentFolder(Path.GetTempPath()), File.ReadAllText(folder));
    }

    [Fact]
    public async Task A_probe_that_does_not_answer_in_time_is_stopped_with_everything_it_started()
    {
        var sleeper = Path.Combine(_temp.Create("evidence"), "sleeper.pid");
        _fakes.Install("codex", On("login", "status").SpawnSleepingChild(sleeper).Hang());

        var output = await Probes.RunAsync(_fakes.Resolver.Resolve("codex")!, new Probe(["login", "status"]) { Timeout = TimeSpan.FromSeconds(5) });

        Assert.Equal((null, true), (output.ExitCode, output.TimedOut));
        AssertGone(int.Parse(File.ReadAllText(sleeper)));
    }

    [Fact]
    public async Task A_probe_that_answered_but_ignores_the_end_of_its_input_is_stopped()
    {
        _fakes.Install("pi", On("--mode", "rpc", "--no-session").Replay(Fixture.Path("pi-rpc-models.jsonl")).Hang());

        var output = await Probes.RunAsync(_fakes.Resolver.Resolve("pi")!, PiCatalog.Probe);

        Assert.Equal((null, false), (output.ExitCode, output.TimedOut));
        Assert.Equal(5, Assert.IsType<CatalogParse.Models>(PiCatalog.Parse(output)).Options.Length);
    }
}
