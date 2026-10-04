using System.Collections.Immutable;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using static IDevelop.Execution.JsonFields;

namespace IDevelop.Execution;

/// <summary>Antigravity CLI through <c>agy</c> with stream-json input and output.</summary>
internal static class Antigravity
{
    public static readonly ClientDefinition Definition = new()
    {
        Command = "agy",
        Catalog = new CatalogSource.Probed(new Probe(["models"]), ParseCatalog),
        // agy has no sign-in check, so a model list that loads counts as ready.
        Readiness = _ => [],
        Launch = Launch,
        Interpret = Interpret,
    };

    private static LaunchArguments Launch(LaunchRequest request) => new(
    [
        "--input-format", "stream-json", "--output-format", "stream-json", "--model", request.Model,
        .. request.Reasoning is { } effort ? ["--effort", effort] : Array.Empty<string>(),
        "--mode", "accept-edits", "--print=",
    ],
    UserLine(request.Prompt));

    /// <summary>The one stdin line agy reads before it starts: <c>{"event":"user","message":{"role":"user","content":...}}</c>.</summary>
    private static string UserLine(string prompt)
    {
        using var buffer = new MemoryStream();
        // The line goes to agy's stdin, never into HTML, so readable escaping is safe.
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("event", "user");
            writer.WriteStartObject("message");
            writer.WriteString("role", "user");
            writer.WriteString("content", prompt);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    /// <summary>
    /// <c>agy models</c> prints "id&lt;TAB&gt;Display name" lines, one per model and effort, such as
    /// "gemini-3.8-flash-high	Gemini 3.8 Flash (High)". A trailing "-level" splits off only when the name ends with the
    /// same "(Level)". The bare id with <c>--effort</c> selects the variant, and a model without levels takes no effort.
    /// </summary>
    private static CatalogParse ParseCatalog(ProbeOutput output)
    {
        if (output.ExitCode != 0)
        {
            return new CatalogParse.Problem(ClientRegistry.ProbeFailure("agy models", output));
        }

        var models = new List<(string Id, string Name, List<string> Levels)>();
        foreach (var line in TextLines.Lines(output.Stdout))
        {
            var fields = line.Split('\t', 2);
            var (id, name) = (fields[0].Trim(), fields.Length > 1 ? fields[1].Trim() : fields[0].Trim());
            var dash = id.LastIndexOf('-');
            var level = dash > 0 ? id[(dash + 1)..] : "";
            var suffix = $" ({level})";
            if (ClientRegistry.IsLevel(level) && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                (id, name) = (id[..dash], name[..^suffix.Length]);
            }
            else
            {
                level = "";
            }

            var index = models.FindIndex(model => model.Id == id);
            if (index < 0)
            {
                models.Add((id, name, []));
                index = models.Count - 1;
            }

            if (level.Length > 0)
            {
                models[index].Levels.Add(level);
            }
        }

        if (models.Count == 0)
        {
            return new CatalogParse.Problem("agy listed no models.");
        }

        return new CatalogParse.Models([.. models.Select(model =>
        {
            var levels = ClientRegistry.SortLevels(model.Levels);
            return new ModelOption(model.Id, model.Name, levels) { DefaultReasoning = ClientRegistry.DefaultLevel(levels) };
        })]);
    }

    private static ImmutableArray<AgentEvent> Interpret(string line)
    {
        using var json = JsonDocument.Parse(line);
        var root = json.RootElement;
        var step = root.Property("step_update");
        return root.String("event") switch
        {
            "init" => Init(root),
            "step_update" when step?.String("step_type") == "tool" && step?.String("state") == "ACTIVE" && step?.String("tool_name") is { } tool =>
                [new AgentEvent.ToolStarted(tool, step?.Property("tool_info")?.Property("parameters")?.String("TargetFile"))],
            // Response deltas are skipped: the result event carries the whole text.
            "result" when root.Property("result") is { } result => [Verdict(result)],
            _ => [],
        };
    }

    private static ImmutableArray<AgentEvent> Init(JsonElement root)
    {
        var events = ImmutableArray.CreateBuilder<AgentEvent>();
        if (NonBlank(root.String("conversation_id")) is { } conversation)
        {
            events.Add(new AgentEvent.SessionStarted(conversation));
        }

        if (root.Property("init")?.String("model") is { } model)
        {
            events.Add(new AgentEvent.Reported(model, null));
        }

        return events.ToImmutable();
    }

    private static AgentEvent Verdict(JsonElement result) => result.String("status") == "SUCCESS"
        ? new AgentEvent.Succeeded(NonBlank(result.String("response"))?.TrimEnd())
        : new AgentEvent.Failed(NonBlank(result.String("error")) ?? "Antigravity CLI reported an error.");
}
