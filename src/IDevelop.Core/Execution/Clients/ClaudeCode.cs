using System.Collections.Immutable;
using System.Text.Json;
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
        Command = "claude",
        Catalog = new CatalogSource.Fixed(Models),
        Readiness = _ => [new ReadinessProbe(null, new Probe(["auth", "status"]), SignInProblem)],
        Launch = Launch,
        Interpret = Interpret,
    };

    private static LaunchArguments Launch(LaunchRequest request) => new(
    [
        "-p", "--output-format", "stream-json", "--verbose", "--model", request.Model,
        .. request.Reasoning is { } effort ? ["--effort", effort] : Array.Empty<string>(),
        "--permission-mode", "acceptEdits",
    ],
    request.Prompt);

    private static string? SignInProblem(ProbeOutput output)
    {
        try
        {
            using var json = JsonDocument.Parse(output.Stdout);
            switch (json.RootElement.Bool("loggedIn"))
            {
                case true:
                    return null;
                case false:
                    return "Claude Code is not signed in. Run claude in a terminal and sign in.";
            }
        }
        catch (JsonException)
        {
        }

        return ClientRegistry.ProbeFailure("claude auth status", output);
    }

    private static ImmutableArray<AgentEvent> Interpret(string line)
    {
        using var json = JsonDocument.Parse(line);
        var root = json.RootElement;
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
