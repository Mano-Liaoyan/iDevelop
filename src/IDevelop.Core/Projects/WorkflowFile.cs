using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using IDevelop.Execution;
using IDevelop.Workflows;

namespace IDevelop.Projects;

internal static class WorkflowFile
{
    /// <summary>The only format this version reads and writes.</summary>
    public const string FormatV2 = "idevelop.workflow/2";

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
            Format = FormatV2,
            Id = workflow.Id.Value,
            Tasks = [.. workflow.Tasks.Values.Select(task => new TaskDto
            {
                Id = task.Id.Value,
                Title = task.Title,
                Instructions = Lines(task.Instructions),
                AcceptanceCriteria = Lines(task.AcceptanceCriteria),
                Execution = task.Execution is { } execution
                    ? new ExecutionDto { Client = Clients.WireName(execution.Client), Model = execution.Model, Reasoning = execution.Reasoning }
                    : null,
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
            file = format == FormatV2
                ? JsonSerializer.Deserialize<FileDto>(utf8, Options)!
                : throw new ProjectException($"{path} has format \"{format}\". This version of iDevelop reads {FormatV2}.");
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
                Execution = task.Execution is { } execution ? Execution(execution, path, id) : null,
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

    private static ExecutionSettings Execution(ExecutionDto execution, string path, TaskId task) =>
        new(Clients.ParseWireName(execution.Client) ?? throw new ProjectException($"{path}: task {task} names unknown agent \"{execution.Client}\"."))
        {
            Model = execution.Model,
            Reasoning = execution.Reasoning,
        };

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
        public required ExecutionDto? Execution { get; init; }
    }

    private sealed class ExecutionDto
    {
        public required string Client { get; init; }
        public required string? Model { get; init; }
        public required string? Reasoning { get; init; }
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
