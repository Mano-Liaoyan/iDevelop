using System.Collections.Immutable;
using System.Text.Json;
using static IDevelop.Execution.JsonFields;

namespace IDevelop.Execution;

/// <summary>Pi through <c>pi -p --mode json</c>. On Windows <c>pi</c> is an npm <c>pi.cmd</c> shim.</summary>
internal static class Pi
{
    private static readonly ImmutableArray<string> StandardLevels = ["minimal", "low", "medium", "high"];

    private static readonly ImmutableArray<string> ExtraLevels = ["off", "xhigh", "max"];

    public static readonly ClientDefinition Definition = new()
    {
        Command = "pi",
        Catalog = new CatalogSource.Probed(
            new Probe(["--mode", "rpc", "--no-session"])
            {
                Stdin = """{"id":"idevelop-models","type":"get_available_models"}""" + "\n",
                DoneWhen = IsModelsResponse,
            },
            ParseCatalog),
        // One check per provider. Never send RPC set_model: it can change the user's default model.
        Readiness = models => [.. models.Select(model => model.Provider!).Distinct().Select(provider =>
            new ReadinessProbe(provider, new Probe(["auth", "check", "--provider", provider, "--json"]), output => SignInProblem(provider, output)))],
        Launch = Launch,
        Interpret = Interpret,
    };

    private static LaunchArguments Launch(LaunchRequest request) => new(
    [
        "-p", "--mode", "json", "--model", request.Model,
        .. request.Reasoning is { } thinking ? ["--thinking", thinking] : Array.Empty<string>(),
    ],
    request.Prompt);

    private static CatalogParse ParseCatalog(ProbeOutput output)
    {
        foreach (var line in ClientRegistry.Lines(output.Stdout).Where(IsModelsResponse))
        {
            try
            {
                using var json = JsonDocument.Parse(line);
                var response = json.RootElement;
                if (response.Bool("success") != true)
                {
                    return new CatalogParse.Problem($"Pi did not list its models: {response.String("error") ?? "it reported a failure"}.");
                }

                ImmutableArray<ModelOption> models = [.. response.Property("data")?.Items("models").Select(Model).OfType<ModelOption>() ?? []];
                return models.IsEmpty ? new CatalogParse.Problem("Pi listed no models.") : new CatalogParse.Models(models);
            }
            catch (JsonException)
            {
            }
        }

        return new CatalogParse.Problem($"Pi did not list its models. {ClientRegistry.ProbeFailure("pi --mode rpc", output)}");
    }

    private static bool IsModelsResponse(string line) =>
        line.Contains("\"type\":\"response\"", StringComparison.Ordinal) && line.Contains("\"command\":\"get_available_models\"", StringComparison.Ordinal);

    private static ModelOption? Model(JsonElement model)
    {
        if (model.String("provider") is not { } provider || model.String("id") is not { } id)
        {
            return null;
        }

        var levels = Levels(model);
        return new ModelOption($"{provider}/{id}", $"{model.String("name") ?? id} ({provider})", levels)
        {
            Provider = provider,
            DefaultReasoning = ClientRegistry.DefaultLevel(levels),
        };
    }

    /// <summary>
    /// Pi clamps an unsupported level without saying so, so only the levels its map allows are offered.
    /// A null entry removes a standard level. The extra levels exist only when the map names them with a value.
    /// </summary>
    private static ImmutableArray<string> Levels(JsonElement model)
    {
        if (model.Bool("reasoning") != true)
        {
            return [];
        }

        var map = model.Property("thinkingLevelMap");
        JsonValueKind? Entry(string level) =>
            map is { ValueKind: JsonValueKind.Object } levels && levels.TryGetProperty(level, out var value) ? value.ValueKind : null;

        return ClientRegistry.SortLevels(
        [
            .. StandardLevels.Where(level => Entry(level) != JsonValueKind.Null),
            .. ExtraLevels.Where(level => Entry(level) is { } kind && kind != JsonValueKind.Null),
        ]);
    }

    private static string? SignInProblem(string provider, ProbeOutput output)
    {
        try
        {
            using var json = JsonDocument.Parse(output.Stdout);
            if (json.RootElement.String("status") is { } status)
            {
                return status == "ready" ? null : $"Pi's sign-in for {provider} is {status}. Sign in to {provider} in Pi again.";
            }
        }
        catch (JsonException)
        {
        }

        return ClientRegistry.ProbeFailure($"pi auth check --provider {provider}", output);
    }

    private static ImmutableArray<AgentEvent> Interpret(string line)
    {
        using var json = JsonDocument.Parse(line);
        var root = json.RootElement;
        return root.String("type") switch
        {
            "session" when root.String("id") is { } id => [new AgentEvent.SessionStarted(id)],
            "message_end" when root.Property("message") is { } message && message.String("role") == "assistant" => Assistant(message),
            "tool_execution_start" when root.String("toolName") is { } tool =>
                [new AgentEvent.ToolStarted(tool, root.Property("args")?.String("path") ?? root.Property("args")?.String("command"))],
            // Pi exits 0 even when the turn fails, so this verdict is its only success signal.
            "agent_end" => [Verdict(root.Items("messages").Where(message => message.String("role") == "assistant").Select(message => (JsonElement?)message).LastOrDefault())],
            _ => [],
        };
    }

    private static ImmutableArray<AgentEvent> Assistant(JsonElement message)
    {
        var events = ImmutableArray.CreateBuilder<AgentEvent>();
        var model = message.String("provider") is { } provider && message.String("model") is { } id ? $"{provider}/{id}" : message.String("model");
        var thinking = message.String("thinkingLevel");
        if (model is not null || thinking is not null)
        {
            events.Add(new AgentEvent.Reported(model, thinking));
        }

        if (Text(message) is { } text)
        {
            events.Add(new AgentEvent.Message(text));
        }

        return events.ToImmutable();
    }

    private static AgentEvent Verdict(JsonElement? last)
    {
        if (last is not { } message)
        {
            return new AgentEvent.Failed("Pi ended without an answer.");
        }

        return message.String("stopReason") switch
        {
            "stop" => new AgentEvent.Succeeded(Text(message)),
            "error" => new AgentEvent.Failed(NonBlank(message.String("errorMessage")) ?? "Pi reported an error."),
            "aborted" => new AgentEvent.Failed("Pi stopped the turn."),
            var reason => new AgentEvent.Failed($"Pi ended with stop reason {reason ?? "none"}."),
        };
    }

    private static string? Text(JsonElement message) => NonBlank(string.Join("\n",
        message.Items("content").Where(part => part.String("type") == "text").Select(part => part.String("text")).OfType<string>()));
}
