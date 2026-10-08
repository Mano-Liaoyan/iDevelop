using System.Collections.Immutable;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using IDevelop.Projects;
using IDevelop.Workflows;

namespace IDevelop.Execution;

internal sealed record JournalRead(ImmutableArray<RunEntry> Entries, RunRejection? Rejection);

/// <summary>The versioned JSONL encoding and strict decoding of run events.</summary>
internal static class RunJournal
{
    internal static readonly JsonSerializerOptions Options = CreateOptions();

    private static readonly HashSet<string> RunEvents = ["approved", "amended", "reserved", "turnClaimed", "turnClosed", "attemptClosed",
        "resultAccepted", "stopRequested", "settled", "abandoned", "ownershipFenced", "rootExitObserved", "turnCaptured", "captureDisposed"];

    private static readonly HashSet<string> MaterializationEvents = ["layoutAllocated", "planned", "gitIntended", "gitObserved",
        "prepared", "blocked", "salvageRetained", "blockResolved", "preserved", "preservationDiverged", "restored", "recoveryBaselined"];

    public static string Encode(RunEntry entry) => JsonSerializer.Serialize(entry, Options) + "\n";

    public static JournalRead Decode(string jsonl) => Decode(Encoding.UTF8.GetBytes(jsonl));

    public static JournalRead Decode(ReadOnlySpan<byte> jsonl)
    {
        var entries = ImmutableArray.CreateBuilder<RunEntry>();
        var utf8 = new UTF8Encoding(false, true);
        var offset = 0;
        while (offset < jsonl.Length)
        {
            var sequence = entries.Count + 1L;
            var length = jsonl[offset..].IndexOf((byte)'\n');
            if (length < 0)
            {
                return new(entries.ToImmutable(), new(RunProblem.IncompleteTail, sequence));
            }

            try
            {
                var line = utf8.GetString(jsonl.Slice(offset, length));
                using var document = JsonDocument.Parse(line, new JsonDocumentOptions { AllowDuplicateProperties = false });
                var root = document.RootElement;
                if (root.GetProperty("schema").GetInt32() is not (1 or 2 or 3))
                {
                    return new(entries.ToImmutable(), new(RunProblem.UnsupportedSchema, sequence));
                }
                if (root.GetProperty("sequence").GetInt64() != sequence)
                {
                    return new(entries.ToImmutable(), new(RunProblem.SequenceGap, sequence));
                }
                var eventType = root.GetProperty("event").GetProperty("type").GetString() ?? "";
                if (!RunEvents.Contains(eventType) && (root.GetProperty("schema").GetInt32() == 1 || !MaterializationEvents.Contains(eventType)))
                {
                    return new(entries.ToImmutable(), new(RunProblem.UnsupportedEvent, sequence));
                }
                var entry = JsonSerializer.Deserialize<RunEntry>(line, Options)!;
                if (RunValidation.Entry(entry) is { } problem)
                {
                    return new(entries.ToImmutable(), new(problem, sequence));
                }
                entries.Add(entry);
            }
            catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException or
                KeyNotFoundException or FormatException or ArgumentException or ProjectException or BlueprintException or OverflowException or
                DecoderFallbackException)
            {
                return new(entries.ToImmutable(), new(RunProblem.InvalidData, sequence));
            }
            offset += length + 1;
        }
        return new(entries.ToImmutable(), null);
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
        TypeInfoResolver = Resolver(),
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
            new IdConverter<CaptureId>(id => id.Value, value => new(value)),
            new InputConverter(), new WorkflowConverter(), new TaskConverter(), new BlueprintConverter(),
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false),
        },
    };

    private static IJsonTypeInfoResolver Resolver()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type == typeof(ResultRecord))
            {
                info.Properties.Single(property => property.Name == "artifacts").ShouldSerialize =
                    (_, value) => value is System.Collections.Immutable.ImmutableArray<ArtifactRecord> { IsDefaultOrEmpty: false };
            }
        });
        return resolver;
    }

    internal sealed class InputConverter : JsonConverter<InputRecord>
    {
        private sealed record LegacyInput(InputId Id, TaskId Task, RevisionId Revision, ImmutableArray<InputBinding> Bindings,
            CommitId CodeBase, string Text);

        private sealed record MaterialInput(InputId Id, TaskId Task, RevisionId Revision, ImmutableArray<InputBinding> Bindings,
            CodeSelection Code, string Text, ImmutableArray<DeliveredFile> Files, ReviewInput? Review);

        public override InputRecord Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var json = JsonDocument.ParseValue(ref reader);
            if (json.RootElement.TryGetProperty("codeBase", out _))
            {
                var legacy = json.RootElement.Deserialize<LegacyInput>(options)!;
                return new(legacy.Id, legacy.Task, legacy.Revision, legacy.Bindings, new CodeSelection.Legacy(legacy.CodeBase),
                    legacy.Text, [], null);
            }
            var input = json.RootElement.Deserialize<MaterialInput>(options)!;
            return new(input.Id, input.Task, input.Revision, input.Bindings, input.Code, input.Text, input.Files, input.Review);
        }

        public override void Write(Utf8JsonWriter writer, InputRecord value, JsonSerializerOptions options)
        {
            if (value.Code is CodeSelection.Legacy legacy)
            {
                JsonSerializer.Serialize(writer, new LegacyInput(value.Id, value.Task, value.Revision, value.Bindings, legacy.Base,
                    value.Text), options);
            }
            else
            {
                JsonSerializer.Serialize(writer, new MaterialInput(value.Id, value.Task, value.Revision, value.Bindings, value.Code,
                    value.Text, value.Files, value.Review), options);
            }
        }
    }

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
