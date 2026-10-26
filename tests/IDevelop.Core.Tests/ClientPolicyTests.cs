using IDevelop.Clients;
using IDevelop.Workflows;

namespace IDevelop.Core.Tests;

public class ClientPolicyTests
{
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
