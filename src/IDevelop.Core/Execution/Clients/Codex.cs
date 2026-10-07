using System.Collections.Immutable;
using System.Text.Json;
using IDevelop.Workflows;
using static IDevelop.Execution.JsonFields;

namespace IDevelop.Execution;

/// <summary>Codex through <c>codex app-server</c>.</summary>
internal static class Codex
{
    public static readonly ClientDefinition Definition = new()
    {
        Name = "Codex",
        WireName = "codex",
        Command = "codex",
        Catalog = new CatalogSource.Probed(new Probe(["debug", "models"]), ParseCatalog),
        Readiness = _ => [new ReadinessProbe(null, new Probe(["login", "status"]), SignInProblem)],
        Launch = Launch,
        Protocol = request => new CodexProtocol(request),
        Terminal = session => $"codex resume {session}",
        HasReadOnlyMode = true,
    };

    private static LaunchArguments Launch(LaunchRequest request) => new(
        ["app-server", "-c", $"approval_policy={((NativePolicy.Codex)request.PolicyFor(ClientId.Codex).Native).ApprovalPolicy}", "-c", "features.default_mode_request_user_input=false"], "");

    private static CatalogParse ParseCatalog(ProbeOutput output)
    {
        if (output.ExitCode != 0)
        {
            return new CatalogParse.Problem(Probes.Failure("codex debug models", output));
        }

        ImmutableArray<ModelOption> models;
        try
        {
            using var json = JsonDocument.Parse(output.Stdout);
            models = [.. json.RootElement.Items("models").Where(model => model.String("visibility") == "list").Select(Model).OfType<ModelOption>()];
        }
        catch (JsonException)
        {
            return new CatalogParse.Problem("codex debug models printed a model list iDevelop could not read.");
        }

        return models.IsEmpty ? new CatalogParse.Problem("Codex listed no models.") : new CatalogParse.Models(models);
    }

    private static ModelOption? Model(JsonElement model)
    {
        if (model.String("slug") is not { } slug)
        {
            return null;
        }

        var levels = ReasoningLevels.Sort(model.Items("supported_reasoning_levels").Select(level => level.String("effort")).OfType<string>());
        return new ModelOption(slug, model.String("display_name") ?? slug, levels)
        {
            DefaultReasoning = model.String("default_reasoning_level") is { } level && levels.Contains(level) ? level : ReasoningLevels.Default(levels),
        };
    }

    private static ReadinessProblem? SignInProblem(ProbeOutput output) =>
        output.ExitCode == 0 ? null : new ReadinessProblem("Codex is not signed in. Run codex login in a terminal.", SignedOut: true);

    /// <summary>Codex passes an API error through as JSON text. Its inner message is the readable part.</summary>
    internal static string InnerMessage(string message)
    {
        if (!message.StartsWith('{'))
        {
            return message;
        }

        try
        {
            using var json = JsonDocument.Parse(message);
            return json.RootElement.Property("error")?.String("message") ?? message;
        }
        catch (JsonException)
        {
            return message;
        }
    }
}
