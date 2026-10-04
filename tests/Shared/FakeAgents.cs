using IDevelop.Workflows;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.TestSupport;

/// <summary>Fake answers to each client's list and sign-in commands, from the recorded probes.</summary>
internal static class FakeAgents
{
    public static readonly FakeRule ClaudeSignedIn = On("auth", "status").Print("""{"loggedIn":true}""");

    public static readonly FakeRule CodexModels = On("debug", "models").Replay(Fixture.Path("codex-debug-models.json"));

    public static readonly FakeRule CodexSignedIn = On("login", "status").Print("Logged in using ChatGPT");

    // Pi's RPC mode keeps running after it answers and exits when its input ends, as the real one does.
    public static readonly FakeRule PiModels = On("--mode", "rpc", "--no-session").Replay(Fixture.Path("pi-rpc-models.jsonl")).WaitForStdinEnd();

    public static readonly FakeRule AgyModels = On("models").Replay(Fixture.Path("agy-models.txt"));

    public static FakeRule PiProvider(string provider, string status) =>
        On("auth", "check", "--provider", provider, "--json").Print($$"""{"status":"{{status}}"}""");

    /// <summary>One client as on the probing machine, plus the rules for its runs.</summary>
    public static void Install(FakeClients fakes, ClientId client, params FakeRule[] runs) => _ = client switch
    {
        ClientId.ClaudeCode => fakes.Install("claude", [ClaudeSignedIn, .. runs]),
        ClientId.Codex => fakes.Install("codex", [CodexModels, CodexSignedIn, .. runs]),
        ClientId.Pi => fakes.Install("pi", [PiModels, PiProvider("deepseek", "ready"), PiProvider("openai-codex", "invalid"), .. runs]),
        ClientId.Antigravity => fakes.Install("agy", [AgyModels, .. runs]),
    };
}
