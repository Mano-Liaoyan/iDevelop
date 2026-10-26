using System.Collections.Immutable;
using System.Text.Json;
using IDevelop.Workflows;

namespace IDevelop.Clients;

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
        Skills = new SkillSource(folder =>
        {
            var conversation = new SkillsConversation(folder);
            return new Probe(["app-server"])
            {
                Folder = folder,
                Stdin = JsonSerializer.Serialize(new
                {
                    id = InitializeRequest, method = "initialize",
                    @params = new { clientInfo = new { name = "idevelop", version = "1.0.0" }, capabilities = new { experimentalApi = false } },
                }) + "\n",
                Reply = conversation.Reply,
                DoneWhen = conversation.Done,
            };
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

    /// <summary>The answer to any of a listing's skills/list requests, whose ids count up from <c>idevelop-skills-1</c>.</summary>
    private static bool IsSkillsResponse(string line) => line.Contains($"\"id\":\"{SkillsRequest}-", StringComparison.Ordinal);

    /// <summary>
    /// A skill listing's requests. Codex 0.160.1 lists its remote plugins' skills only once it has loaded those plugins in the
    /// background, about a second after the app-server starts, and no notification marks it (probe, 2026-10-11). So the
    /// listing asks again every <see cref="Poll"/> until at least <see cref="Settle"/> has passed since the connection
    /// initialized and two answers in a row agree, or until <see cref="Bound"/>, and keeps the last answer. It runs no turn.
    /// </summary>
    private sealed class SkillsConversation(string folder)
    {
        private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(300);
        private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(1500);
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(8);

        private readonly System.Diagnostics.Stopwatch _clock = new();
        private int _requests;
        private string? _previous;
        private bool _settled;

        public ProbeReply? Reply(string line)
        {
            if (IsResponse(line, InitializeRequest))
            {
                _clock.Start();
                return """{"method":"initialized"}""" + "\n" + List();
            }

            if (_settled || !IsSkillsResponse(line))
            {
                return null;
            }

            var names = Names(line);
            _settled = names is null || _clock.Elapsed >= Bound || _clock.Elapsed >= Settle && names == _previous;
            _previous = names;
            return _settled ? null : new ProbeReply(List()) { Delay = Poll };
        }

        public bool Done(string line) => _settled && IsSkillsResponse(line);

        private string List() =>
            JsonSerializer.Serialize(new { id = $"{SkillsRequest}-{++_requests}", method = "skills/list", @params = new { cwds = new[] { folder } } }) + "\n";

        /// <summary>Every skill name the answer lists, in order, or null for an answer that is not a list.</summary>
        private static string? Names(string line)
        {
            try
            {
                using var json = JsonDocument.Parse(line);
                return json.RootElement.Property("result") is { } result
                    ? string.Join("\n", result.Items("data").SelectMany(entry => entry.Items("skills")).Select(skill => skill.String("name")).Order(StringComparer.Ordinal))
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>A skill the person's configuration turned off is not loaded, so the listing leaves it out.</summary>
    private static SkillListing ParseSkills(ProbeOutput output)
    {
        // The last answer is the one the listing settled on.
        foreach (var line in TextLines.Lines(output.Stdout).Where(IsSkillsResponse).Reverse())
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
