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
            Assert.Equal([.. ClaudeStart, "--permission-mode", row.ClaudeAccess, "--resume", Session, .. row.ClaudePrompts],
                Start(ClientId.ClaudeCode, row.Blueprint, row.Mode).Plan!.Launch.Arguments.ToArray());

            var codex = Start(ClientId.Codex, row.Blueprint, row.Mode).Plan!;
            Assert.Equal(["app-server", "-c", "approval_policy=never", "-c", "features.default_mode_request_user_input=false"], codex.Launch.Arguments.ToArray());
            var protocol = codex.Client.Protocol(codex.Request);
            protocol.Start();
            Assert.Equal(
                $$$"""{"id":"thread-1","method":"thread/resume","params":{"model":"m","approvalPolicy":"never","approvalsReviewer":"user","sandbox":"{{{row.Sandbox}}}","threadId":"{{{Session}}}","excludeTurns":true}}""",
                protocol.Read("""{"id":"init-1","result":{}}""").Writes[1]);

            Assert.Equal([.. AgyStart, "--mode", row.AgyMode, "--print=", "--conversation", Session],
                Start(ClientId.Antigravity, row.Blueprint, row.Mode).Plan!.Launch.Arguments.ToArray());

            var pi = Start(ClientId.Pi, row.Blueprint, row.Mode);
            if (row.PiRuns)
            {
                Assert.Equal(["-p", "--mode", "json", "--model", "m", "--thinking", "high", "--session-id", Session], pi.Plan!.Launch.Arguments.ToArray());
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

    [Fact]
    public void The_effective_policy_bypasses_permissions_for_an_editing_Claude_Code_node_and_keeps_native_access_otherwise()
    {
        (ClientId Client, bool ReadOnly, ConversationMode Mode, bool Reviewer,
            NativePolicy Native, QuestionHandling Questions, PermissionPrompts Prompts)[] rows =
        [
            (ClientId.ClaudeCode, false, ConversationMode.Autonomous, false, new NativePolicy.Claude("bypassPermissions"), QuestionHandling.Decline, PermissionPrompts.None),
            (ClientId.ClaudeCode, true, ConversationMode.Autonomous, false, new NativePolicy.Claude("plan"), QuestionHandling.Decline, PermissionPrompts.None),
            (ClientId.ClaudeCode, false, ConversationMode.Autonomous, true, new NativePolicy.Claude("plan"), QuestionHandling.Decline, PermissionPrompts.None),
            (ClientId.ClaudeCode, false, ConversationMode.MayAsk, true, new NativePolicy.Claude("plan"), QuestionHandling.Decline, PermissionPrompts.None),
            (ClientId.ClaudeCode, false, ConversationMode.Chat, true, new NativePolicy.Claude("plan"), QuestionHandling.Decline, PermissionPrompts.None),
            (ClientId.ClaudeCode, false, ConversationMode.MayAsk, false, new NativePolicy.Claude("bypassPermissions"), QuestionHandling.Surface, PermissionPrompts.Host),
            (ClientId.ClaudeCode, false, ConversationMode.Chat, false, new NativePolicy.Claude("bypassPermissions"), QuestionHandling.Surface, PermissionPrompts.Host),
            (ClientId.ClaudeCode, true, ConversationMode.MayAsk, false, new NativePolicy.Claude("plan"), QuestionHandling.Surface, PermissionPrompts.Host),
            (ClientId.ClaudeCode, true, ConversationMode.Chat, false, new NativePolicy.Claude("plan"), QuestionHandling.Surface, PermissionPrompts.Host),
            (ClientId.Codex, false, ConversationMode.Chat, false, new NativePolicy.Codex("workspace-write"), QuestionHandling.Decline, PermissionPrompts.None),
            (ClientId.Codex, true, ConversationMode.MayAsk, false, new NativePolicy.Codex("read-only"), QuestionHandling.Decline, PermissionPrompts.None),
            (ClientId.Codex, false, ConversationMode.Chat, true, new NativePolicy.Codex("read-only"), QuestionHandling.Decline, PermissionPrompts.None),
            (ClientId.Pi, false, ConversationMode.Chat, false, new NativePolicy.Pi(), QuestionHandling.Decline, PermissionPrompts.None),
            (ClientId.Pi, true, ConversationMode.MayAsk, false, new NativePolicy.Pi(), QuestionHandling.Decline, PermissionPrompts.None),
            (ClientId.Antigravity, false, ConversationMode.Chat, false, new NativePolicy.Antigravity("accept-edits"), QuestionHandling.Decline, PermissionPrompts.None),
            (ClientId.Antigravity, true, ConversationMode.MayAsk, false, new NativePolicy.Antigravity("plan"), QuestionHandling.Decline, PermissionPrompts.None),
        ];

        foreach (var row in rows)
        {
            var policy = ClientPolicy.For(row.Client, row.ReadOnly, row.Mode, row.Reviewer);

            Assert.Equal((row.Native, row.Questions, row.Prompts), (policy.Native, policy.Questions, policy.PermissionPrompts));
            if (policy.Native is NativePolicy.Codex codex)
            {
                Assert.Equal(("never", "user"), (codex.ApprovalPolicy, codex.ApprovalsReviewer));
            }
        }
    }
}
