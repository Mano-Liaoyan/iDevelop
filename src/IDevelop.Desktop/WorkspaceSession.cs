using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using IDevelop.Projects;
using IDevelop.Workflows;

namespace IDevelop.Desktop;

/// <summary>A workflow of an open project, by the project's folder and the workflow's id.</summary>
internal readonly record struct WorkflowRef(string Folder, WorkflowId Workflow)
{
    public bool Is(string folder, WorkflowId workflow) => ProjectFolders.Comparer.Equals(Folder, folder) && Workflow == workflow;
}

/// <summary>
/// The projects a window had open, which it opens again at the next start, in the per-user session file beside the theme
/// preference: <c>{"projects":[folder...],"selected":{"folder":...,"workflow":...},"expanded":[{"folder":...,"workflow":...}]}</c>.
/// </summary>
internal sealed record WorkspaceSession(ImmutableArray<string> Projects, WorkflowRef? Selected, ImmutableArray<WorkflowRef> Expanded)
{
    public static readonly WorkspaceSession Empty = new([], null, []);

    /// <summary>A missing or unreadable file, or one that is not this shape, reads as no session. An entry that is not
    /// this shape is skipped.</summary>
    public static WorkspaceSession Read(string file)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(file));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return Empty;
        }

        if (root is not JsonObject session)
        {
            return Empty;
        }

        return new(
            [.. Items(session["projects"]).Select(Text).OfType<string>()],
            Ref(session["selected"]),
            [.. Items(session["expanded"]).Select(Ref).OfType<WorkflowRef>()]);
    }

    public void Write(string file)
    {
        var session = new JsonObject
        {
            ["projects"] = new JsonArray([.. Projects.Select(folder => JsonValue.Create(folder))]),
            ["selected"] = Selected is { } selected ? Node(selected) : null,
            ["expanded"] = new JsonArray([.. Expanded.Select(Node)]),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        AtomicFile.Replace(file, JsonSerializer.SerializeToUtf8Bytes(session));
    }

    private static IEnumerable<JsonNode?> Items(JsonNode? node) => node is JsonArray array ? array : [];

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;

    private static WorkflowRef? Ref(JsonNode? node) =>
        node is JsonObject entry && Text(entry["folder"]) is { } folder && Text(entry["workflow"]) is { } id && Guid.TryParse(id, out var guid)
            ? new WorkflowRef(folder, new WorkflowId(guid))
            : null;

    private static JsonObject Node(WorkflowRef workflow) => new()
    {
        ["folder"] = workflow.Folder,
        ["workflow"] = workflow.Workflow.Value.ToString(),
    };
}
