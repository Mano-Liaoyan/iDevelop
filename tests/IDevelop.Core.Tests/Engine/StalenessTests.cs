using IDevelop.Engine;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Engine.EngineFixture;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.Core.Tests.Engine;

/// <summary>
/// A result is stale while its basis differs from the current one. An upstream rerun stops a running node after it, which
/// runs again in a fresh session once the upstream completes. A person's edit stops a running node, which waits for their Run.
/// </summary>
public sealed class StalenessTests : IAsyncLifetime
{
    private readonly EngineFixture _f = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _f.DisposeAsync().AsTask();

    [Fact]
    public async Task A_result_goes_stale_after_an_instruction_or_model_change_and_current_again_when_it_is_undone()
    {
        var a = _f.Node("A");
        _f.Install(Reply("s-a", "Report A"));
        var engine = await _f.Open();
        await engine.Run(a);
        await Until<NodeState.Completed>(engine, a);
        var original = _f.Workflow;

        engine.Update(original.Must(new WorkflowEdit.SetField(a, "instructions", "Do more.")));
        var afterInstruction = engine.State(a);
        engine.Update(original.Must(new WorkflowEdit.SetExecution(a, Agy with { Reasoning = "high" })));
        var afterModel = engine.State(a);
        engine.Update(original);

        Assert.Equal((new NodeState.Stale(), new NodeState.Stale(), new NodeState.Completed()), (afterInstruction, afterModel, engine.State(a)));
        Assert.Single(engine.History(a));
    }

    [Fact]
    public async Task A_result_goes_stale_when_its_inputs_change()
    {
        var (a, b, c) = (_f.Node("A"), _f.Node("B"), _f.Node("C"));
        _f.Connect(a, c);
        _f.Install(On().Choose((Fresh("A"), Reply("s-a", "Report A")), (Fresh("B"), Reply("s-b", "Report B")), (Fresh("C"), Reply("s-c", "Report C"))));
        var engine = await _f.Open();
        await engine.RunWorkflow();
        await Until<NodeState.Completed>(engine, b);
        await Until<NodeState.Completed>(engine, c);

        _f.Connect(b, c);
        engine.Update(_f.Workflow);

        Assert.Equal(new NodeState.Stale(), engine.State(c));
        Assert.Null(engine.CanRun(c));
    }

    [Fact]
    public async Task An_upstream_rerun_stops_a_running_node_after_it_which_runs_again_in_a_fresh_session_once_the_upstream_completes()
    {
        var (a, b) = (_f.Node("A"), _f.Node("B"));
        _f.Connect(a, b);
        var gate = _f.File("gate");
        _f.Install(On().Choose(
            (Fresh("A"), Reply("s-a", "Report A")),
            (Fresh("B"), Reply("s-b", "Report B", On().RecordArguments(_f.File("b.json")).WaitForFile(gate)))));
        var engine = await _f.Open();
        await engine.Run(a);
        await Until<NodeState.Running>(engine, b);

        await engine.Run(a);
        File.WriteAllText(gate, "");

        await Until(engine, b, state => state is NodeState.Completed && engine.History(a).Count == 2);
        Assert.Equal([new BasisInput(a, engine.History(a)[1].Id)], Assert.Single(engine.History(b)).Basis.Inputs.AsEnumerable());
        Assert.Contains(new NodeEntry.Stopped(), engine.Entries(b));
        Assert.All(engine.Entries(b).OfType<NodeEntry.TurnStarted>(), start => Assert.Null(start.Session));
        Assert.DoesNotContain("--conversation", _f.Arguments("b.json"));
    }

    [Fact]
    public async Task A_person_s_edit_of_a_running_node_stops_it_and_it_waits_stale_for_their_Run()
    {
        var a = _f.Node("A");
        _f.Install(On().WaitForFile(_f.File("never")));
        var engine = await _f.Open();
        await engine.Run(a);
        await Until<NodeState.Running>(engine, a);

        engine.Update(_f.Workflow.Must(new WorkflowEdit.SetField(a, "instructions", "Do more.")));

        await Until<NodeState.Stale>(engine, a);
        Assert.Equal(new NodeEntry.Stopped(), engine.Entries(a)[^1]);
        Assert.Null(engine.CanRun(a));
        Assert.Empty(engine.History(a));
        engine.Update(_f.Workflow);
        Assert.Equal(new NodeState.Idle(), engine.State(a));
    }

    [Fact]
    public async Task A_new_edge_from_a_node_without_a_result_stops_a_running_node()
    {
        var (a, b) = (_f.Node("A"), _f.Node("B"));
        _f.Install(On().WaitForFile(_f.File("never")));
        var engine = await _f.Open();
        await engine.Run(b);
        await Until<NodeState.Running>(engine, b);

        _f.Connect(a, b);
        engine.Update(_f.Workflow);

        await Until<NodeState.Stale>(engine, b);
        Assert.Equal(new NodeState.Idle(), engine.State(a));
    }
}
