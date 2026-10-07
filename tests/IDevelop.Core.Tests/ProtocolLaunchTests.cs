using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;

namespace IDevelop.Core.Tests;

public sealed class ProtocolLaunchTests
{
    private static readonly string[] ClaudeEditNone = ["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages", "--model", "m", "--effort", "high", "--permission-mode", "acceptEdits", "--resume", "session-1", "--permission-prompts", "none"];
    private static readonly string[] ClaudePlanNone = ["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages", "--model", "m", "--effort", "high", "--permission-mode", "plan", "--resume", "session-1", "--permission-prompts", "none"];
    private static readonly string[] ClaudeEditHost = ["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages", "--model", "m", "--effort", "high", "--permission-mode", "acceptEdits", "--resume", "session-1", "--permission-prompt-tool", "stdio"];
    private static readonly string[] ClaudePlanHost = ["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages", "--model", "m", "--effort", "high", "--permission-mode", "plan", "--resume", "session-1", "--permission-prompt-tool", "stdio"];
    private static readonly string[] Codex = ["app-server", "-c", "approval_policy=never", "-c", "features.default_mode_request_user_input=false"];
    private static readonly string[] Pi = ["-p", "--mode", "json", "--model", "m", "--thinking", "high", "--session-id", "session-1"];
    private static readonly string[] AgyEdit = ["--input-format", "stream-json", "--output-format", "stream-json", "--model", "m", "--effort", "high", "--mode", "accept-edits", "--print=", "--conversation", "session-1"];
    private static readonly string[] AgyPlan = ["--input-format", "stream-json", "--output-format", "stream-json", "--model", "m", "--effort", "high", "--mode", "plan", "--print=", "--conversation", "session-1"];

    [Fact]
    public void Every_client_launches_with_literal_arguments_for_each_policy_row()
    {
        (bool ReadOnly, ConversationMode Mode, bool Reviewer, bool Host, string[] Claude, string[] Agy)[] rows =
        [
            (false, ConversationMode.Autonomous, false, false, ClaudeEditNone, AgyEdit),
            (true, ConversationMode.Autonomous, false, false, ClaudePlanNone, AgyPlan),
            (false, ConversationMode.Autonomous, false, true, ClaudeEditNone, AgyEdit),
            (true, ConversationMode.Autonomous, false, true, ClaudePlanNone, AgyPlan),
            (false, ConversationMode.MayAsk, false, true, ClaudeEditHost, AgyEdit),
            (true, ConversationMode.MayAsk, false, true, ClaudePlanHost, AgyPlan),
            (false, ConversationMode.Chat, false, true, ClaudeEditHost, AgyEdit),
            (true, ConversationMode.Chat, false, true, ClaudePlanHost, AgyPlan),
            (false, ConversationMode.MayAsk, false, false, ClaudeEditNone, AgyEdit),
            (true, ConversationMode.MayAsk, false, false, ClaudePlanNone, AgyPlan),
            (false, ConversationMode.Chat, false, false, ClaudeEditNone, AgyEdit),
            (true, ConversationMode.Chat, false, false, ClaudePlanNone, AgyPlan),
            (false, ConversationMode.Autonomous, true, true, ClaudePlanNone, AgyPlan),
            (false, ConversationMode.MayAsk, true, true, ClaudePlanNone, AgyPlan),
            (false, ConversationMode.Chat, true, true, ClaudePlanNone, AgyPlan),
            (true, ConversationMode.Chat, true, false, ClaudePlanNone, AgyPlan),
        ];
        foreach (var row in rows)
        {
            foreach (var client in Clients.All)
            {
                var request = new LaunchRequest("m", "high", "banana")
                {
                    ReadOnly = row.ReadOnly, ResumeSession = "session-1",
                    Policy = ClientPolicy.For(client, row.ReadOnly, row.Mode, row.Reviewer, row.Host ? new HostQuestions.DeferImmediately() : new HostQuestions.Disabled()),
                };
                var actual = Clients.Get(client).Launch(request);
                Assert.Equal(client switch
                {
                    ClientId.ClaudeCode => row.Claude,
                    ClientId.Codex => Codex,
                    ClientId.Pi => Pi,
                    ClientId.Antigravity => row.Agy,
                }, actual.Arguments.ToArray());
            }
        }
    }

    [Fact]
    public void Fresh_turns_without_effort_have_literal_arguments_and_one_shot_input()
    {
        Assert.Equal(["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages", "--model", "m", "--permission-mode", "acceptEdits", "--permission-prompts", "none"],
            Clients.Get(ClientId.ClaudeCode).Launch(new LaunchRequest("m", null, "banana")).Arguments.ToArray());
        Assert.Equal(["app-server", "-c", "approval_policy=never", "-c", "features.default_mode_request_user_input=false"],
            Clients.Get(ClientId.Codex).Launch(new LaunchRequest("m", null, "banana")).Arguments.ToArray());
        var pi = Clients.Get(ClientId.Pi).Protocol(new LaunchRequest("m", null, "banana")).Start();
        Assert.Equal(["banana"], pi.Writes.ToArray());
        Assert.True(pi.CloseInput);
        var agy = Clients.Get(ClientId.Antigravity).Protocol(new LaunchRequest("m", null, "banana")).Start();
        Assert.Equal(["""{"event":"user","message":{"role":"user","content":"banana"}}""" + "\n"], agy.Writes.ToArray());
        Assert.True(agy.CloseInput);
    }
}
