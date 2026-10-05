using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using IDevelop.Workflows;

namespace IDevelop.Projects;

/// <summary>A workflow read from its file, and what the reader converted when the file was an older format.</summary>
internal sealed record ParsedWorkflow(Workflow Workflow, string? Converted);

internal static class WorkflowFile
{
    /// <summary>The format this version writes.</summary>
    public const string FormatV3 = "idevelop.workflow/3";

    /// <summary>Read and converted. It goes once no format 2 file remains.</summary>
    public const string FormatV2 = "idevelop.workflow/2";

    /// <summary>Strict, readable JSON, which a blueprint file shares.</summary>
    internal static readonly JsonSerializerOptions Options = new()
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

    internal static readonly JsonSerializerOptions HeaderOptions = new(JsonSerializerDefaults.Web);

    public static byte[] Serialize(Workflow workflow)
    {
        var file = new FileDto
        {
            Format = FormatV3,
            Id = workflow.Id.Value,
            Blueprints = [.. workflow.Blueprints.Values.Select(BlueprintJson.ToDto)],
            Tasks = [.. workflow.Tasks.Values.Select(task => new TaskDto
            {
                Id = task.Id.Value,
                Title = task.Title,
                Blueprint = task.Blueprint.Key.ToString(),
                Fields = task.Blueprint.Fields.ToDictionary(field => field.Key, string?[]? (field) => BlueprintJson.Lines(task.Field(field.Key))),
                Execution = BlueprintJson.Execution(task.Execution),
                Conversation = BlueprintJson.ConversationName(task.Conversation),
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

    public static ParsedWorkflow Parse(ReadOnlySpan<byte> utf8, string path)
    {
        // Some editors save UTF-8 with a byte order mark, which System.Text.Json rejects.
        if (utf8.StartsWith("﻿"u8))
        {
            utf8 = utf8["﻿"u8.Length..];
        }

        FileDto? file;
        FileDtoV2? older;
        try
        {
            var format = JsonSerializer.Deserialize<HeaderDto>(utf8, HeaderOptions)?.Format;
            (file, older) = format switch
            {
                FormatV3 => (JsonSerializer.Deserialize<FileDto>(utf8, Options)!, (FileDtoV2?)null),
                FormatV2 => (null, JsonSerializer.Deserialize<FileDtoV2>(utf8, Options)!),
                _ => throw new ProjectException($"{path} has format \"{format}\". This version of iDevelop reads {FormatV3} and {FormatV2}."),
            };
        }
        catch (JsonException e)
        {
            throw new ProjectException($"{path} is not a valid workflow file. {e.Message}", e);
        }

        return file is not null ? new ParsedWorkflow(Read(file, path), null) : Convert(older!, path);
    }

    private static Workflow Read(FileDto file, string path)
    {
        var blueprints = new Dictionary<BlueprintKey, Blueprint>();
        foreach (var (index, dto) in file.Blueprints.Index())
        {
            var blueprint = BlueprintJson.FromDto(dto ?? throw new ProjectException($"{path}: blueprints[{index}] is null."), path);
            if (!blueprints.TryAdd(blueprint.Key, blueprint))
            {
                throw new ProjectException($"{path}: blueprint {blueprint.Key} appears twice.");
            }

            // A built-in is read-only, so a copy that differs from the one this version ships was edited by hand.
            if (BuiltInBlueprints.Find(blueprint.Key) is { } builtIn && !builtIn.Equals(blueprint))
            {
                throw new ProjectException($"{path}: blueprint {blueprint.Key} differs from the built-in it names.");
            }
        }

        var workflow = Workflow.Empty(new WorkflowId(file.Id));
        foreach (var (index, task) in file.Tasks.Index())
        {
            if (task is null)
            {
                throw new ProjectException($"{path}: tasks[{index}] is null.");
            }

            var id = new TaskId(task.Id);
            var entry = $"{path}: task {id}";
            var blueprint = BlueprintKey.Parse(task.Blueprint) is { } key && blueprints.TryGetValue(key, out var found)
                ? found
                : throw new ProjectException($"{entry} names blueprint \"{task.Blueprint}\", which the file does not hold.");
            if (blueprint.Fields.Select(field => field.Key).FirstOrDefault(key => !task.Fields.ContainsKey(key)) is { } missing)
            {
                throw new ProjectException($"{entry} has no value for {missing}.");
            }

            var fields = ImmutableDictionary.CreateBuilder<string, string>();
            foreach (var (field, lines) in task.Fields)
            {
                fields[field] = BlueprintJson.Text(lines ?? throw new ProjectException($"{entry} has a null value for {field}."), entry, field);
            }

            var place = new WorkflowEdit.PlaceNode(id, blueprint, Position(file.Layout, path, id))
            {
                Title = task.Title,
                Fields = fields.ToImmutable(),
                Settings = new NodeSettings(
                    task.Execution is { } execution ? BlueprintJson.Execution(execution, entry) : null,
                    BlueprintJson.Conversation(task.Conversation, entry)),
            };
            workflow = Replay(workflow, place, path, $"task {id}");
        }

        if (blueprints.Keys.Where(key => !workflow.Blueprints.ContainsKey(key)).Select(key => (BlueprintKey?)key).FirstOrDefault() is { } unused)
        {
            throw new ProjectException($"{path}: no task uses blueprint {unused}.");
        }

        return Connect(workflow, file.Connections, file.Layout, path, ParseKind);
    }

    /// <summary>
    /// Each task becomes a node of the built-in Implement with the same text and agent, and each review connection becomes
    /// a dependency, which blocked the same way and did nothing at run time.
    /// </summary>
    private static ParsedWorkflow Convert(FileDtoV2 file, string path)
    {
        var workflow = Workflow.Empty(new WorkflowId(file.Id));
        foreach (var (index, task) in file.Tasks.Index())
        {
            if (task is null)
            {
                throw new ProjectException($"{path}: tasks[{index}] is null.");
            }

            var id = new TaskId(task.Id);
            var entry = $"{path}: task {id}";
            var place = new WorkflowEdit.PlaceNode(id, BuiltInBlueprints.Implement, Position(file.Layout, path, id))
            {
                Title = task.Title,
                Fields = ImmutableDictionary.CreateRange(
                [
                    KeyValuePair.Create("instructions", BlueprintJson.Text(task.Instructions, entry, "instructions")),
                    KeyValuePair.Create("acceptanceCriteria", BlueprintJson.Text(task.AcceptanceCriteria, entry, "acceptanceCriteria")),
                ]),
                Settings = new NodeSettings(task.Execution is { } execution ? BlueprintJson.Execution(execution, entry) : null, ConversationMode.Autonomous),
            };
            workflow = Replay(workflow, place, path, $"task {id}");
        }

        var reviews = file.Connections.Count(connection => connection?.Kind == "review");
        workflow = Connect(workflow, file.Connections, file.Layout, path, kind => kind == "review" ? ConnectionKind.Dependency : ParseKind(kind));
        return new ParsedWorkflow(workflow, Conversion(workflow.Tasks.Count, reviews));
    }

    /// <summary>What the window says after opening a format 2 file.</summary>
    private static string Conversion(int tasks, int reviews)
    {
        var nodes = tasks == 1 ? "Its task became an Implement node" : $"Its {tasks} tasks became Implement nodes";
        var connections = reviews switch
        {
            0 => "",
            1 => ", and its review connection became a dependency",
            _ => $", and its {reviews} review connections became dependencies",
        };
        var changes = tasks == 0 ? "" : $" {nodes}{connections}.";
        return $"iDevelop converted this workflow from format 2.{changes} Save to keep it in format 3.";
    }

    private static Workflow Connect(
        Workflow workflow, List<ConnectionDto?> connections, Dictionary<string, PointDto?> layout, string path, Func<string, ConnectionKind?> parseKind)
    {
        foreach (var (index, connection) in connections.Index())
        {
            if (connection is null)
            {
                throw new ProjectException($"{path}: connections[{index}] is null.");
            }

            var key = new ConnectionKey(new TaskId(connection.From), new TaskId(connection.To));
            var entry = $"connection {key.From} -> {key.To}";
            var kind = parseKind(connection.Kind) ?? throw new ProjectException($"{path}: {entry} has unknown kind \"{connection.Kind}\".");
            workflow = Replay(workflow, new WorkflowEdit.Connect(key, kind), path, entry);
        }

        var stray = layout.Keys.FirstOrDefault(key => !Guid.TryParse(key, out var id) || !workflow.Tasks.ContainsKey(new TaskId(id)));
        if (stray is not null)
        {
            throw new ProjectException($"{path}: the layout has a position for {stray}, which is not a task.");
        }

        return workflow;
    }

    private static CanvasPoint Position(Dictionary<string, PointDto?> layout, string path, TaskId id)
    {
        if (!layout.TryGetValue(id.ToString(), out var point))
        {
            throw new ProjectException($"{path}: task {id} has no position in the layout.");
        }

        return point is null
            ? throw new ProjectException($"{path}: task {id} has a null position in the layout.")
            : new CanvasPoint(point.X, point.Y);
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
        EditRejection.UnknownField field => $"has a value for {field.Key}, which its blueprint has no field for",
        EditRejection.BlueprintConflict conflict => $"holds blueprint {conflict.Key}, which differs from another copy of it",
        EditRejection.SelfConnection => "connects a task to itself",
        EditRejection.UnknownTask unknown => $"names {unknown.Task}, which is not a task",
        EditRejection.DuplicateConnection => "repeats an earlier connection between the same tasks",
        EditRejection.OrderingCycle cycle =>
            $"would close a cycle: {string.Join(" -> ", cycle.Path.Select(id => workflow.Tasks[id].Title))}",
        _ => reason.ToString(),
    };

    private static string KindName(ConnectionKind kind) => kind switch
    {
        ConnectionKind.Dependency => "dependency",
        ConnectionKind.Context => "context",
    };

    private static ConnectionKind? ParseKind(string name) =>
        Enum.GetValues<ConnectionKind>().Where(kind => KindName(kind) == name).Select(kind => (ConnectionKind?)kind).FirstOrDefault();

    internal sealed class HeaderDto
    {
        public string? Format { get; init; }
    }

    // RespectNullableAnnotations does not reach collection elements or dictionary values, so
    // those are nullable here and Parse reports each null with its entry.
    private sealed class FileDto
    {
        public required string Format { get; init; }
        public required Guid Id { get; init; }
        public required List<BlueprintDto?> Blueprints { get; init; }
        public required List<TaskDto?> Tasks { get; init; }
        public required List<ConnectionDto?> Connections { get; init; }
        public required Dictionary<string, PointDto?> Layout { get; init; }
    }

    private sealed class TaskDto
    {
        public required Guid Id { get; init; }
        public required string Title { get; init; }
        public required string Blueprint { get; init; }
        public required Dictionary<string, string?[]?> Fields { get; init; }
        public required ExecutionDto? Execution { get; init; }
        public required string Conversation { get; init; }
    }

    private sealed class FileDtoV2
    {
        public required string Format { get; init; }
        public required Guid Id { get; init; }
        public required List<TaskDtoV2?> Tasks { get; init; }
        public required List<ConnectionDto?> Connections { get; init; }
        public required Dictionary<string, PointDto?> Layout { get; init; }
    }

    private sealed class TaskDtoV2
    {
        public required Guid Id { get; init; }
        public required string Title { get; init; }
        public required string?[] Instructions { get; init; }
        public required string?[] AcceptanceCriteria { get; init; }
        public required ExecutionDto? Execution { get; init; }
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
