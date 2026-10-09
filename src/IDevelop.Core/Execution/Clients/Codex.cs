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
        // skills/list lists the skills of every scope, the bundled system skills too, after the connection initializes.
        Skills = new SkillSource(folder => new Probe(["app-server"])
        {
            Folder = folder,
            Stdin = JsonSerializer.Serialize(new
            {
                id = InitializeRequest, method = "initialize",
                @params = new { clientInfo = new { name = "idevelop", version = "1.0.0" }, capabilities = new { experimentalApi = false } },
            }) + "\n",
            Reply = line => IsResponse(line, InitializeRequest)
                ? """{"method":"initialized"}""" + "\n" + JsonSerializer.Serialize(new { id = SkillsRequest, method = "skills/list", @params = new { cwds = new[] { folder } } }) + "\n"
                : null,
            DoneWhen = line => IsResponse(line, SkillsRequest),
        }, ParseSkills),
    };

    private const string InitializeRequest = "idevelop-init";

    private const string SkillsRequest = "idevelop-skills";

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

    private static bool IsResponse(string line, string id) => line.Contains($"\"id\":\"{id}\"", StringComparison.Ordinal);

    /// <summary>A skill the person's configuration turned off is not loaded, so the listing leaves it out.</summary>
    private static SkillListing ParseSkills(ProbeOutput output)
    {
        foreach (var line in TextLines.Lines(output.Stdout).Where(line => IsResponse(line, SkillsRequest)))
        {
            try
            {
                using var json = JsonDocument.Parse(line);
                if (json.RootElement.Property("error") is { } error)
                {
                    return new SkillListing.Unavailable($"Codex did not list its skills: {error.String("message") ?? "it reported an error"}.");
                }

                return new SkillListing.Listed(SkillChoices.Distinct(json.RootElement.Property("result")?.Items("data")
                    .SelectMany(entry => entry.Items("skills"))
                    .Where(skill => skill.Bool("enabled") != false && skill.String("name") is not null)
                    .Select(skill => new SkillOption(skill.String("name")!, skill.String("description") ?? "")) ?? []));
            }
            catch (JsonException)
            {
            }
        }

        return new SkillListing.Unavailable($"Codex did not list its skills. {Probes.Failure("codex app-server", output)}");
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
