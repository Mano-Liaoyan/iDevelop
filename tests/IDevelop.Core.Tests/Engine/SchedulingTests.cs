using IDevelop.Engine;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Engine.EngineFixture;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.Core.Tests.Engine;

/// <summary>Every ready node starts at once, and a completion hands its result to each node after it.</summary>
public sealed class SchedulingTests : IAsyncLifetime
{
    private readonly EngineFixture _f = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _f.DisposeAsync().AsTask();

    [Fact]
    public async Task Run_Workflow_fans_out_at_once_and_fans_in_once_every_predecessor_completed()
    {
        var (a, b, c, d) = (_f.Node("A"), _f.Node("B"), _f.Node("C"), _f.Node("D"));
        _f.Connect(a, b);
        _f.Connect(a, c);
        _f.Connect(b, d);
        _f.Connect(c, d);
        var gate = _f.File("gate");
        _f.Install(On().Choose(
            (Fresh("A"), Reply("s-a", "Report A")),
            (Fresh("B"), Gated(gate, "s-b", "Report B")),
            (Fresh("C"), Gated(gate, "s-c", "Report C")),
            (Fresh("D"), Reply("s-d", "Report D", On().CapturePrompt(_f.File("d.txt"))))));
        var engine = await _f.Open();

        await engine.RunWorkflow();

        await Until<NodeState.Running>(engine, b);
        await Until<NodeState.Running>(engine, c);
        Assert.Equal((new NodeState.Completed(), new NodeState.Idle()), (engine.State(a), engine.State(d)));
        Assert.Equal("It waits for B.", engine.CanRun(d));
        File.WriteAllText(gate, "");
        await Until<NodeState.Completed>(engine, d);
        var prompt = _f.Prompt("d.txt");
        Assert.Contains("### B\n\nReport B\n\n### C\n\nReport C", prompt, StringComparison.Ordinal);
        Assert.Equal([engine.History(b)[0].Id, engine.History(c)[0].Id], engine.History(d)[0].Basis.Inputs.AsEnumerable());
        Assert.Equal("Report D", engine.History(d)[0].Report);
    }

    [Fact]
    public async Task A_node_s_Run_runs_it_and_then_each_node_after_it()
    {
        var (a, b) = (_f.Node("A"), _f.Node("B"));
        _f.Connect(a, b);
        _f.Install(On().Choose((Fresh("A"), Reply("s-a", "Report A")), (Fresh("B"), Reply("s-b", "Report B"))));
        var engine = await _f.Open();

        Assert.Equal("It waits for A.", engine.CanRun(b));
        await engine.Run(a);

        await Until<NodeState.Completed>(engine, b);
        Assert.Equal([engine.History(a)[0].Id], engine.History(b)[0].Basis.Inputs.AsEnumerable());
    }

    private static FakeRule Gated(string gate, string session, string reply) => Reply(session, reply, On().WaitForFile(gate));
}
