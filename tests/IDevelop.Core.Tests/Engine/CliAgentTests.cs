using System.Collections.Concurrent;
using IDevelop.Clients;
using IDevelop.Engine;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.TestSupport.FakeAgents;

namespace IDevelop.Core.Tests.Engine;

/// <summary>
/// A Claude Code turn ends when its client exits after its background work (ADR 0030), and a Claude Code editing node says
/// that every tool runs without asking for permission (ADR 0039).
/// </summary>
public sealed class CliAgentTests : IDisposable
{
    private const string Session = "session-1";
    private const string Answer = "The subagent found 3 files.";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);
    private readonly TempFolder _temp = new();
    private readonly FakeClients _fakes;
    private readonly string _project;

    public CliAgentTests()
    {
        _fakes = new FakeClients(_temp.Create("bin"));
        _project = _temp.Create("project");
    }

    public void Dispose()
    {
        _fakes.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task The_turn_waits_past_the_success_exit_time_for_a_background_subagent_and_takes_the_later_result()
    {
        Install(_fakes, ClientId.ClaudeCode, Fresh(ClientId.ClaudeCode).Print(SessionLine(ClientId.ClaudeCode, Session)).Print(ClaudeBackgroundStart())
            .WaitForStdinEnd().Sleep(800).Print(ClaudeBackgroundEnd(Session, "Found 3 files.", Answer)));

        var (outcome, events) = await Turn();

        Assert.Equal(new TurnOutcome.Done(Answer), outcome);
        var lists = events.OfType<AgentEvent.BackgroundTasks>().ToArray();
        Assert.Equal(new BackgroundTask("Survey the files", IsSubagent: true), Assert.Single(lists[0].Tasks));
        Assert.Empty(lists[^1].Tasks);
    }

    [Fact]
    public async Task Once_the_background_work_has_ended_a_client_that_does_not_exit_still_meets_the_success_exit_time()
    {
        Install(_fakes, ClientId.ClaudeCode, Fresh(ClientId.ClaudeCode).Print(SessionLine(ClientId.ClaudeCode, Session)).Print(ClaudeBackgroundStart())
            .WaitForStdinEnd().Print(ClaudeBackgroundEnd(Session, "Found 3 files.", Answer)).Hang());

        var (outcome, _) = await Turn();

        Assert.Equal(new TurnOutcome.Failed("Claude Code reported success but did not shut down."), outcome);
    }

    [Fact]
    public async Task A_subagent_s_message_is_never_the_turn_s_reply()
    {
        Install(_fakes, ClientId.ClaudeCode, Fresh(ClientId.ClaudeCode).Print(SessionLine(ClientId.ClaudeCode, Session)).Print([
            """{"type":"assistant","message":{"id":"m-1","content":[{"type":"text","text":"Mine."}]},"parent_tool_use_id":null}""",
            """{"type":"assistant","message":{"id":"m-2","content":[{"type":"text","text":"The subagent's."}]},"parent_tool_use_id":"toolu_1"}""",
            """{"type":"result","subtype":"success","is_error":false,"result":""}"""]));

        var (outcome, _) = await Turn();

        Assert.Equal(new TurnOutcome.Done("Mine."), outcome);
    }

    [Fact]
    public async Task A_fresh_turn_of_a_Claude_Code_editing_node_says_that_every_tool_runs_without_asking()
    {
        Install(_fakes, ClientId.ClaudeCode, Fresh(ClientId.ClaudeCode).Print(SessionLine(ClientId.ClaudeCode, Session)).Print(ReplyLines(ClientId.ClaudeCode, "Done.")));

        var (_, events) = await Turn();

        Assert.Equal(new AgentEvent.Notice("Every tool runs without asking for permission."), events[0]);
    }

    private async Task<(TurnOutcome Outcome, AgentEvent[] Events)> Turn()
    {
        var node = TestNodes.Implement(TestTasks.Design, "Survey", "Survey the files",
            execution: new ExecutionSettings(ClientId.ClaudeCode) { Model = "claude-haiku-4-5", Reasoning = "high" });
        var events = new ConcurrentQueue<AgentEvent>();
        var agent = new CliAgent(await _fakes.DiscoverAsync(), _project) { SuccessExitTime = TimeSpan.FromMilliseconds(200) };
        var outcome = await agent.Turn(node, _project, "Survey the files", null, events.Enqueue, CancellationToken.None).WaitAsync(Patience);
        return (outcome, [.. events]);
    }
}
