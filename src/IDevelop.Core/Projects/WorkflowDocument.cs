using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using IDevelop.Workflows;

namespace IDevelop.Projects;

/// <summary>
/// The open workflow of a project folder. A project keeps each workflow in
/// <c>.idp/workflows/&lt;workflow id&gt;.json</c>. This version opens one workflow per project.
/// </summary>
public sealed class WorkflowDocument
{
    private const string DataFolderName = ".idp";

    private Workflow _saved;

    private WorkflowDocument(string projectFolder, string filePath, Workflow workflow)
    {
        ProjectFolder = projectFolder;
        FilePath = filePath;
        Current = workflow;
        _saved = workflow;
    }

    public string ProjectFolder { get; }

    public string FilePath { get; }

    public Workflow Current { get; private set; }

    public bool HasUnsavedChanges => !ReferenceEquals(Current, _saved);

    /// <summary>Raised after <see cref="Current"/> or the saved state changes.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Opens any existing folder. A folder without a workflow file opens as an empty workflow
    /// and stays untouched until the first <see cref="Save"/>.
    /// </summary>
    /// <exception cref="ProjectException">The folder is missing, holds several workflows, or its workflow file is invalid.</exception>
    public static WorkflowDocument Open(string folder)
    {
        var projectFolder = Path.GetFullPath(folder);
        if (!Directory.Exists(projectFolder))
        {
            throw new ProjectException($"The folder {projectFolder} does not exist.");
        }

        var workflowsFolder = Path.Combine(projectFolder, DataFolderName, "workflows");
        string[] files = Directory.Exists(workflowsFolder)
            ? [.. Directory.EnumerateFiles(workflowsFolder, "*.json").Order(StringComparer.Ordinal)]
            : [];
        switch (files)
        {
            case []:
                var empty = Workflow.Empty(WorkflowId.New());
                return new WorkflowDocument(projectFolder, Path.Combine(workflowsFolder, $"{empty.Id}.json"), empty);
            case [var file]:
                return new WorkflowDocument(projectFolder, file, WorkflowFile.Parse(File.ReadAllBytes(file), file));
            default:
                throw new ProjectException(
                    $"{workflowsFolder} holds {files.Length} workflow files. This version of iDevelop opens one workflow per project.");
        }
    }

    /// <summary>An edit that is rejected or has no effect leaves the document unchanged and raises nothing.</summary>
    public EditResult Apply(WorkflowEdit edit)
    {
        var result = Current.Apply(edit);
        if (result is EditResult.Applied applied && !ReferenceEquals(applied.Workflow, Current))
        {
            Current = applied.Workflow;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return result;
    }

    /// <summary>
    /// Writes <see cref="Current"/> atomically. On an I/O error the previous file stays intact,
    /// <see cref="HasUnsavedChanges"/> stays true, and the exception propagates.
    /// </summary>
    /// <exception cref="ProjectException">Another workflow file sits beside this one, so saving would leave a project that no longer opens.</exception>
    public void Save()
    {
        var snapshot = Current;
        var workflowsFolder = Path.GetDirectoryName(FilePath)!;
        var other = Directory.Exists(workflowsFolder)
            ? Directory.EnumerateFiles(workflowsFolder, "*.json").Order(StringComparer.Ordinal).FirstOrDefault(file => file != FilePath)
            : null;
        if (other is not null)
        {
            throw new ProjectException($"Not saved. {other} is another workflow file, and this version of iDevelop keeps one workflow per project.");
        }

        Directory.CreateDirectory(workflowsFolder);
        var gitignore = Path.Combine(ProjectFolder, DataFolderName, ".gitignore");
        if (!File.Exists(gitignore))
        {
            File.WriteAllText(gitignore, "*.tmp\n");
        }

        AtomicFile.Replace(FilePath, WorkflowFile.Serialize(snapshot));
        _saved = snapshot;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>The message is for the user.</summary>
public sealed class ProjectException(string message, Exception? inner = null) : Exception(message, inner);

internal static class WorkflowFile
{
    public const string FormatTag = "idevelop.workflow/1";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        IndentSize = 2,
        NewLine = "\n",
        // Keeps Chinese and other non-ASCII text readable. The file is never embedded in HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        AllowDuplicateProperties = false,
    };

    private static readonly JsonSerializerOptions HeaderOptions = new(JsonSerializerDefaults.Web);

    public static byte[] Serialize(Workflow workflow)
    {
        var file = new FileDto
        {
            Format = FormatTag,
            Id = workflow.Id.Value,
            Tasks = [.. workflow.Tasks.Values.Select(task => new TaskDto
            {
                Id = task.Id.Value,
                Title = task.Title,
                Instructions = Lines(task.Instructions),
                AcceptanceCriteria = Lines(task.AcceptanceCriteria),
            })],
            Connections = [.. workflow.Connections.Select(connection => new ConnectionDto
            {
                From = connection.Key.From.Value,
                To = connection.Key.To.Value,
                Kind = KindName(connection.Value),
            })],
            Layout = workflow.Positions.ToDictionary(
                position => position.Key.ToString(),
                PointDto? (position) => new PointDto { X = position.Value.X, Y = position.Value.Y }),
        };
        return [.. JsonSerializer.SerializeToUtf8Bytes(file, Options), (byte)'\n'];
    }

    public static Workflow Parse(ReadOnlySpan<byte> utf8, string path)
    {
        // Some editors save UTF-8 with a byte order mark, which System.Text.Json rejects.
        if (utf8.StartsWith("\uFEFF"u8))
        {
            utf8 = utf8["\uFEFF"u8.Length..];
        }

        FileDto file;
        try
        {
            var format = JsonSerializer.Deserialize<HeaderDto>(utf8, HeaderOptions)?.Format;
            if (format != FormatTag)
            {
                throw new ProjectException($"{path} has format \"{format}\". This version of iDevelop reads {FormatTag}.");
            }

            file = JsonSerializer.Deserialize<FileDto>(utf8, Options)!;
        }
        catch (JsonException e)
        {
            throw new ProjectException($"{path} is not a valid workflow file. {e.Message}", e);
        }

        var workflow = Workflow.Empty(new WorkflowId(file.Id));
        foreach (var (index, task) in file.Tasks.Index())
        {
            if (task is null)
            {
                throw new ProjectException($"{path}: tasks[{index}] is null.");
            }

            var id = new TaskId(task.Id);
            if (!file.Layout.TryGetValue(id.ToString(), out var point))
            {
                throw new ProjectException($"{path}: task {id} has no position in the layout.");
            }

            if (point is null)
            {
                throw new ProjectException($"{path}: task {id} has a null position in the layout.");
            }

            var definition = new TaskDefinition(id)
            {
                Title = task.Title,
                Instructions = Text(task.Instructions, path, id, "instructions"),
                AcceptanceCriteria = Text(task.AcceptanceCriteria, path, id, "acceptanceCriteria"),
            };
            workflow = Replay(workflow, new WorkflowEdit.CreateTask(definition, new CanvasPoint(point.X, point.Y)), path, $"task {id}");
        }

        foreach (var (index, connection) in file.Connections.Index())
        {
            if (connection is null)
            {
                throw new ProjectException($"{path}: connections[{index}] is null.");
            }

            var key = new ConnectionKey(new TaskId(connection.From), new TaskId(connection.To));
            var entry = $"connection {key.From} -> {key.To}";
            var kind = ParseKind(connection.Kind) ?? throw new ProjectException($"{path}: {entry} has unknown kind \"{connection.Kind}\".");
            workflow = Replay(workflow, new WorkflowEdit.Connect(key, kind), path, entry);
        }

        var stray = file.Layout.Keys.FirstOrDefault(key => !Guid.TryParse(key, out var id) || !workflow.Tasks.ContainsKey(new TaskId(id)));
        if (stray is not null)
        {
            throw new ProjectException($"{path}: the layout has a position for {stray}, which is not a task.");
        }

        return workflow;
    }

    private static Workflow Replay(Workflow workflow, WorkflowEdit edit, string path, string entry) => workflow.Apply(edit) switch
    {
        EditResult.Applied applied => applied.Workflow,
        EditResult.Rejected rejected => throw new ProjectException($"{path}: {entry} {Problem(rejected.Reason, workflow)}."),
        _ => throw new UnreachableException(),
    };

    private static string Problem(EditRejection reason, Workflow workflow) => reason switch
    {
        EditRejection.TaskAlreadyExists => "repeats an earlier task",
        EditRejection.SelfConnection => "connects a task to itself",
        EditRejection.UnknownTask unknown => $"names {unknown.Task}, which is not a task",
        EditRejection.DuplicateConnection => "repeats an earlier connection between the same tasks",
        EditRejection.OrderingCycle cycle =>
            $"would close a cycle: {string.Join(" -> ", cycle.Path.Select(id => workflow.Tasks[id].Title))}",
        _ => reason.ToString(),
    };

    private static string[] Lines(string text) => text.Length == 0 ? [] : text.Split('\n');

    private static string Text(string?[] lines, string path, TaskId task, string property) => lines.Contains(null)
        ? throw new ProjectException($"{path}: task {task} has a null line in {property}.")
        : string.Join('\n', lines);

    private static string KindName(ConnectionKind kind) => kind switch
    {
        ConnectionKind.Dependency => "dependency",
        ConnectionKind.Context => "context",
        ConnectionKind.Review => "review",
    };

    private static ConnectionKind? ParseKind(string name) => name switch
    {
        "dependency" => ConnectionKind.Dependency,
        "context" => ConnectionKind.Context,
        "review" => ConnectionKind.Review,
        _ => null,
    };

    private sealed class HeaderDto
    {
        public string? Format { get; init; }
    }

    // RespectNullableAnnotations does not reach collection elements or dictionary values, so
    // those are nullable here and Parse reports each null with its entry.
    private sealed class FileDto
    {
        public required string Format { get; init; }
        public required Guid Id { get; init; }
        public required List<TaskDto?> Tasks { get; init; }
        public required List<ConnectionDto?> Connections { get; init; }
        public required Dictionary<string, PointDto?> Layout { get; init; }
    }

    private sealed class TaskDto
    {
        public required Guid Id { get; init; }
        public required string Title { get; init; }
        public required string?[] Instructions { get; init; }
        public required string?[] AcceptanceCriteria { get; init; }
    }

    private sealed class ConnectionDto
    {
        public required Guid From { get; init; }
        public required Guid To { get; init; }
        public required string Kind { get; init; }
    }

    private sealed class PointDto
    {
        public required double X { get; init; }
        public required double Y { get; init; }
    }
}

internal static class AtomicFile
{
    public static void Replace(string path, ReadOnlySpan<byte> contents)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }
}
