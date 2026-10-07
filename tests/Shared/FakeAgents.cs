using System.Text.Json;
using IDevelop.Workflows;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.TestSupport;

/// <summary>Fake answers to each client's list and sign-in commands, from the recorded probes, and each client's turns.</summary>
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

    /// <summary>One client as on the probing machine, plus the rules for its runs. Returns the shim's path.</summary>
    public static string Install(FakeClients fakes, ClientId client, params FakeRule[] runs) => client switch
    {
        ClientId.ClaudeCode => fakes.Install("claude", [ClaudeSignedIn, .. runs]),
        ClientId.Codex => fakes.Install("codex", [CodexModels, CodexSignedIn, .. runs]),
        ClientId.Pi => fakes.Install("pi", [PiModels, PiProvider("deepseek", "ready"), PiProvider("openai-codex", "invalid"), .. runs]),
        ClientId.Antigravity => fakes.Install("agy", [AgyModels, .. runs]),
    };

    /// <summary>A turn that starts a new session. Install it after <see cref="Resuming"/>, whose arguments it also matches.</summary>
    public static FakeRule Fresh(ClientId client) => client == ClientId.Codex ? On("app-server").Thread("thread/start") : On();

    /// <summary>A turn that resumes <paramref name="session"/>, in the client's own argument shape.</summary>
    public static FakeRule Resuming(ClientId client, string session) => client switch
    {
        ClientId.ClaudeCode => On().With("--resume", session),
        ClientId.Codex => On("app-server").Thread("thread/resume", session),
        ClientId.Pi => On().With("--session-id", session),
        ClientId.Antigravity => On().With("--conversation", session),
    };

    /// <summary>The line with which the client reports its session at the start of a turn.</summary>
    public static string SessionLine(ClientId client, string session) => client switch
    {
        ClientId.ClaudeCode => $$"""{"type":"system","subtype":"init","session_id":"{{session}}","model":"claude-haiku-4-5"}""",
        ClientId.Codex => JsonSerializer.Serialize(new { method = "thread/started", @params = new { thread = new { id = session } } }),
        ClientId.Pi => $$"""{"type":"session","id":"{{session}}"}""",
        ClientId.Antigravity => $$"""{"event":"init","conversation_id":"{{session}}"}""",
    };

    /// <summary>The lines with which the client ends a successful turn whose final text is <paramref name="text"/>.</summary>
    public static string[] ReplyLines(ClientId client, string text)
    {
        var json = JsonSerializer.Serialize(text);
        return client switch
        {
            ClientId.ClaudeCode => [$$$"""{"type":"result","subtype":"success","is_error":false,"result":{{{json}}}}"""],
            ClientId.Codex =>
            [
                JsonSerializer.Serialize(new { method = "item/completed", @params = new { item = new { id = "item_1", type = "agentMessage", text } } }),
                """{"method":"turn/completed","params":{"turn":{"id":"turn-1","status":"completed"}}}""",
            ],
            ClientId.Pi => [$$$"""{"type":"agent_end","messages":[{"role":"assistant","stopReason":"stop","content":[{"type":"text","text":{{{json}}}}]}]}"""],
            ClientId.Antigravity => [$$$"""{"event":"result","result":{"status":"SUCCESS","response":{{{json}}}}}"""],
        };
    }

    public static string AppLine(string line)
    {
        using var json = JsonDocument.Parse(line);
        var root = json.RootElement;
        var type = root.GetProperty("type").GetString();
        var item = root.TryGetProperty("item", out var found) ? found : (JsonElement?)null;
        object? translated = type switch
        {
            "thread.started" => new { method = "thread/started", @params = new { thread = new { id = root.GetProperty("thread_id").GetString() } } },
            "item.started" when item?.GetProperty("type").GetString() == "command_execution" => new { method = "item/started", @params = new { item = new { type = "commandExecution", command = item?.GetProperty("command").GetString() } } },
            "item.completed" when item?.GetProperty("type").GetString() == "agent_message" => new { method = "item/completed", @params = new { item = new { type = "agentMessage", id = item?.GetProperty("id").GetString(), text = item?.GetProperty("text").GetString() } } },
            "item.completed" when item?.GetProperty("type").GetString() == "error" => new { method = "error", @params = new { error = new { message = item?.GetProperty("message").GetString() } } },
            "error" => new { method = "error", @params = new { error = new { message = root.GetProperty("message").GetString() } } },
            "turn.completed" => new { method = "turn/completed", @params = new { turn = new { id = "turn-1", status = "completed" } } },
            "turn.failed" => new { method = "turn/completed", @params = new { turn = new { id = "turn-1", status = "failed", error = root.GetProperty("error") } } },
            _ => new { method = "ignored" },
        };
        return JsonSerializer.Serialize(translated);
    }

}
