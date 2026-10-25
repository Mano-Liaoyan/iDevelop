using IDevelop.Engine;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Engine.EngineFixture;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.Core.Tests.Engine;

/// <summary>An Approval node's agent is the person, who approves what the nodes before it handed on or sends it back.</summary>
public sealed class ApprovalTests : IAsyncLifetime
{
    private readonly EngineFixture _f = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _f.DisposeAsync().AsTask();

    [Fact]
    public async Task A_send_back_continues_the_predecessor_s_session_and_an_approval_hands_its_new_result_on()
    {
        var (a, p, c) = (_f.Node("A"), _f.Approval("Check"), _f.Node("C"));
        _f.Connect(a, p);
        _f.Connect(p, c);
        _f.Install(
            Reply("s-a", "Report A fixed", FakeAgents.Resuming(ClientId.Antigravity, "s-a").CapturePrompt(_f.File("note.txt"))),
            On().Choose(
                (Fresh("A"), Reply("s-a", "Report A")),
                (Fresh("C"), Reply("s-c", "Report C", On().CapturePrompt(_f.File("c.txt"))))));
        var engine = await _f.Open();
        await engine.RunWorkflow();
        Assert.Equal(new NodeState.InputRequired(NodeState.Approval), await Until<NodeState.InputRequired>(engine, p));
        Assert.Equal("It waits for Check.", engine.CanRun(c));

        await engine.SendBack(p, "Fix the typo.");

        Assert.Equal(new NodeState.Running(), engine.State(a));
        await Until<NodeState.InputRequired>(engine, p);
        Assert.Equal("Fix the typo.", _f.Prompt("note.txt"));
        await engine.Approve(p);
        await Until<NodeState.Completed>(engine, c);
        Assert.Equal("### A\n\nReport A fixed", Assert.Single(engine.History(p)).Report);
        Assert.Contains("### Check\n\n### A\n\nReport A fixed", _f.Prompt("c.txt"), StringComparison.Ordinal);
    }
}
