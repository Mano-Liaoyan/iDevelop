using System.Collections.Immutable;
using System.Text.Json;
using static IDevelop.Execution.JsonFields;

namespace IDevelop.Execution;

/// <summary>Codex through <c>codex exec --json</c>.</summary>
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
        Interpret = Interpret,
        Terminal = session => $"codex resume {session}",
        HasReadOnlyMode = true,
    };

    // The level is unquoted, so the argument passes the batch-shim rule for an npm codex.cmd.
    // Codex reads the value as TOML and falls back to the plain string.
    // A user's approval_policy can let an automatic reviewer approve a command outside the sandbox, so approvals are off.
    // exec resume takes no --sandbox, so a resumed turn sets the same sandbox through its config key.
    private static LaunchArguments Launch(LaunchRequest request)
    {
        string[] common =
        [
            "--json", "-m", request.Model,
            .. request.Reasoning is { } effort ? ["-c", $"model_reasoning_effort={effort}"] : Array.Empty<string>(),
            "-c", "approval_policy=never",
        ];
        var sandbox = request.ReadOnly ? "read-only" : "workspace-write";
        return new(
            request.ResumeSession is { } session
                ? ["exec", "resume", .. common, "--skip-git-repo-check", "-c", $"sandbox_mode={sandbox}", session, "-"]
                : ["exec", .. common, "--sandbox", sandbox, "--skip-git-repo-check", "-"],
            request.Prompt);
    }

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

    private static string? SignInProblem(ProbeOutput output) =>
        output.ExitCode == 0 ? null : "Codex is not signed in. Run codex login in a terminal.";

    private static ImmutableArray<AgentEvent> Interpret(string line)
    {
        using var json = JsonDocument.Parse(line);
        var root = json.RootElement;
        var item = root.Property("item");
        return (root.String("type"), item?.String("type")) switch
        {
            ("thread.started", _) when root.String("thread_id") is { } thread => [new AgentEvent.SessionStarted(thread)],
            ("item.started", "command_execution") when item?.String("command") is { } command => [new AgentEvent.ToolStarted("command", command)],
            ("item.completed", "agent_message") when NonBlank(item?.String("text")) is { } text => [new AgentEvent.Message(text)],
            ("item.completed", "error") when item?.String("message") is { } message => [new AgentEvent.Notice(message)],
            // A top-level error can be one Codex recovers from, so only the turn decides the outcome.
            ("error", _) when root.String("message") is { } message => [new AgentEvent.Notice(InnerMessage(message))],
            ("turn.completed", _) => [new AgentEvent.Succeeded(null)],
            ("turn.failed", _) => [new AgentEvent.Failed(root.Property("error")?.String("message") is { } message ? InnerMessage(message) : "Codex reported a failed turn.")],
            _ => [],
        };
    }

    /// <summary>Codex passes an API error through as JSON text. Its inner message is the readable part.</summary>
    private static string InnerMessage(string message)
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
