using IDevelop.Clients;
using IDevelop.Workflows;

namespace IDevelop.Core.Tests;

public class ClientPolicyTests
{
    private const string Session = "session-1";

    private static readonly string[] ClaudeStart = ["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages", "--model", "m", "--effort", "high"];
    private static readonly string[] AgyStart = ["--input-format", "stream-json", "--output-format", "stream-json", "--model", "m", "--effort", "high"];
    private static readonly string[] Decline = ["--permission-prompts", "none"];
    private static readonly string[] Surface = ["--permission-prompt-tool", "stdio"];

    /// <summary>
    /// A start goes through <see cref="LaunchPlan.For"/>, so this table holds each client's permission flags for every kind of
    /// work and conversation mode, whatever shape the launch takes inside (ADR 0039).
    /// </summary>
    [Fact]
    public void A_start_gives_each_client_the_permission_flags_of_the_nodes_work_and_conversation()
    {
        (Blueprint Blueprint, ConversationMode Mode, string ClaudeAccess, string[] ClaudePrompts, string Sandbox, string AgyMode, bool PiRuns)[] rows =
        [
            (BuiltInBlueprints.Implement, ConversationMode.Autonomous, "bypassPermissions", Decline, "workspace-write", "accept-edits", true),
            (BuiltInBlueprints.Implement, ConversationMode.MayAsk, "bypassPermissions", Surface, "workspace-write", "accept-edits", true),
            (BuiltInBlueprints.Implement, ConversationMode.Chat, "bypassPermissions", Surface, "workspace-write", "accept-edits", true),
            (BuiltInBlueprints.Merge, ConversationMode.Autonomous, "bypassPermissions", Decline, "workspace-write", "accept-edits", true),
            (BuiltInBlueprints.Merge, ConversationMode.MayAsk, "bypassPermissions", Surface, "workspace-write", "accept-edits", true),
            (BuiltInBlueprints.Merge, ConversationMode.Chat, "bypassPermissions", Surface, "workspace-write", "accept-edits", true),
            (BuiltInBlueprints.Plan, ConversationMode.Autonomous, "plan", Decline, "read-only", "plan", false),
            (BuiltInBlueprints.Plan, ConversationMode.MayAsk, "plan", Surface, "read-only", "plan", false),
            (BuiltInBlueprints.Plan, ConversationMode.Chat, "plan", Surface, "read-only", "plan", false),
            (BuiltInBlueprints.Review, ConversationMode.Autonomous, "plan", Decline, "read-only", "plan", false),
            (BuiltInBlueprints.Review, ConversationMode.MayAsk, "plan", Decline, "read-only", "plan", false),
            (BuiltInBlueprints.Review, ConversationMode.Chat, "plan", Decline, "read-only", "plan", false),
        ];
        foreach (var row in rows)
        {
            var claude = Start(ClientId.ClaudeCode, row.Blueprint, row.Mode).Plan!;
            Assert.Equal([.. ClaudeStart, "--permission-mode", row.ClaudeAccess, "--resume", Session, .. row.ClaudePrompts], claude.Launch.Arguments.ToArray());
            Assert.Equal(row.ClaudePrompts == Surface, claude.Request.Surfaces);

            var codex = Start(ClientId.Codex, row.Blueprint, row.Mode).Plan!;
            Assert.Equal(["app-server", "-c", "approval_policy=never", "-c", "features.default_mode_request_user_input=false"], codex.Launch.Arguments.ToArray());
            Assert.False(codex.Request.Surfaces);
            var protocol = codex.Client.Protocol(codex.Request);
            protocol.Start();
            Assert.Equal(
                $$$"""{"id":"thread-1","method":"thread/resume","params":{"model":"m","approvalPolicy":"never","approvalsReviewer":"user","sandbox":"{{{row.Sandbox}}}","threadId":"{{{Session}}}","excludeTurns":true}}""",
                protocol.Read("""{"id":"init-1","result":{}}""").Writes[1]);

            var agy = Start(ClientId.Antigravity, row.Blueprint, row.Mode).Plan!;
            Assert.Equal([.. AgyStart, "--mode", row.AgyMode, "--print=", "--conversation", Session], agy.Launch.Arguments.ToArray());
            Assert.False(agy.Request.Surfaces);

            var pi = Start(ClientId.Pi, row.Blueprint, row.Mode);
            if (row.PiRuns)
            {
                Assert.Equal(["-p", "--mode", "json", "--model", "m", "--thinking", "high", "--session-id", Session], pi.Plan!.Launch.Arguments.ToArray());
                Assert.False(pi.Plan.Request.Surfaces);
            }
            else
            {
                Assert.Equal("Pi has no read-only mode.", pi.Problem);
            }
        }
    }

    private static (LaunchPlan? Plan, string? Problem) Start(ClientId client, Blueprint blueprint, ConversationMode mode)
    {
        var node = new TaskDefinition(new TaskId(Guid.NewGuid()), blueprint) { Execution = new ExecutionSettings(client) { Model = "m", Reasoning = "high" }, Conversation = mode };
        var ready = new ClientStatus.Ready(new ResolvedCommand("/usr/bin/client", false), [new ModelOption("m", "M", ["high"])]);
        return LaunchPlan.For(node, "/project", "banana", Session, new Dictionary<ClientId, ClientStatus> { [client] = ready }, null);
    }
}
