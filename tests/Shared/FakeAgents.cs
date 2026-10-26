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
    public static readonly FakeRule PiModels = PiRpc();

    public static readonly FakeRule AgyModels = On("models").Replay(Fixture.Path("agy-models.txt"));

    /// <summary>
    /// Claude Code's skill listing, which a turn tells apart by its partial messages: its initialize answer lists the skills
    /// in its project folder and in <paramref name="folders"/> beside its own commands, compact and the bundled simplify
    /// alike, and its reload_skills answer lists the same skills with simplify among them.
    /// </summary>
    public static FakeRule ClaudeSkills(params string[] folders) => On("-p").Lacking("--include-partial-messages")
        .WaitForLine("initialize").ListSkills("claude", [".claude/skills", .. folders], builtIn: ["compact", "simplify"])
        .WaitForLine("reload_skills").ListSkills("claude-skills", [".claude/skills", .. folders], builtIn: ["simplify"]);

    /// <summary>Codex's answer to each skills/list: the skills in its project folders and in <paramref name="folders"/>.</summary>
    public static FakeRule CodexSkills(params string[] folders) => CodexSkills([.. folders], null, 0);

    /// <summary>
    /// Codex's answer to each skills/list, which holds the skills in <paramref name="late"/> only once the app-server has run
    /// <paramref name="lateAfterMs"/>, as Codex lists its remote plugins' skills once it has loaded them in the background.
    /// </summary>
    public static FakeRule CodexSkills(string[] folders, string[]? late, int lateAfterMs)
    {
        string[] all = [".agents/skills", ".codex/skills", .. folders];
        return On("app-server").Thread("skills/list").ListSkills("codex", all, late: late, lateAfterMs: lateAfterMs)
            .AnswerEach("skills/list", On().ListSkills("codex", all, late: late, lateAfterMs: lateAfterMs));
    }

    /// <summary>Pi's RPC mode, which answers the model list and the command list with the skills in its project folders and in <paramref name="folders"/>.</summary>
    public static FakeRule PiRpc(params string[] folders) => On("--mode", "rpc", "--no-session").Answer(
        ("get_available_models", On().Replay(Fixture.Path("pi-rpc-models.jsonl"))),
        ("get_commands", On().ListSkills("pi", [".pi/skills", ".agents/skills", .. folders]))).WaitForStdinEnd();

    public static FakeRule PiProvider(string provider, string status) =>
        On("auth", "check", "--provider", provider, "--json").Print($$"""{"status":"{{status}}"}""");

    /// <summary>One client as on the probing machine, plus the rules for its runs. Returns the shim's path.</summary>
    public static string Install(FakeClients fakes, ClientId client, params FakeRule[] runs) => client switch
    {
        ClientId.ClaudeCode => fakes.Install("claude", [ClaudeSignedIn, ClaudeSkills(), .. runs]),
        ClientId.Codex => fakes.Install("codex", [CodexModels, CodexSignedIn, CodexSkills(), .. runs]),
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

    /// <summary>
    /// Claude Code's stream when its agent hands work to a subagent in the background, as the #93 probe saw it: the Agent
    /// call, the backgrounded task, and a first result while the subagent still runs.
    /// </summary>
    public static string[] ClaudeBackgroundStart(string waiting = "The subagent runs in the background. I'll wait for it.") =>
    [
        """{"type":"assistant","message":{"id":"m-agent","content":[{"type":"tool_use","id":"toolu_1","name":"Agent","input":{"subagent_type":"general-purpose","description":"Survey the files","prompt":"List the files.","run_in_background":true}}]},"parent_tool_use_id":null}""",
        """{"type":"system","subtype":"task_started","task_id":"task-1","tool_use_id":"toolu_1","description":"Survey the files","subagent_type":"general-purpose","is_backgrounded":true}""",
        """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","content":"Async agent launched."}]},"parent_tool_use_id":null}""",
        $$$"""{"type":"result","subtype":"success","is_error":false,"result":{{{JsonSerializer.Serialize(waiting)}}}}""",
    ];

    /// <summary>
    /// The rest of that stream once stdin has closed: the subagent's own message, its task's notification, and a second
    /// turn of the session whose result, <paramref name="answer"/>, is the reply.
    /// </summary>
    public static string[] ClaudeBackgroundEnd(string session, string subagentSays, string answer) =>
    [
        $$$"""{"type":"assistant","message":{"id":"m-sub","content":[{"type":"text","text":{{{JsonSerializer.Serialize(subagentSays)}}}}]},"parent_tool_use_id":"toolu_1"}""",
        """{"type":"system","subtype":"task_notification","task_id":"task-1","tool_use_id":"toolu_1","status":"completed","output_file":"","summary":"Done"}""",
        SessionLine(ClientId.ClaudeCode, session),
        $$$"""{"type":"assistant","message":{"id":"m-answer","content":[{"type":"text","text":{{{JsonSerializer.Serialize(answer)}}}}]},"parent_tool_use_id":null}""",
        $$$"""{"type":"result","subtype":"success","is_error":false,"result":{{{JsonSerializer.Serialize(answer)}}},"origin":{"kind":"task-notification"}}""",
    ];

    /// <summary>
    /// Claude Code's request that the person answer <paramref name="question"/>, one choice of <paramref name="options"/>, as
    /// its AskUserQuestion tool sends it on the control channel.
    /// </summary>
    public static string ClaudeAsks(string question, params string[] options) => JsonSerializer.Serialize(new
    {
        type = "control_request", request_id = "ask-1",
        request = new
        {
            subtype = "can_use_tool", tool_name = "AskUserQuestion", tool_use_id = "toolu_ask",
            input = new { questions = new[] { new { header = "Choice", question, multiSelect = false, options = options.Select(label => new { label, description = $"Use {label}." }) } } },
        },
    });

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
