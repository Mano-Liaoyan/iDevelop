using System.Collections.Immutable;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using IDevelop.Projects;
using IDevelop.Workflows;

namespace IDevelop.Execution;

internal sealed record JournalRead(ImmutableArray<RunEntry> Entries, RunRejection? Rejection);

/// <summary>The versioned JSONL encoding and strict decoding of run events.</summary>
internal static class RunJournal
{
    internal static readonly JsonSerializerOptions Options = CreateOptions();

    private static readonly HashSet<string> Events = ["approved", "amended", "reserved", "turnClaimed", "turnClosed", "attemptClosed",
        "resultAccepted", "stopRequested", "settled", "abandoned"];

    public static string Encode(RunEntry entry) => JsonSerializer.Serialize(entry, Options) + "\n";

    public static JournalRead Decode(string jsonl)
    {
        var entries = ImmutableArray.CreateBuilder<RunEntry>();
        var lines = jsonl.Split('\n');
        for (var index = 0; index < lines.Length - 1; index++)
        {
            var sequence = index + 1L;
            try
            {
                using var document = JsonDocument.Parse(lines[index], new JsonDocumentOptions { AllowDuplicateProperties = false });
                var root = document.RootElement;
                if (root.GetProperty("schema").GetInt32() != 1)
                {
                    return new(entries.ToImmutable(), new(RunProblem.UnsupportedSchema, sequence));
                }
                if (root.GetProperty("sequence").GetInt64() != sequence)
                {
                    return new(entries.ToImmutable(), new(RunProblem.SequenceGap, sequence));
                }
                if (!Events.Contains(root.GetProperty("event").GetProperty("type").GetString() ?? ""))
                {
                    return new(entries.ToImmutable(), new(RunProblem.UnsupportedEvent, sequence));
                }
                var entry = JsonSerializer.Deserialize<RunEntry>(lines[index], Options)!;
                if (RunValidation.Entry(entry) is { } problem)
                {
                    return new(entries.ToImmutable(), new(problem, sequence));
                }
                entries.Add(entry);
            }
            catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException or
                KeyNotFoundException or FormatException or ArgumentException or ProjectException or BlueprintException or OverflowException)
            {
                return new(entries.ToImmutable(), new(RunProblem.InvalidData, sequence));
            }
        }
        return new(entries.ToImmutable(), lines[^1].Length == 0 ? null : new(RunProblem.IncompleteTail, entries.Count + 1L));
    }

    internal static string Canonical<T>(T value)
    {
        var element = JsonSerializer.SerializeToElement(value, Options);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            Sorted(writer, element);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Sorted(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Sorted(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    Sorted(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static JsonSerializerOptions CreateOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        Converters =
        {
            new IdConverter<WorkflowId>(id => id.Value, value => new(value)),
            new IdConverter<TaskId>(id => id.Value, value => new(value)),
            new IdConverter<AttemptId>(id => id.Value, value => new(value)),
            new IdConverter<RunId>(id => id.Value, value => new(value)),
            new IdConverter<InputId>(id => id.Value, value => new(value)),
            new IdConverter<ResultId>(id => id.Value, value => new(value)),
            new IdConverter<OperationId>(id => id.Value, value => new(value)),
            new WorkflowConverter(), new TaskConverter(), new BlueprintConverter(),
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false),
        },
    };

    internal sealed class IdConverter<T>(Func<T, Guid> value, Func<Guid, T> create) : JsonConverter<T>
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TryGetGuid(out var id) && id != Guid.Empty ? create(id) : throw new JsonException("Invalid id.");

        public override void Write(Utf8JsonWriter writer, T id, JsonSerializerOptions options) => writer.WriteStringValue(value(id));
    }

    internal sealed class WorkflowConverter : JsonConverter<Workflow>
    {
        public override Workflow Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var json = JsonDocument.ParseValue(ref reader);
            return WorkflowFile.Parse(Encoding.UTF8.GetBytes(json.RootElement.GetRawText()), "run snapshot").Workflow;
        }

        public override void Write(Utf8JsonWriter writer, Workflow value, JsonSerializerOptions options)
        {
            using var json = JsonDocument.Parse(WorkflowFile.Serialize(value));
            json.RootElement.WriteTo(writer);
        }
    }

    internal sealed class BlueprintConverter : JsonConverter<Blueprint>
    {
        public override Blueprint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            BlueprintJson.FromDto(JsonSerializer.Deserialize<BlueprintDto>(ref reader,
                WorkflowFile.Options) ?? throw new JsonException("Invalid blueprint."), "run proposal");

        public override void Write(Utf8JsonWriter writer, Blueprint value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, BlueprintJson.ToDto(value), options);
    }

    internal sealed class TaskConverter : JsonConverter<TaskDefinition>
    {
        private static readonly WorkflowId Container = new(Guid.Parse("00000000-0000-0000-0000-000000000001"));

        public override TaskDefinition Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var json = JsonDocument.ParseValue(ref reader);
            try
            {
                var workflow = WorkflowFile.Parse(Encoding.UTF8.GetBytes(json.RootElement.GetRawText()), "standalone capture").Workflow;
                if (workflow.Tasks.Count != 1 || workflow.Connections.Count != 0)
                {
                    throw new JsonException("Invalid capture.");
                }

                return workflow.Tasks.Values.Single();
            }
            catch (ProjectException error)
            {
                throw new JsonException("Invalid standalone capture.", error);
            }
        }

        public override void Write(Utf8JsonWriter writer, TaskDefinition task, JsonSerializerOptions options)
        {
            var placed = (EditResult.Applied)Workflow.Empty(Container).Apply(new WorkflowEdit.PlaceNode(task.Id, task.Blueprint, new(0, 0))
            {
                Title = task.Title,
                Fields = task.Fields.ToImmutableDictionary(),
                Settings = new(task.Execution, task.Conversation),
            });
            using var json = JsonDocument.Parse(WorkflowFile.Serialize(placed.Workflow));
            json.RootElement.WriteTo(writer);
        }
    }
}
