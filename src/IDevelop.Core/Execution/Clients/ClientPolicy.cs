using IDevelop.Workflows;

namespace IDevelop.Execution;

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

internal sealed record EffectivePolicy(
    NativePolicy Native, QuestionHandling Questions, PermissionPrompts PermissionPrompts,
    ClientCapabilities Capabilities);

internal static class ClientPolicy
{
    public static EffectivePolicy For(ClientId client, bool readOnly, ConversationMode mode, bool reviewer, HostQuestions host)
    {
        var surfaces = client == ClientId.ClaudeCode && !reviewer && host is not HostQuestions.Disabled && mode switch
        {
            ConversationMode.Autonomous => false,
            ConversationMode.MayAsk or ConversationMode.Chat => true,
        };
        var reads = readOnly || reviewer;
        NativePolicy native = client switch
        {
            ClientId.ClaudeCode => new NativePolicy.Claude(reads ? "plan" : "acceptEdits"),
            ClientId.Codex => new NativePolicy.Codex(reads ? "read-only" : "workspace-write"),
            ClientId.Pi => new NativePolicy.Pi(),
            ClientId.Antigravity => new NativePolicy.Antigravity(reads ? "plan" : "accept-edits"),
        };
        var limitations = client switch
        {
            ClientId.ClaudeCode => surfaces
                ? "Permission requests are declined."
                : "Live questions are disabled. Permission requests are declined.",
            ClientId.Codex => "Questions arrive as text. Permission requests are declined.",
            ClientId.Pi => reads
                ? "Pi has no read-only mode, so this task cannot start. Questions arrive as text. Pi has no permission system."
                : "Questions arrive as text. Pi has no permission system.",
            ClientId.Antigravity => reads
                ? "Questions arrive as text. No interactive permission channel is available. Resuming an earlier writable session may retain write access."
                : "Questions arrive as text. No interactive permission channel is available.",
        };
        return new EffectivePolicy(native, surfaces ? QuestionHandling.Surface : QuestionHandling.Decline,
            surfaces ? PermissionPrompts.Host : PermissionPrompts.None,
            new ClientCapabilities(StreamsText: true, LiveQuestions: surfaces, limitations));
    }
}
