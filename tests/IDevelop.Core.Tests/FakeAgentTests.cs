using IDevelop.Execution;
using IDevelop.TestSupport;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.Core.Tests;

public sealed class FakeAgentTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly FakeClients _fakes;

    public FakeAgentTests() => _fakes = new FakeClients(_temp.Create("bin"));

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task A_rule_matches_the_Git_command_after_its_leading_configuration_pairs()
    {
        _fakes.Install("git", On("rev-parse", "--git-path", "index").Print("matched").Exit(0));
        var git = _fakes.Resolver.Resolve("git")!;

        var configured = await Probes.RunAsync(git, new Probe(["-c", "core.fsmonitor=false", "-c", "core.trustctime=true", "rev-parse", "--git-path", "index"]));
        var other = await Probes.RunAsync(git, new Probe(["-c", "core.fsmonitor=false", "status", "rev-parse", "--git-path", "index"]));

        Assert.Equal((0, "matched\n"), (configured.ExitCode, configured.Stdout));
        Assert.Equal((99, ""), (other.ExitCode, other.Stdout));
    }
}
