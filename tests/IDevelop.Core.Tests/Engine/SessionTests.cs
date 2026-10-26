using IDevelop.Engine;
using IDevelop.Clients;
using IDevelop.Nodes;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Engine.EngineFixture;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.Core.Tests.Engine;

/// <summary>
/// A person's message continues the node's session, a Retry starts a fresh one, and a turn that had no end when iDevelop
/// stopped reopens as interrupted.
/// </summary>
public sealed class SessionTests : IAsyncLifetime
{
    private readonly EngineFixture _f = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _f.DisposeAsync().AsTask();

    [Fact]
    public async Task A_person_s_message_continues_a_Chat_node_s_session_and_Approve_takes_its_last_reply()
    {
        var a = _f.Node("A", ConversationMode.Chat);
        _f.Install(
            Reply("s-a", "Second", FakeAgents.Resuming(ClientId.Antigravity, "s-a").RecordArguments(_f.File("continued.json"))),
            Reply("s-a", "First"));
        var engine = await _f.Open();
        await engine.Run(a);
        await Until<NodeState.InputRequired>(engine, a);

        await engine.Send(a, "Go on.", interrupt: false);

        Assert.Equal(new NodeState.Running(), engine.State(a));
        Assert.Equal(new NodeState.InputRequired(NodeState.Reply), await Until<NodeState.InputRequired>(engine, a));
        Assert.Contains("--conversation", _f.Arguments("continued.json"));
        Assert.Contains(new NodeEntry.PersonMessage("Go on."), engine.Entries(a));
        Assert.Equal(new NodeEntry.TurnStarted(engine.Entries(a).OfType<NodeEntry.TurnStarted>().First().Basis, "s-a"), engine.Entries(a).OfType<NodeEntry.TurnStarted>().Last());
        await engine.Approve(a);
        Assert.Equal((new NodeState.Completed(), "Second"), (engine.State(a), Assert.Single(engine.History(a)).Report));
    }

    [Fact]
    public async Task A_message_with_interrupt_stops_the_running_turn_and_continues_the_session_with_it()
    {
        var a = _f.Node("A");
        _f.Install(
            Reply("s-a", "Interrupted and done", FakeAgents.Resuming(ClientId.Antigravity, "s-a")),
            On().Print(FakeAgents.SessionLine(ClientId.Antigravity, "s-a")).WaitForFile(_f.File("never")));
        var engine = await _f.Open();
        await engine.Run(a);
        await Eventually(() => engine.Entries(a).Any(entry => entry is NodeEntry.Agent { Event: AgentEvent.SessionStarted }));

        await engine.Send(a, "Stop and finish.", interrupt: true);

        await Until<NodeState.Completed>(engine, a);
        Assert.Equal("Interrupted and done", Assert.Single(engine.History(a)).Report);
        Assert.Contains(new NodeEntry.Stopped(), engine.Entries(a));
    }

    [Fact]
    public async Task A_message_that_waits_for_a_turn_the_person_stops_is_dropped_and_the_log_says_so()
    {
        var a = _f.Node("A");
        _f.Install(On().Print(FakeAgents.SessionLine(ClientId.Antigravity, "s-a")).WaitForFile(_f.File("never")));
        var engine = await _f.Open();
        await engine.Run(a);

        await engine.Send(a, "Later.", interrupt: false);
        await engine.Stop(a);

        Assert.Equal(new NodeEntry.MessageDropped("Later."), engine.Entries(a)[^1]);
        Assert.Equal(new NodeState.Idle(), engine.State(a));
    }

    [Fact]
    public async Task Approve_refuses_a_May_ask_node_that_asks_a_question()
    {
        var a = _f.Node("A", ConversationMode.MayAsk);
        _f.Install(Reply("s-a", "I need to know.\n\n```idevelop\n{\"status\": \"asking\", \"question\": \"Which file?\"}\n```"));
        var engine = await _f.Open();
        await engine.Run(a);

        Assert.Equal(new NodeState.InputRequired("Which file?"), await Until<NodeState.InputRequired>(engine, a));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.Approve(a));
        Assert.Empty(engine.History(a));
    }

    [Fact]
    public async Task Retry_starts_a_fresh_session()
    {
        var a = _f.Node("A");
        _f.Install(
            Reply("s-a", "Resumed", FakeAgents.Resuming(ClientId.Antigravity, "s-a").RecordArguments(_f.File("resumed.json"))),
            Reply("s-a", "Report A", On().RecordArguments(_f.File("fresh.json"))));
        var engine = await _f.Open();
        await engine.Run(a);
        await Until<NodeState.Completed>(engine, a);
        File.Delete(_f.File("fresh.json"));

        await engine.Retry(a);

        await Until(engine, a, state => state is NodeState.Completed && engine.History(a).Count == 2);
        Assert.DoesNotContain("--conversation", _f.Arguments("fresh.json"));
        Assert.False(File.Exists(_f.File("resumed.json")));
    }

    [Fact]
    public async Task A_planner_s_log_keeps_the_handles_its_prompt_named_so_its_proposal_reads_the_same_after_a_reopen()
    {
        var a = _f.Reader("A");
        var b = _f.Node("B");
        _f.Connect(a, b);
        _f.Install(Reply("s-a", "Plan"));
        var engine = await _f.Open();
        var context = PlanningContext.For(_f.Workflow, a, [BuiltInBlueprints.Implement], _ => false) with { Checking = [ClientId.Pi] };
        engine.Planning = _ => context;

        await engine.Run(a);
        await Until<NodeState.Completed>(engine, a);

        var expected = context.Handles(a.Value);
        Assert.Equal(ClientId.Pi, Assert.Single(expected.Checking));
        Assert.Equal(expected, engine.Planned(a));
        Assert.Null(engine.Planned(b));
        await using var reopened = new WorkflowEngine(_f.Project, _f.Workflow, await _f.Fakes.DiscoverAsync());
        Assert.Equal(expected, reopened.Planned(a));
    }

    [Fact]
    public async Task After_a_crash_a_turn_without_an_end_reopens_interrupted_and_a_message_continues_its_session()
    {
        var a = _f.Reader("A");
        _f.Install(Reply("s-a", "Continued", FakeAgents.Resuming(ClientId.Antigravity, "s-a")));
        var node = _f.Workflow.Tasks[a];
        var basis = new Basis([], AgentPrompt.For(node, ""), new AgentModel(ClientId.Antigravity, Agy.Model, Agy.Reasoning));
        var log = new NodeLog(_f.Project, a);
        log.Append(new NodeEntry.TurnStarted(basis, null));
        log.Append(new NodeEntry.Agent(new AgentEvent.SessionStarted("s-a")));
        File.AppendAllText(NodeLog.PathOf(_f.Project, a), """{"entry":"agent","event":{"type":"mess""");

        var engine = await _f.Open();

        Assert.Equal(new NodeState.InputRequired(NodeState.Interrupted), engine.State(a));
        await engine.Send(a, "Go on.", interrupt: false);
        await Until<NodeState.Completed>(engine, a);
        await using var reopened = new WorkflowEngine(_f.Project, _f.Workflow, await _f.Fakes.DiscoverAsync());
        Assert.Equal((new NodeState.Completed(), engine.History(a)[0]), (reopened.State(a), Assert.Single(reopened.History(a))));
        Assert.Contains(new NodeEntry.PersonMessage("Go on."), reopened.Entries(a));
        Assert.Contains("nodes/\n", File.ReadAllText(Path.Combine(_f.Project, ".idp", ".gitignore")), StringComparison.Ordinal);
    }
}
