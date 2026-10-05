using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Execution.AgentEvent;

namespace IDevelop.Core.Tests;

public class ClientDefinitionTests
{
    private const string BadCodexModel = "The 'gpt-bogus-9' model is not supported when using Codex with a ChatGPT account.";

    private const string BadClaudeModel =
        "There's an issue with the selected model (claude-bogus-9). It may not exist or you may not have access to it. Run --model to pick a different model.";

    private const string PiRefreshFailure =
        "OAuth refresh failed for openai-codex: OpenAI Codex token refresh failed (401): {\n" +
        "  \"error\": {\n" +
        "    \"message\": \"Could not validate your refresh token. Please try signing in again.\",\n" +
        "    \"type\": \"invalid_request_error\",\n" +
        "    \"param\": null,\n" +
        "    \"code\": \"invalid_refresh_token\"\n" +
        "  }\n" +
        "}";

    [Fact]
    public void Claude_Code_reports_its_session_models_tool_and_result()
    {
        Assert.Equal(
            [
                new SessionStarted("847c08de-2ab8-4e5f-bcee-7d813def3756"),
                new Reported("claude-haiku-4-5", null),
                new Reported("claude-haiku-4-5-20251001", null),
                new Reported("claude-haiku-4-5-20251001", null),
                new ToolStarted("Write", @"C:\project\hello.txt"),
                new Reported("claude-haiku-4-5-20251001", null),
                new Message("DONE"),
                new Succeeded("DONE"),
            ],
            Events(ClientId.ClaudeCode, "claude-success.jsonl"));
    }

    [Fact]
    public void Claude_Code_fails_a_bad_model_with_its_own_message()
    {
        Assert.Equal(
            [
                new SessionStarted("0a78e77d-034b-493d-a042-0fe607c70c34"),
                new Reported("claude-bogus-9", null),
                new Message(BadClaudeModel),
                new Failed(BadClaudeModel),
            ],
            Events(ClientId.ClaudeCode, "claude-bad-model.jsonl"));
    }

    [Fact]
    public void Claude_Code_turns_each_permission_denial_into_a_notice()
    {
        var events = Clients.Get(ClientId.ClaudeCode).Interpret(
            """{"type":"result","subtype":"success","is_error":false,"result":"I could not run it.","permission_denials":[{"tool_name":"Bash"}]}""");

        Assert.Equal([new Notice("Claude Code denied Bash."), new Succeeded("I could not run it.")], events.ToArray());
    }

    [Fact]
    public void Codex_reports_its_thread_command_message_and_completed_turn()
    {
        Assert.Equal(
            [
                new SessionStarted("01a104d5-d442-71a1-9b08-8938c119e5ae"),
                new ToolStarted("command", @"pwsh.exe -Command ""Set-Content -LiteralPath .\\hello.txt -Value 'hi' -NoNewline"""),
                new Message("DONE"),
                new Succeeded(null),
            ],
            Events(ClientId.Codex, "codex-success.jsonl"));
    }

    [Fact]
    public void Codex_fails_a_bad_model_with_the_inner_api_message_and_keeps_earlier_errors_as_notices()
    {
        Assert.Equal(
            [
                new SessionStarted("01a104d9-a009-7be2-838d-52895b1024e6"),
                new Notice("Configured service tier `priority` is not advertised as supported for model `gpt-bogus-9` and will be omitted from requests."),
                new Notice("Model metadata for `gpt-bogus-9` not found. Defaulting to fallback metadata; this can degrade performance and cause issues."),
                new Notice(BadCodexModel),
                new Failed(BadCodexModel),
            ],
            Events(ClientId.Codex, "codex-bad-model.jsonl"));
    }

    [Fact]
    public void Pi_reports_the_served_model_and_level_and_succeeds_on_its_last_answer()
    {
        Assert.Equal(
            [
                new SessionStarted("01a104d6-5d29-70a3-b067-4dea17388eb1"),
                new Reported("deepseek/deepseek-v4-pro", "high"),
                new ToolStarted("write", "hello.txt"),
                new Reported("deepseek/deepseek-v4-pro", "high"),
                new Message("DONE"),
                new Succeeded("DONE"),
            ],
            Events(ClientId.Pi, "pi-success.jsonl"));
    }

    [Fact]
    public void Pi_fails_a_turn_that_ended_in_an_error_although_it_exits_zero()
    {
        Assert.Equal(
            [
                new SessionStarted("01a104d6-0e5d-7643-b15b-7044586e5c34"),
                new Reported("openai-codex/gpt-5.5", "low"),
                new Failed(PiRefreshFailure),
            ],
            Events(ClientId.Pi, "pi-auth-error.jsonl"));
    }

    [Fact]
    public void Antigravity_reports_its_conversation_tool_and_whole_response()
    {
        Assert.Equal(
            [
                new SessionStarted("88fcc1a4-0a4f-495c-a2db-b6fc830d0b4a"),
                new Reported("gemini-3.8-flash", null),
                new ToolStarted("write_to_file", @"C:\project\hello.txt"),
                new Succeeded("DONE"),
            ],
            Events(ClientId.Antigravity, "agy-success.jsonl"));
    }

    [Fact]
    public void Antigravity_fails_a_bad_model_with_its_error()
    {
        Assert.Equal(
            [new Failed("invalid model selection (--model \"gemini-bogus-9\" --effort \"low\"): --effort is not supported for model \"gemini-bogus-9\"")],
            Events(ClientId.Antigravity, "agy-bad-model.jsonl"));
    }

    [Fact]
    public void Each_client_launches_with_its_verified_command_line_and_reads_the_prompt_on_stdin()
    {
        var prompt = "# Greet\n\nWrite \"hi\" to 审查.txt";

        var claude = Launch(ClientId.ClaudeCode, "claude-opus-5-5", "xhigh", prompt);
        var codex = Launch(ClientId.Codex, "gpt-6-sol", "high", prompt);
        var pi = Launch(ClientId.Pi, "deepseek/deepseek-v4-pro", "high", prompt);
        var agy = Launch(ClientId.Antigravity, "gemini-3.8-flash", "low", prompt);

        Assert.Equal(
            ["-p", "--output-format", "stream-json", "--verbose", "--model", "claude-opus-5-5", "--effort", "xhigh", "--permission-mode", "acceptEdits"],
            claude.Arguments.ToArray());
        Assert.Equal(
            ["exec", "--json", "-m", "gpt-6-sol", "-c", "model_reasoning_effort=high", "-c", "approval_policy=never", "--sandbox", "workspace-write", "--skip-git-repo-check", "-"],
            codex.Arguments.ToArray());
        Assert.Equal(["-p", "--mode", "json", "--model", "deepseek/deepseek-v4-pro", "--thinking", "high"], pi.Arguments.ToArray());
        Assert.Equal(
            ["--input-format", "stream-json", "--output-format", "stream-json", "--model", "gemini-3.8-flash", "--effort", "low", "--mode", "accept-edits", "--print="],
            agy.Arguments.ToArray());
        Assert.Equal([prompt, prompt, prompt], new[] { claude.Stdin, codex.Stdin, pi.Stdin });
        Assert.Equal("""{"event":"user","message":{"role":"user","content":"# Greet\n\nWrite \"hi\" to 审查.txt"}}""" + "\n", agy.Stdin);
    }

    [Fact]
    public void Each_client_resumes_a_session_with_the_arguments_the_probe_ran()
    {
        const string session = "01a108d7-464d-77a3-8908-a36f38ce6c14";
        LaunchArguments Resume(ClientId client, string model, string? reasoning) =>
            Clients.Get(client).Launch(new LaunchRequest(model, reasoning, "banana") { ResumeSession = session });

        var claude = Resume(ClientId.ClaudeCode, "claude-opus-5-5", "xhigh");
        var codex = Resume(ClientId.Codex, "gpt-6-sol", "high");
        var pi = Resume(ClientId.Pi, "deepseek/deepseek-v4-pro", "high");
        var agy = Resume(ClientId.Antigravity, "gemini-3.8-flash", "low");

        Assert.Equal(
            ["-p", "--output-format", "stream-json", "--verbose", "--model", "claude-opus-5-5", "--effort", "xhigh", "--permission-mode", "acceptEdits", "--resume", session],
            claude.Arguments.ToArray());
        Assert.Equal(
            [
                "exec", "resume", "--json", "-m", "gpt-6-sol", "-c", "model_reasoning_effort=high", "-c", "approval_policy=never",
                "--skip-git-repo-check", "-c", "sandbox_mode=workspace-write", session, "-",
            ],
            codex.Arguments.ToArray());
        Assert.Equal(["-p", "--mode", "json", "--model", "deepseek/deepseek-v4-pro", "--thinking", "high", "--session-id", session], pi.Arguments.ToArray());
        Assert.Equal(
            [
                "--input-format", "stream-json", "--output-format", "stream-json", "--model", "gemini-3.8-flash", "--effort", "low", "--mode", "accept-edits", "--print=",
                "--conversation", session,
            ],
            agy.Arguments.ToArray());
        Assert.Equal(["banana", "banana", "banana"], new[] { claude.Stdin, codex.Stdin, pi.Stdin });
        Assert.Equal("""{"event":"user","message":{"role":"user","content":"banana"}}""" + "\n", agy.Stdin);
    }

    [Fact]
    public void Each_client_names_the_command_that_opens_a_session_in_its_own_terminal_interface()
    {
        Assert.Equal(
            [
                "claude --resume 5a1c01fc-d492-491f-a024-4e4c1bf97949",
                "codex resume 5a1c01fc-d492-491f-a024-4e4c1bf97949",
                "pi --session 5a1c01fc-d492-491f-a024-4e4c1bf97949",
                "agy --conversation 5a1c01fc-d492-491f-a024-4e4c1bf97949",
            ],
            Clients.All.Select(client => Clients.Get(client).Terminal("5a1c01fc-d492-491f-a024-4e4c1bf97949")));
    }

    [Fact]
    public void A_model_without_reasoning_levels_launches_without_a_level_argument()
    {
        Assert.Equal(
            ["--input-format", "stream-json", "--output-format", "stream-json", "--model", "claude-opus-4-6-thinking", "--mode", "accept-edits", "--print="],
            Launch(ClientId.Antigravity, "claude-opus-4-6-thinking", null, "Go").Arguments.ToArray());
        Assert.Equal(["-p", "--mode", "json", "--model", "local/tiny"], Launch(ClientId.Pi, "local/tiny", null, "Go").Arguments.ToArray());
    }

    [Fact]
    public void The_Codex_catalog_lists_visible_models_with_their_levels_and_default()
    {
        Assert.Equal(
            [
                new ModelOption("gpt-6.1-sol", "GPT-6.1-Sol", ["low", "medium", "high", "xhigh", "max", "ultra"]) { DefaultReasoning = "low" },
                new ModelOption("gpt-6-sol", "GPT-6-Sol", ["low", "medium", "high", "xhigh", "max", "ultra"]) { DefaultReasoning = "medium" },
                new ModelOption("gpt-5.5", "GPT-5.5", ["low", "medium", "high", "xhigh"]) { DefaultReasoning = "medium" },
            ],
            Models(ClientId.Codex, new ProbeOutput(0, Fixture.Text("codex-debug-models.json"), "", false)));
    }

    [Fact]
    public void The_Pi_catalog_offers_only_the_levels_each_models_map_allows()
    {
        Assert.Equal(
            [
                new ModelOption("deepseek/deepseek-flash", "DeepSeek V4.1 Flash (deepseek)", ["low", "high", "max"]) { Provider = "deepseek", DefaultReasoning = "high" },
                new ModelOption("deepseek/deepseek-v4-pro", "DeepSeek V4 Pro (deepseek)", ["high", "max"]) { Provider = "deepseek", DefaultReasoning = "high" },
                new ModelOption("openai-codex/gpt-5.5", "GPT-5.5 (openai-codex)", ["minimal", "low", "medium", "high", "xhigh"])
                {
                    Provider = "openai-codex",
                    DefaultReasoning = "medium",
                },
                new ModelOption("openai-codex/gpt-6-astra", "GPT-6 Astra (openai-codex)", ["minimal", "low", "medium", "high", "xhigh", "max"])
                {
                    Provider = "openai-codex",
                    DefaultReasoning = "medium",
                },
                new ModelOption("openai-codex/gpt-6-luna", "GPT-6 Luna (openai-codex)", ["off", "minimal", "low", "medium", "high", "xhigh", "max"])
                {
                    Provider = "openai-codex",
                    DefaultReasoning = "medium",
                },
            ],
            Models(ClientId.Pi, new ProbeOutput(null, Fixture.Text("pi-rpc-models.jsonl"), "", false)));
    }

    [Fact]
    public void The_Antigravity_catalog_groups_effort_variants_under_the_bare_id()
    {
        Assert.Equal(
            [
                new ModelOption("gemini-3.8-flash", "Gemini 3.8 Flash", ["low", "medium", "high"]) { DefaultReasoning = "medium" },
                new ModelOption("gemini-3.7-flash", "Gemini 3.7 Flash", ["low", "medium", "high"]) { DefaultReasoning = "medium" },
                new ModelOption("gemini-3.6-flash", "Gemini 3.6 Flash", ["low", "medium", "high"]) { DefaultReasoning = "medium" },
                new ModelOption("gemini-3.1-pro", "Gemini 3.1 Pro", ["low", "high"]) { DefaultReasoning = "high" },
                new ModelOption("claude-sonnet-4-6", "Claude Sonnet 4.6 (Thinking)", []),
                new ModelOption("claude-opus-4-6-thinking", "Claude Opus 4.6 (Thinking)", []),
                new ModelOption("gpt-oss-120b", "GPT-OSS 120B", ["medium"]) { DefaultReasoning = "medium" },
            ],
            Models(ClientId.Antigravity, new ProbeOutput(0, Fixture.Text("agy-models.txt"), "", false)));
    }

    [Fact]
    public void A_catalog_that_cannot_be_read_explains_why()
    {
        Assert.Equal(
            new CatalogParse.Problem("codex debug models exited with code 1: error: the access token could not be refreshed"),
            Catalog(ClientId.Codex, new ProbeOutput(1, "", "warning: slow\nerror: the access token could not be refreshed\n", false)));
        Assert.Equal(
            new CatalogParse.Problem("codex debug models printed a model list iDevelop could not read."),
            Catalog(ClientId.Codex, new ProbeOutput(0, "{\"models\": [", "", false)));
        Assert.Equal(
            new CatalogParse.Problem("Pi did not list its models. pi --mode rpc was stopped: Error: no API key found"),
            Catalog(ClientId.Pi, new ProbeOutput(null, Fixture.Lines("pi-rpc-models.jsonl").First() + "\n", "Error: no API key found\n", false)));
        Assert.Equal(new CatalogParse.Problem("agy listed no models."), Catalog(ClientId.Antigravity, new ProbeOutput(0, "\n", "", false)));
    }

    [Fact]
    public void Sign_in_checks_read_each_clients_answer()
    {
        Assert.Null(Problem(ClientId.ClaudeCode, new ProbeOutput(0, """{"loggedIn":true}""", "", false)));
        Assert.Equal(
            "Claude Code is not signed in. Run claude in a terminal and sign in.",
            Problem(ClientId.ClaudeCode, new ProbeOutput(1, """{"loggedIn":false}""", "", false)));
        Assert.Equal("claude auth status exited with code 2: unknown command", Problem(ClientId.ClaudeCode, new ProbeOutput(2, "", "unknown command", false)));
        Assert.Null(Problem(ClientId.Codex, new ProbeOutput(0, "Logged in using ChatGPT\n", "", false)));
        Assert.Equal("Codex is not signed in. Run codex login in a terminal.", Problem(ClientId.Codex, new ProbeOutput(1, "Not logged in\n", "", false)));
    }

    [Fact]
    public void Pi_checks_the_sign_in_of_each_provider_in_its_catalog()
    {
        var catalog = Models(ClientId.Pi, new ProbeOutput(null, Fixture.Text("pi-rpc-models.jsonl"), "", false));

        var probes = Clients.Get(ClientId.Pi).Readiness([.. catalog]);

        Assert.Equal(new string?[] { "deepseek", "openai-codex" }, probes.Select(probe => probe.Provider));
        Assert.Equal(["auth", "check", "--provider", "openai-codex", "--json"], probes[1].Probe.Arguments.ToArray());
        Assert.Null(probes[0].Problem(new ProbeOutput(0, """{"status":"ready"}""", "", false)));
        Assert.Equal(
            "Pi's sign-in for openai-codex is invalid. Sign in to openai-codex in Pi again.",
            probes[1].Problem(new ProbeOutput(0, """{"status":"invalid"}""", "", false)));
    }

    private static AgentEvent[] Events(ClientId client, string fixture) =>
        [.. Fixture.Lines(fixture).SelectMany(line => Clients.Get(client).Interpret(line))];

    private static LaunchArguments Launch(ClientId client, string model, string? reasoning, string prompt) =>
        Clients.Get(client).Launch(new LaunchRequest(model, reasoning, prompt));

    private static CatalogParse Catalog(ClientId client, ProbeOutput output) =>
        Assert.IsType<CatalogSource.Probed>(Clients.Get(client).Catalog).Parse(output);

    private static ModelOption[] Models(ClientId client, ProbeOutput output) =>
        [.. Assert.IsType<CatalogParse.Models>(Catalog(client, output)).Options];

    private static string? Problem(ClientId client, ProbeOutput output) => Assert.Single(Clients.Get(client).Readiness([])).Problem(output);
}
