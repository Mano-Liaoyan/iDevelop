using IDevelop.Execution;
using IDevelop.TestSupport;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.Core.Tests;

[Collection(ProcessTests.Name)]
public sealed class ProbeTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly FakeClients _fakes;

    public ProbeTests() => _fakes = new FakeClients(_temp.Create("bin"));

    public void Dispose() => _temp.Dispose();

    private static Probe PiModelList => ((CatalogSource.Probed)Pi.Definition.Catalog).Probe;

    [Fact]
    public async Task A_probe_that_answered_gets_the_end_of_its_input_and_exits_on_its_own()
    {
        var stdin = Path.Combine(_temp.Create("evidence"), "stdin.txt");
        _fakes.Install("pi", On("--mode", "rpc", "--no-session").Replay(Fixture.Path("pi-rpc-models.jsonl")).CaptureStdin(stdin).Exit(0));

        var output = await Probes.RunAsync(_fakes.Resolver.Resolve("pi")!, PiModelList, CancellationToken.None);

        Assert.Equal((0, false), (output.ExitCode, output.TimedOut));
        Assert.Equal("""{"id":"idevelop-models","type":"get_available_models"}""" + "\n", File.ReadAllText(stdin));
        Assert.Equal(5, Assert.IsType<CatalogParse.Models>(Pi.ParseCatalog(output)).Options.Length);
    }

    [Fact]
    public async Task A_probe_that_answered_but_ignores_the_end_of_its_input_is_stopped()
    {
        _fakes.Install("pi", On("--mode", "rpc", "--no-session").Replay(Fixture.Path("pi-rpc-models.jsonl")).Hang());

        var output = await Probes.RunAsync(_fakes.Resolver.Resolve("pi")!, PiModelList, CancellationToken.None);

        Assert.Equal((null, false), (output.ExitCode, output.TimedOut));
        Assert.Equal(5, Assert.IsType<CatalogParse.Models>(Pi.ParseCatalog(output)).Options.Length);
    }
}
