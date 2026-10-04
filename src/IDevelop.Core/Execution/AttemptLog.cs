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

    private static readonly JsonSerializerOptions Options = new()
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
            new SettingsConverter(),
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
        // Other windows fold a live log, so readers share it.
        _events = new FileStream(Path.Combine(folder, EventsFile), mode, FileAccess.Write, FileShare.Read);
    }

    public string Folder { get; }

    /// <summary>The folder of a task's attempts, which also holds its run lock.</summary>
    public static string TaskFolder(string attemptsFolder, TaskId task) => Path.Combine(attemptsFolder, task.ToString());

    public static string FolderOf(string attemptsFolder, TaskId task, AttemptId attempt) =>
        Path.Combine(TaskFolder(attemptsFolder, task), attempt.ToString());

    /// <summary>Creates the attempt's folder and writes <paramref name="requested"/> as its first line.</summary>
    public static AttemptLog Create(string attemptsFolder, AttemptEvent.Requested requested)
    {
        var folder = Directory.CreateDirectory(FolderOf(attemptsFolder, requested.Task, requested.Attempt)).FullName;
        var log = new AttemptLog(folder, FileMode.CreateNew);
        try
        {
            log.Append(requested);
            return log;
        }
        catch
        {
            log.Dispose();
            throw;
        }
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
    public static (ImmutableDictionary<TaskId, AttemptRecord> Latest, ImmutableArray<string> Warnings) ReadLatest(string attemptsFolder)
    {
        var latest = ImmutableDictionary.CreateBuilder<TaskId, AttemptRecord>();
        var warnings = ImmutableArray.CreateBuilder<string>();
        try
        {
            if (!Directory.Exists(attemptsFolder))
            {
                return (latest.ToImmutable(), []);
            }

            foreach (var taskFolder in Directory.EnumerateDirectories(attemptsFolder).Where(IsIdFolder))
            {
                if (Newest(taskFolder, warnings) is { } record)
                {
                    latest[record.Task] = record;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"iDevelop could not read {attemptsFolder}. {e.Message}");
        }

        return (latest.ToImmutable(), warnings.ToImmutable());
    }

    /// <summary>
    /// The newest readable attempt of one task, or null. Reconciliation reads a task again this way once it holds the
    /// task's lock. The read that found the task running has already reported its warnings.
    /// </summary>
    public static AttemptRecord? ReadLatest(string attemptsFolder, TaskId task)
    {
        try
        {
            return Newest(TaskFolder(attemptsFolder, task), ImmutableArray.CreateBuilder<string>());
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

    private static AttemptRecord? Newest(string taskFolder, ImmutableArray<string>.Builder warnings)
    {
        var newestFirst = Directory.EnumerateDirectories(taskFolder).Where(IsIdFolder).OrderByDescending(folder => Guid.Parse(Path.GetFileName(folder)));
        foreach (var attemptFolder in newestFirst)
        {
            if (TryFold(attemptFolder, warnings) is { } record)
            {
                return record;
            }
        }

        return null;
    }

    private static AttemptRecord? TryFold(string folder, ImmutableArray<string>.Builder warnings)
    {
        try
        {
            if (AttemptReducer.Replay(Read(folder)) is { } record)
            {
                return record;
            }

            warnings.Add($"{folder} holds no attempt record, so iDevelop skipped it.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"iDevelop could not read {folder}, so it skipped that attempt. {e.Message}");
        }

        return null;
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
