using System.Collections.Immutable;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>
/// <c>.idp/attempts/&lt;task id&gt;/&lt;attempt id&gt;/</c>. <c>events.jsonl</c> is the only truth about an attempt, and it
/// is only ever appended to. <c>output.jsonl</c> holds the client's raw stdout and <c>stderr.log</c> its stderr. Those two
/// are evidence for people, and nothing reads them back.
/// </summary>
internal sealed class AttemptLog : IDisposable
{
    private const string EventsFile = "events.jsonl";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Keeps prompts and paths readable. The log is never embedded in HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        Converters =
        {
            new IdConverter<TaskId>(id => id.Value, value => new TaskId(value)),
            new IdConverter<AttemptId>(id => id.Value, value => new AttemptId(value)),
            new IdConverter<WorkflowId>(id => id.Value, value => new WorkflowId(value)),
            new IdConverter<RunId>(id => id.Value, value => new RunId(value)),
            new IdConverter<InputId>(id => id.Value, value => new InputId(value)),
            new SettingsConverter(),
            new BlueprintKeyConverter(),
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false),
        },
    };

    private readonly Lock _gate = new();
    private readonly FileStream _events;
    private StreamWriter? _output;
    private StreamWriter? _stderr;
    private bool _closed;

    private AttemptLog(string folder, FileMode mode)
    {
        Folder = folder;
        LineCount = mode == FileMode.Append ? File.ReadLines(Path.Combine(folder, EventsFile)).LongCount() : 0;
        // Other windows fold a live log, so readers share it.
        _events = new FileStream(Path.Combine(folder, EventsFile), mode, FileAccess.Write, FileShare.Read);
    }

    public string Folder { get; }

    public long LineCount { get; private set; }

    /// <summary>The folder of a task's attempts, which also holds its run lock.</summary>
    public static string TaskFolder(string attemptsFolder, TaskId task) => Path.Combine(attemptsFolder, task.ToString());

    public static string FolderOf(string attemptsFolder, TaskId task, AttemptId attempt) =>
        Path.Combine(TaskFolder(attemptsFolder, task), attempt.ToString());

    /// <summary>Creates the attempt's folder and writes <paramref name="requested"/> as its first line.</summary>
    public static AttemptLog Create(string attemptsFolder, AttemptEvent.Requested requested, Func<Stream, Stream>? events = null)
    {
        var folder = Directory.CreateDirectory(FolderOf(attemptsFolder, requested.Task, requested.Attempt)).FullName;
        var temporary = Path.Combine(folder, EventsFile + ".tmp");
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            using var stream = events is null ? file : events(file);
            stream.Write(Utf8.GetBytes(JsonSerializer.Serialize<AttemptEvent>(requested, Options) + "\n"));
            stream.Flush();
        }
        File.Move(temporary, Path.Combine(folder, EventsFile), overwrite: false);
        return Open(folder);
    }

    /// <summary>Opens an existing log to append to it, as reconciliation does.</summary>
    public static AttemptLog Open(string folder) => new(folder, FileMode.Append);

    /// <summary>One line per event, handed to the operating system before this returns, so it survives an app crash.</summary>
    public void Append(AttemptEvent e)
    {
        var line = Utf8.GetBytes(JsonSerializer.Serialize(e, Options) + "\n");
        lock (_gate)
        {
            _events.Write(line);
            _events.Flush();
            LineCount++;
        }
    }

    /// <summary>Does nothing once the log is closed: the client's pipes can outlive its attempt.</summary>
    public void AppendOutput(string line) => Evidence(ref _output, "output.jsonl", line);

    public void AppendStderr(string line) => Evidence(ref _stderr, "stderr.log", line);

    /// <summary>Lines that do not parse are skipped: a torn last line after a crash, or an event a newer version wrote.</summary>
    /// <exception cref="IOException">The log could not be read.</exception>
    public static ImmutableArray<AttemptEvent> Read(string folder)
    {
        using var stream = new FileStream(Path.Combine(folder, EventsFile), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Utf8);
        var events = ImmutableArray.CreateBuilder<AttemptEvent>();
        while (reader.ReadLine() is { } line)
        {
            try
            {
                if (JsonSerializer.Deserialize<AttemptEvent>(line, Options) is { } e)
                {
                    events.Add(e);
                }
            }
            catch (Exception e) when (e is JsonException or NotSupportedException)
            {
            }
        }

        return events.ToImmutable();
    }

    /// <summary>
    /// The newest readable attempt of each task, folded. Opening a project needs only these, and only these can be a
    /// run that a crashed instance left behind. A folder that cannot be read is skipped and named in the warnings.
    /// </summary>
    public static (ImmutableDictionary<TaskId, AttemptRecord> Latest, ImmutableArray<string> Warnings,
        ImmutableDictionary<(TaskId Task, AttemptId Attempt), long> LogRevisions) ReadLatest(string attemptsFolder)
    {
        var latest = ImmutableDictionary.CreateBuilder<TaskId, AttemptRecord>();
        var revisions = ImmutableDictionary.CreateBuilder<(TaskId Task, AttemptId Attempt), long>();
        var warnings = ImmutableArray.CreateBuilder<string>();
        try
        {
            if (!Directory.Exists(attemptsFolder))
            {
                return (latest.ToImmutable(), [], revisions.ToImmutable());
            }

            foreach (var taskFolder in Directory.EnumerateDirectories(attemptsFolder).Where(IsIdFolder))
            {
                var (record, lines) = Newest(taskFolder, warnings);
                if (record is not null)
                {
                    latest[record.Task] = record;
                    revisions[(record.Task, record.Id)] = lines;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"iDevelop could not read {attemptsFolder}. {e.Message}");
        }

        return (latest.ToImmutable(), warnings.ToImmutable(), revisions.ToImmutable());
    }

    /// <summary>
    /// The newest readable attempt of one task, or null. Reconciliation reads a task again this way once it holds the
    /// task's lock. The read that found the task running has already reported its warnings.
    /// </summary>
    public static (AttemptRecord? Record, long LineCount) ReadLatest(string attemptsFolder, TaskId task)
    {
        try
        {
            return Newest(TaskFolder(attemptsFolder, task), ImmutableArray.CreateBuilder<string>());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, 0);
        }
    }

    /// <summary>One attempt of a task, folded, or null when its log cannot be read.</summary>
    public static AttemptRecord? ReadAttempt(string attemptsFolder, TaskId task, AttemptId attempt)
    {
        try
        {
            return AttemptReducer.Replay(Read(FolderOf(attemptsFolder, task, attempt)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Preserves physical positions even when a torn or unknown line is skipped.</summary>
    public static ImmutableArray<PositionedAttemptEvent> ReadPositioned(string folder) => ReadPositioned(folder, out _, out _);

    public static ImmutableArray<PositionedAttemptEvent> ReadPositioned(string folder, out long lineCount, out long length)
    {
        using var stream = new FileStream(Path.Combine(folder, EventsFile), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Utf8);
        var events = ImmutableArray.CreateBuilder<PositionedAttemptEvent>();
        long position = 0;
        while (reader.ReadLine() is { } line)
        {
            try
            {
                if (JsonSerializer.Deserialize<AttemptEvent>(line, Options) is { } e)
                {
                    events.Add(new PositionedAttemptEvent(position, e));
                }
            }
            catch (Exception e) when (e is JsonException or NotSupportedException)
            {
            }

            position++;
        }

        lineCount = position;
        length = stream.Position;
        return events.ToImmutable();
    }

    public static AttemptHistory? ReadHistory(string attemptsFolder, TaskId task, AttemptId attempt,
        IReadOnlyDictionary<string, LiveMessageBuffer>? live = null) => ReadHistoryAt(FolderOf(attemptsFolder, task, attempt), task, attempt, live);

    /// <summary>The history of the attempt whose log is in <paramref name="folder"/>, standalone or run-owned.</summary>
    public static AttemptHistory? ReadHistoryAt(string folder, TaskId task, AttemptId attempt,
        IReadOnlyDictionary<string, LiveMessageBuffer>? live = null)
    {
        try
        {
            var events = ReadPositioned(folder);
            return events.FirstOrDefault(line => line.Event is AttemptEvent.Requested)?.Event is AttemptEvent.Requested requested
                && requested.Task == task && requested.Attempt == attempt
                ? ConversationHistory.Project(events, live) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _closed = true;
            _events.Dispose();
            _output?.Dispose();
            _stderr?.Dispose();
        }
    }

    private static (AttemptRecord? Record, long LineCount) Newest(string taskFolder, ImmutableArray<string>.Builder warnings)
    {
        var newestFirst = Directory.EnumerateDirectories(taskFolder).Where(IsIdFolder).OrderByDescending(folder => Guid.Parse(Path.GetFileName(folder)));
        foreach (var attemptFolder in newestFirst)
        {
            var result = TryFold(attemptFolder, warnings);
            if (result.Record is not null)
            {
                return result;
            }
        }

        return (null, 0);
    }

    private static (AttemptRecord? Record, long LineCount) TryFold(string folder, ImmutableArray<string>.Builder warnings)
    {
        try
        {
            var events = ReadPositioned(folder, out var lines, out _);
            if (AttemptReducer.Replay(events.Select(line => line.Event)) is { } record)
            {
                return (record, lines);
            }

            warnings.Add($"{folder} holds no attempt record, so iDevelop skipped it.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"iDevelop could not read {folder}, so it skipped that attempt. {e.Message}");
        }

        return (null, 0);
    }

    private static bool IsIdFolder(string folder) => Guid.TryParse(Path.GetFileName(folder), out _);

    private void Evidence(ref StreamWriter? writer, string file, string line)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            try
            {
                writer ??= new StreamWriter(new FileStream(Path.Combine(Folder, file), FileMode.Append, FileAccess.Write, FileShare.Read), Utf8);
                writer.Write(line);
                writer.Write('\n');
                writer.Flush();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Evidence only. The events log reports a folder that can no longer be written.
            }
        }
    }

    private sealed class IdConverter<T>(Func<T, Guid> value, Func<Guid, T> create) : JsonConverter<T>
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => create(reader.GetGuid());

        public override void Write(Utf8JsonWriter writer, T id, JsonSerializerOptions options) => writer.WriteStringValue(value(id));
    }

    /// <summary>Stores a key as <c>id@version</c>, as the workflow file does.</summary>
    private sealed class BlueprintKeyConverter : JsonConverter<BlueprintKey>
    {
        public override BlueprintKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            BlueprintKey.Parse(reader.GetString() ?? "") ?? throw new JsonException("The blueprint key is not an id and a version.");

        public override void Write(Utf8JsonWriter writer, BlueprintKey key, JsonSerializerOptions options) => writer.WriteStringValue(key.ToString());
    }

    /// <summary>Stores the client by its wire name, as the workflow file does.</summary>
    private sealed class SettingsConverter : JsonConverter<ExecutionSettings>
    {
        public override ExecutionSettings Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var json = JsonDocument.ParseValue(ref reader);
            var root = json.RootElement;
            var name = root.String("client") ?? throw new JsonException("The settings name no client.");
            var client = Clients.ParseWireName(name) ?? throw new JsonException($"The settings name unknown client \"{name}\".");
            return new ExecutionSettings(client) { Model = root.String("model"), Reasoning = root.String("reasoning") };
        }

        public override void Write(Utf8JsonWriter writer, ExecutionSettings settings, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("client", Clients.WireName(settings.Client));
            writer.WriteString("model", settings.Model);
            writer.WriteString("reasoning", settings.Reasoning);
            writer.WriteEndObject();
        }
    }
}
