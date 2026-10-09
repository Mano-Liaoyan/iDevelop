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
        // The initialize response lists every command the session would load, and marks Claude Code's own. Those are its
        // built-in commands and its bundled skills together, so the listing leaves them all out.
        Skills = new SkillSource(folder => new Probe(["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose"])
        {
            Folder = folder,
            Stdin = JsonSerializer.Serialize(new { type = "control_request", request_id = SkillsRequest, request = new { subtype = "initialize" } }) + "\n",
            DoneWhen = IsSkillsResponse,
        }, ParseSkills) { ListsBuiltIns = false },
    };

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

    private static bool IsSkillsResponse(string line) =>
        line.Contains("\"control_response\"", StringComparison.Ordinal) && line.Contains($"\"{SkillsRequest}\"", StringComparison.Ordinal);

    private static SkillListing ParseSkills(ProbeOutput output)
    {
        foreach (var line in TextLines.Lines(output.Stdout).Where(IsSkillsResponse))
        {
            try
            {
                using var json = JsonDocument.Parse(line);
                if (json.RootElement.Property("response") is not { } response || response.String("subtype") != "success")
                {
                    return new SkillListing.Unavailable(
                        $"Claude Code did not list its skills: {json.RootElement.Property("response")?.String("error") ?? "it reported a failure"}.");
                }

                return new SkillListing.Listed(SkillChoices.Distinct(response.Property("response")?.Items("commands")
                    .Where(command => command.Bool("builtin") != true && command.String("name") is not null)
                    .Select(command => new SkillOption(command.String("name")!, command.String("description") ?? "")) ?? []));
            }
            catch (JsonException)
            {
            }
        }

        return new SkillListing.Unavailable($"Claude Code did not list its skills. {Probes.Failure("claude -p", output)}");
    }

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
