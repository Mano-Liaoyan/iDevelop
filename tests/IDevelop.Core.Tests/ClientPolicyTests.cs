using IDevelop.Execution;
using IDevelop.Workflows;

namespace IDevelop.Core.Tests;

public class ClientPolicyTests
{
    [Fact]
    public void The_effective_policy_preserves_native_access_and_declines_every_permission_request()
    {
        (ClientId Client, bool ReadOnly, ConversationMode Mode, bool Reviewer, HostQuestions Host,
            NativePolicy Native, QuestionHandling Questions, PermissionPrompts Prompts, bool LiveQuestions)[] rows =
        [
            (ClientId.ClaudeCode, false, ConversationMode.Autonomous, false, new HostQuestions.DeferImmediately(), new NativePolicy.Claude("acceptEdits"), QuestionHandling.Decline, PermissionPrompts.None, false),
            (ClientId.ClaudeCode, true, ConversationMode.Autonomous, false, new HostQuestions.DeferImmediately(), new NativePolicy.Claude("plan"), QuestionHandling.Decline, PermissionPrompts.None, false),
            (ClientId.ClaudeCode, false, ConversationMode.Autonomous, true, new HostQuestions.DeferImmediately(), new NativePolicy.Claude("plan"), QuestionHandling.Decline, PermissionPrompts.None, false),
            (ClientId.ClaudeCode, false, ConversationMode.MayAsk, true, new HostQuestions.DeferImmediately(), new NativePolicy.Claude("plan"), QuestionHandling.Decline, PermissionPrompts.None, false),
            (ClientId.ClaudeCode, false, ConversationMode.Chat, true, HostQuestions.Bounded.Recommended, new NativePolicy.Claude("plan"), QuestionHandling.Decline, PermissionPrompts.None, false),
            (ClientId.ClaudeCode, false, ConversationMode.MayAsk, false, new HostQuestions.DeferImmediately(), new NativePolicy.Claude("acceptEdits"), QuestionHandling.Surface, PermissionPrompts.Host, true),
            (ClientId.ClaudeCode, false, ConversationMode.Chat, false, HostQuestions.Bounded.Recommended, new NativePolicy.Claude("acceptEdits"), QuestionHandling.Surface, PermissionPrompts.Host, true),
            (ClientId.ClaudeCode, true, ConversationMode.MayAsk, false, HostQuestions.Bounded.Recommended, new NativePolicy.Claude("plan"), QuestionHandling.Surface, PermissionPrompts.Host, true),
            (ClientId.ClaudeCode, true, ConversationMode.Chat, false, new HostQuestions.DeferImmediately(), new NativePolicy.Claude("plan"), QuestionHandling.Surface, PermissionPrompts.Host, true),
            (ClientId.ClaudeCode, false, ConversationMode.MayAsk, false, new HostQuestions.Disabled(), new NativePolicy.Claude("acceptEdits"), QuestionHandling.Decline, PermissionPrompts.None, false),
            (ClientId.ClaudeCode, true, ConversationMode.Chat, false, new HostQuestions.Disabled(), new NativePolicy.Claude("plan"), QuestionHandling.Decline, PermissionPrompts.None, false),
            (ClientId.Codex, false, ConversationMode.Chat, false, HostQuestions.Bounded.Recommended, new NativePolicy.Codex("workspace-write"), QuestionHandling.Decline, PermissionPrompts.None, false),
            (ClientId.Codex, true, ConversationMode.MayAsk, false, new HostQuestions.DeferImmediately(), new NativePolicy.Codex("read-only"), QuestionHandling.Decline, PermissionPrompts.None, false),
            (ClientId.Codex, false, ConversationMode.Chat, true, HostQuestions.Bounded.Recommended, new NativePolicy.Codex("read-only"), QuestionHandling.Decline, PermissionPrompts.None, false),
            (ClientId.Pi, false, ConversationMode.Chat, false, HostQuestions.Bounded.Recommended, new NativePolicy.Pi(), QuestionHandling.Decline, PermissionPrompts.None, false),
            (ClientId.Pi, true, ConversationMode.MayAsk, false, new HostQuestions.DeferImmediately(), new NativePolicy.Pi(), QuestionHandling.Decline, PermissionPrompts.None, false),
            (ClientId.Antigravity, false, ConversationMode.Chat, false, HostQuestions.Bounded.Recommended, new NativePolicy.Antigravity("accept-edits"), QuestionHandling.Decline, PermissionPrompts.None, false),
            (ClientId.Antigravity, true, ConversationMode.MayAsk, false, new HostQuestions.DeferImmediately(), new NativePolicy.Antigravity("plan"), QuestionHandling.Decline, PermissionPrompts.None, false),
        ];

        foreach (var row in rows)
        {
            var policy = ClientPolicy.For(row.Client, row.ReadOnly, row.Mode, row.Reviewer, row.Host);

            Assert.Equal((row.Native, row.Questions, row.Prompts, true, row.LiveQuestions),
                (policy.Native, policy.Questions, policy.PermissionPrompts, policy.Capabilities.StreamsText, policy.Capabilities.LiveQuestions));
            if (policy.Native is NativePolicy.Codex codex)
            {
                Assert.Equal(("never", "user"), (codex.ApprovalPolicy, codex.ApprovalsReviewer));
            }
        }

        Assert.Equal("Permission requests are declined.", ClientPolicy.For(ClientId.ClaudeCode, true, ConversationMode.MayAsk, false,
            new HostQuestions.DeferImmediately()).Capabilities.Limitations);
        Assert.Equal("Questions arrive as text. Permission requests are declined.", ClientPolicy.For(ClientId.Codex, false, ConversationMode.Chat, false,
            HostQuestions.Bounded.Recommended).Capabilities.Limitations);
        Assert.Equal("Pi has no read-only mode, so this task cannot start. Questions arrive as text. Pi has no permission system.",
            ClientPolicy.For(ClientId.Pi, true, ConversationMode.Chat, false, HostQuestions.Bounded.Recommended).Capabilities.Limitations);
        Assert.Equal("Questions arrive as text. No interactive permission channel is available. Resuming an earlier writable session may retain write access.",
            ClientPolicy.For(ClientId.Antigravity, true, ConversationMode.Chat, false, HostQuestions.Bounded.Recommended).Capabilities.Limitations);
    }
}
