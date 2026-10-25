using System.Collections.Immutable;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using IDevelop.Execution;
using IDevelop.Projects;
using IDevelop.Workflows;

namespace IDevelop.Engine;

/// <summary>One line of a node's log. A turn starts, the agent and the person speak, and the turn ends one way.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "entry")]
[JsonDerivedType(typeof(TurnStarted), "turnStarted")]
[JsonDerivedType(typeof(Agent), "agent")]
[JsonDerivedType(typeof(PersonMessage), "personMessage")]
[JsonDerivedType(typeof(Completed), "completed")]
[JsonDerivedType(typeof(Waiting), "waiting")]
[JsonDerivedType(typeof(Failed), "failed")]
[JsonDerivedType(typeof(Stopped), "stopped")]
[JsonDerivedType(typeof(MessageDropped), "messageDropped")]
internal abstract record NodeEntry
{
    private NodeEntry() { }

    /// <summary>A turn starts on <paramref name="Basis"/>. <paramref name="Session"/> is the session it continues, or null for a fresh one.</summary>
    public sealed record TurnStarted(Basis Basis, string? Session) : NodeEntry;

    public sealed record Agent(AgentEvent Event) : NodeEntry;

    public sealed record PersonMessage(string Text) : NodeEntry;

    public sealed record Completed(NodeResult Result) : NodeEntry;

    /// <summary>The turn ended and the node waits for the person. <paramref name="Text"/> is the agent's last reply.</summary>
    public sealed record Waiting(string Why, string? Text) : NodeEntry;

    public sealed record Failed(string Reason) : NodeEntry;

    /// <summary>The turn was stopped. Whether that leaves the node stale follows from its basis, as for a result.</summary>
    public sealed record Stopped : NodeEntry;

    /// <summary>A message that waited for a turn which was stopped, so it never went to the agent.</summary>
    public sealed record MessageDropped(string Text) : NodeEntry;
}

/// <summary>
/// A node's log folded: its results, its session and the basis that session started on, the basis of its last turn, how
/// that turn ended, and whether it has no end yet.
/// </summary>
internal sealed record NodeRecord(ImmutableList<NodeResult> History, string? Session, Basis? SessionBasis, Basis? TurnBasis, NodeEntry? End, bool Open)
{
    public static NodeRecord Empty { get; } = new([], null, null, null, null, false);

    public NodeRecord Apply(NodeEntry entry) => entry switch
    {
        NodeEntry.TurnStarted { Session: null } start => this with { Session = null, SessionBasis = start.Basis, TurnBasis = start.Basis, Open = true },
        NodeEntry.TurnStarted start => this with { TurnBasis = start.Basis, Open = true },
        NodeEntry.Agent { Event: AgentEvent.SessionStarted started } => this with { Session = started.SessionId },
        NodeEntry.Completed completed => this with { History = History.Add(completed.Result), End = entry, Open = false },
        NodeEntry.Waiting or NodeEntry.Failed or NodeEntry.Stopped => this with { End = entry, Open = false },
        _ => this,
    };
}

/// <summary>
/// One append-only JSONL file per node under the project's data folder, which Git ignores, replayed when the engine opens
/// it. A line cut off by a crash is skipped, and the next entry starts on a line of its own.
/// </summary>
internal sealed class NodeLog
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private readonly Lock _gate = new();
    private readonly string _project;
    private readonly string _path;
    private ImmutableList<NodeEntry> _entries = [];

    public NodeLog(string projectFolder, TaskId node)
    {
        _project = projectFolder;
        _path = PathOf(projectFolder, node);
        if (File.Exists(_path))
        {
            using (var file = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite))
            {
                if (file.Length > 0 && file.Seek(-1, SeekOrigin.End) >= 0 && file.ReadByte() != '\n')
                {
                    file.WriteByte((byte)'\n');
                }
            }

            foreach (var line in File.ReadLines(_path, Utf8))
            {
                try
                {
                    if (JsonSerializer.Deserialize<NodeEntry>(line, Options) is { } entry) Add(entry);
                }
                catch (JsonException) { }
            }
        }
    }

    public static string PathOf(string projectFolder, TaskId node) => Path.Combine(projectFolder, DataFolder.Name, "nodes", $"{node}.jsonl");

    public ImmutableList<NodeEntry> Entries
    {
        get { lock (_gate) return _entries; }
    }

    public NodeRecord Record { get; private set; } = NodeRecord.Empty;

    public void Append(NodeEntry entry)
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                DataFolder.EnsureGitIgnore(_project);
            }

            File.AppendAllText(_path, JsonSerializer.Serialize(entry, Options) + "\n", Utf8);
            Add(entry);
        }
    }

    private void Add(NodeEntry entry)
    {
        _entries = _entries.Add(entry);
        Record = Record.Apply(entry);
    }
}
