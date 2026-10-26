using IDevelop.Workflows;

namespace IDevelop.Clients;

internal enum QuestionHandling { Decline, Surface }

internal enum PermissionPrompts { None, Host }

internal abstract record NativePolicy
{
    private NativePolicy() { }

    public sealed record Claude(string Mode) : NativePolicy;

    public sealed record Codex(string Sandbox) : NativePolicy
    {
        public string ApprovalPolicy => "never";

        public string ApprovalsReviewer => "user";
    }

    public sealed record Pi : NativePolicy;

    public sealed record Antigravity(string Mode) : NativePolicy;
}

internal sealed record EffectivePolicy(NativePolicy Native, QuestionHandling Questions, PermissionPrompts PermissionPrompts);

internal static class ClientPolicy
{
    /// <param name="liveQuestions">Claude Code's questions and permission requests come to iDevelop on its own channel.</param>
    public static EffectivePolicy For(ClientId client, bool readOnly, ConversationMode mode, bool reviewer, bool liveQuestions = false)
    {
        var surfaces = client == ClientId.ClaudeCode && !reviewer && liveQuestions && mode switch
        {
            ConversationMode.Autonomous => false,
            ConversationMode.MayAsk or ConversationMode.Chat => true,
        };
        var reads = readOnly || reviewer;
        NativePolicy native = client switch
        {
            ClientId.ClaudeCode => new NativePolicy.Claude(reads ? "plan" : "bypassPermissions"),
            ClientId.Codex => new NativePolicy.Codex(reads ? "read-only" : "workspace-write"),
            ClientId.Pi => new NativePolicy.Pi(),
            ClientId.Antigravity => new NativePolicy.Antigravity(reads ? "plan" : "accept-edits"),
        };
        return new EffectivePolicy(native, surfaces ? QuestionHandling.Surface : QuestionHandling.Decline,
            surfaces ? PermissionPrompts.Host : PermissionPrompts.None);
    }
}
