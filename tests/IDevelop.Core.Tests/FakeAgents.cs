using IDevelop.TestSupport;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.Core.Tests;

/// <summary>Fake answers to each client's list and sign-in commands, from the recorded probes.</summary>
internal static class FakeAgents
{
    public static readonly FakeRule ClaudeSignedIn = On("auth", "status").Print("""{"loggedIn":true}""");

    public static readonly FakeRule CodexModels = On("debug", "models").Replay(Fixture.Path("codex-debug-models.json"));

    public static readonly FakeRule CodexSignedIn = On("login", "status").Print("Logged in using ChatGPT");

    // Pi's RPC mode keeps running after it answers, as the real one does.
    public static readonly FakeRule PiModels = On("--mode", "rpc", "--no-session").Replay(Fixture.Path("pi-rpc-models.jsonl")).Hang();

    public static readonly FakeRule AgyModels = On("models").Replay(Fixture.Path("agy-models.txt"));

    public static FakeRule PiProvider(string provider, string status) =>
        On("auth", "check", "--provider", provider, "--json").Print($$"""{"status":"{{status}}"}""");

    /// <summary>All four clients installed and signed in, except Pi's openai-codex provider, as on the probing machine.</summary>
    public static void InstallAll(FakeClients fakes)
    {
        fakes.Install("claude", ClaudeSignedIn);
        fakes.Install("codex", CodexModels, CodexSignedIn);
        fakes.Install("pi", PiModels, PiProvider("deepseek", "ready"), PiProvider("openai-codex", "invalid"));
        fakes.Install("agy", AgyModels);
    }
}
