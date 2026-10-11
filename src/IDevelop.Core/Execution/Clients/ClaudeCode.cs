using System.Collections.Immutable;
using System.Text.Json;
using IDevelop.Workflows;
using static IDevelop.Execution.JsonFields;

namespace IDevelop.Execution;

/// <summary>Claude Code through <c>claude -p --output-format stream-json</c>.</summary>
internal static class ClaudeCode
{
    private static readonly ImmutableArray<string> Efforts = ["low", "medium", "high", "xhigh", "max"];

    // Claude Code has no model-list command, so this list changes with Claude Code releases.
    private static readonly ImmutableArray<ModelOption> Models =
    [
        new("claude-fable-5-1", "Claude Fable 5.1", Efforts) { DefaultReasoning = "high" },
        new("claude-opus-5-5", "Claude Opus 5.5", Efforts) { DefaultReasoning = "high" },
        new("claude-sonnet-5-5", "Claude Sonnet 5.5", Efforts) { DefaultReasoning = "high" },
        new("claude-haiku-4-5", "Claude Haiku 4.5", Efforts) { DefaultReasoning = "high" },
    ];

    public static readonly ClientDefinition Definition = new()
    {
        Name = "Claude Code",
        WireName = "claude-code",
        Command = "claude",
        Catalog = new CatalogSource.Fixed(Models),
        Readiness = _ => [new ReadinessProbe(null, new Probe(["auth", "status"]), SignInProblem)],
        Launch = Launch,
        Protocol = request => new ClaudeProtocol(request),
        Terminal = session => $"claude --resume {session}",
        HasReadOnlyMode = true,
        // reload_skills answers with the session's skills alone, its bundled skills among them, which is what the initialize
        // allow-list chooses from. It follows initialize, as in a turn.
        Skills = new SkillSource(folder => new Probe(["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose"])
        {
            Folder = folder,
            Stdin = Control(InitializeRequest, "initialize"),
            Reply = line => IsResponse(line, InitializeRequest) && Succeeded(line) ? new ProbeReply(Control(SkillsRequest, "reload_skills")) : null,
            DoneWhen = line => IsResponse(line, SkillsRequest) || IsResponse(line, InitializeRequest) && !Succeeded(line),
        }, ParseSkills),
    };

    private const string InitializeRequest = "idevelop-init";

    private const string SkillsRequest = "idevelop-skills";

    private static LaunchArguments Launch(LaunchRequest request) => new(
    [
        "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages", "--model", request.Model,
        .. request.Reasoning is { } effort ? ["--effort", effort] : Array.Empty<string>(),
        "--permission-mode", ((NativePolicy.Claude)request.PolicyFor(ClientId.ClaudeCode).Native).Mode,
        .. request.ResumeSession is { } session ? ["--resume", session] : Array.Empty<string>(),
        .. request.PolicyFor(ClientId.ClaudeCode).PermissionPrompts == PermissionPrompts.Host ? ["--permission-prompt-tool", "stdio"] : new[] { "--permission-prompts", "none" },
    ],
    "");

    private static string Control(string id, string subtype) =>
        JsonSerializer.Serialize(new { type = "control_request", request_id = id, request = new { subtype } }) + "\n";

    private static bool IsResponse(string line, string id) =>
        line.Contains("\"control_response\"", StringComparison.Ordinal) && line.Contains($"\"{id}\"", StringComparison.Ordinal);

    private static bool Succeeded(string line) => Response(line)?.String("subtype") == "success";

    /// <summary>A control response's own "response" object, or null for a line that is not one.</summary>
    private static JsonElement? Response(string line)
    {
        try
        {
            using var json = JsonDocument.Parse(line);
            return json.RootElement.Property("response")?.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The skills that reload_skills lists. A Claude Code without it lists only its initialize commands, which mark its
    /// built-in commands and bundled skills alike, so that listing leaves them all out and says so.
    /// </summary>
    private static SkillListing ParseSkills(ProbeOutput output)
    {
        var lines = TextLines.Lines(output.Stdout).ToArray();
        var initialize = lines.Where(line => IsResponse(line, InitializeRequest)).Select(Response).FirstOrDefault(response => response is not null);
        if (initialize is { } failed && failed.String("subtype") != "success")
        {
            return new SkillListing.Unavailable($"Claude Code did not list its skills: {failed.String("error") ?? "it reported a failure"}.");
        }

        if (lines.Where(line => IsResponse(line, SkillsRequest)).Select(Response).FirstOrDefault(response => response is not null) is { } reloaded)
        {
            if (reloaded.String("subtype") == "success")
            {
                return new SkillListing.Listed(Options(reloaded.Property("response")?.Items("skills")));
            }

            if (initialize is { } initialized)
            {
                return new SkillListing.Listed(Options(initialized.Property("response")?.Items("commands").Where(command => command.Bool("builtin") != true)))
                {
                    LeavesOutBuiltIns = true,
                };
            }
        }

        return new SkillListing.Unavailable($"Claude Code did not list its skills. {Probes.Failure("claude -p", output)}");
    }

    private static ImmutableArray<SkillOption> Options(IEnumerable<JsonElement>? entries) => SkillChoices.Distinct(entries?
        .Where(entry => entry.String("name") is not null)
        .Select(entry => new SkillOption(entry.String("name")!, entry.String("description") ?? "")) ?? []);

    private static ReadinessProblem? SignInProblem(ProbeOutput output)
    {
        try
        {
            using var json = JsonDocument.Parse(output.Stdout);
            switch (json.RootElement.Bool("loggedIn"))
            {
                case true:
                    return null;
                case false:
                    return new ReadinessProblem("Claude Code is not signed in. Run claude in a terminal and sign in.", SignedOut: true);
            }
        }
        catch (JsonException)
        {
        }

        return new ReadinessProblem(Probes.Failure("claude auth status", output), SignedOut: false);
    }

    internal static ImmutableArray<AgentEvent> Events(JsonElement root)
    {
        return root.String("type") switch
        {
            "system" when root.String("subtype") == "init" => Init(root),
            "assistant" when root.Property("message") is { } message => Assistant(message),
            "result" => Result(root),
            _ => [],
        };
    }

    private static ImmutableArray<AgentEvent> Init(JsonElement root)
    {
        var events = ImmutableArray.CreateBuilder<AgentEvent>();
        if (root.String("session_id") is { } session)
        {
            events.Add(new AgentEvent.SessionStarted(session));
        }

        if (root.String("model") is { } model)
        {
            events.Add(new AgentEvent.Reported(model, null));
        }

        return events.ToImmutable();
    }

    private static ImmutableArray<AgentEvent> Assistant(JsonElement message)
    {
        var events = ImmutableArray.CreateBuilder<AgentEvent>();
        // Claude Code writes its own error notes as "<synthetic>" messages, which no model served.
        if (message.String("model") is { } model && model != "<synthetic>")
        {
            events.Add(new AgentEvent.Reported(model, null));
        }

        foreach (var part in message.Items("content"))
        {
            switch (part.String("type"))
            {
                case "text" when NonBlank(part.String("text")) is { } text:
                    events.Add(new AgentEvent.Message(text));
                    break;
                case "tool_use" when part.String("name") is { } name:
                    var input = part.Property("input");
                    events.Add(new AgentEvent.ToolStarted(name, input?.String("file_path") ?? input?.String("command") ?? input?.String("pattern")));
                    break;
            }
        }

        return events.ToImmutable();
    }

    private static ImmutableArray<AgentEvent> Result(JsonElement root)
    {
        var events = ImmutableArray.CreateBuilder<AgentEvent>();
        foreach (var denial in root.Items("permission_denials"))
        {
            if (denial.String("tool_name") is { } tool)
            {
                events.Add(new AgentEvent.Notice($"Claude Code denied {tool}."));
            }
        }

        var result = NonBlank(root.String("result"));
        events.Add(root.Bool("is_error") == true
            ? new AgentEvent.Failed(result ?? $"Claude Code ended with {root.String("subtype") ?? "an error"}.")
            : new AgentEvent.Succeeded(result));
        return events.ToImmutable();
    }
}
