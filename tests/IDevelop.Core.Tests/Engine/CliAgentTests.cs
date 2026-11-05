using System.Collections.Concurrent;
using System.Threading.Channels;
using IDevelop.Clients;
using IDevelop.Engine;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.TestSupport.FakeAgents;

namespace IDevelop.Core.Tests.Engine;

/// <summary>
/// A Claude Code turn ends when its client exits after its background work (ADR 0030), and a Claude Code editing node says
/// that every tool runs without asking for permission (ADR 0039). A Claude Code node that does not ask the person declines
/// a question.
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
            .WaitForStdinEnd().Sleep(2500).Print(ClaudeBackgroundEnd(Session, "Found 3 files.", Answer)));

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

        var (outcome, _) = await Turn(successExitMilliseconds: 200);

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

    public static TheoryData<ClientId, string, ConversationMode, bool> PermissionNotices => new()
    {
        { ClientId.ClaudeCode, "Implement", ConversationMode.Autonomous, true },
        { ClientId.ClaudeCode, "Implement", ConversationMode.Chat, true },
        { ClientId.ClaudeCode, "Merge", ConversationMode.Autonomous, true },
        { ClientId.ClaudeCode, "Plan", ConversationMode.Autonomous, false },
        { ClientId.ClaudeCode, "Review", ConversationMode.Autonomous, false },
        { ClientId.Codex, "Implement", ConversationMode.Autonomous, false },
        { ClientId.Antigravity, "Implement", ConversationMode.Autonomous, false },
    };

    [Theory]
    [MemberData(nameof(PermissionNotices))]
    public async Task Only_a_Claude_Code_node_that_may_edit_says_that_every_tool_runs_without_asking(ClientId client, string blueprint, ConversationMode mode, bool says)
    {
        Install(_fakes, client, Fresh(client).Print(SessionLine(client, Session)).Print(ReplyLines(client, "Done.")));

        var (_, events) = await Turn(node: Node(client, blueprint, mode));

        Assert.Equal(says, events.Contains(new AgentEvent.Notice("Every tool runs without asking for permission.")));
    }

    [Theory]
    [InlineData("Implement", ConversationMode.Autonomous)]
    [InlineData("Review", ConversationMode.Autonomous)]
    [InlineData("Review", ConversationMode.MayAsk)]
    [InlineData("Review", ConversationMode.Chat)]
    public async Task A_Claude_Code_node_that_does_not_ask_the_person_declines_a_question(string blueprint, ConversationMode mode)
    {
        var answer = _temp.Path("answer.json");
        Install(_fakes, ClientId.ClaudeCode, Fresh(ClientId.ClaudeCode).Print(SessionLine(ClientId.ClaudeCode, Session))
            .Print(ClaudeAsks("Which color?", "Red", "Blue")).ReadLine(answer).Print(ReplyLines(ClientId.ClaudeCode, "Done.")));

        var (_, events) = await Turn(node: Node(ClientId.ClaudeCode, blueprint, mode));

        Assert.Contains(new AgentEvent.Notice("iDevelop declined the question, because this node does not ask the person: Which color?"), events);
        Assert.Empty(events.OfType<AgentEvent.QuestionAsked>());
        Assert.Contains("\"behavior\":\"deny\"", await File.ReadAllTextAsync(answer));
    }

    private static TaskDefinition Node(ClientId client, string blueprint, ConversationMode mode) =>
        new(TestTasks.Design, BuiltInBlueprints.All.Single(candidate => candidate.Name == blueprint))
        {
            Execution = new ExecutionSettings(client)
            {
                Model = client switch
                {
                    ClientId.ClaudeCode => "claude-haiku-4-5",
                    ClientId.Codex => "gpt-6-sol",
                    _ => EngineFixture.Agy.Model,
                },
                Reasoning = client == ClientId.Antigravity ? EngineFixture.Agy.Reasoning : "high",
            },
            Conversation = mode,
        };

    // A fake client is a dotnet process. Under the load of the whole suite on Windows, it can take more than 200 ms to exit
    // after it reports success, so a turn that expects a clean exit gives it 2 seconds.
    private async Task<(TurnOutcome Outcome, AgentEvent[] Events)> Turn(int successExitMilliseconds = 2000, TaskDefinition? node = null)
    {
        node ??= TestNodes.Implement(TestTasks.Design, "Survey", "Survey the files",
            execution: new ExecutionSettings(ClientId.ClaudeCode) { Model = "claude-haiku-4-5", Reasoning = "high" });
        var events = new ConcurrentQueue<AgentEvent>();
        var agent = new CliAgent(await _fakes.DiscoverAsync(), _project) { SuccessExitTime = TimeSpan.FromMilliseconds(successExitMilliseconds) };
        var outcome = await agent.Turn(node, _project, "Survey the files", null, events.Enqueue, Channel.CreateUnbounded<QuestionReply>().Reader, CancellationToken.None).WaitAsync(Patience);
        return (outcome, [.. events]);
    }
}
