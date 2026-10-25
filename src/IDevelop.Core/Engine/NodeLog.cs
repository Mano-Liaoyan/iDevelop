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
[JsonDerivedType(typeof(Conflicted), "conflicted")]
[JsonDerivedType(typeof(Feedback), "feedback")]
internal abstract record NodeEntry
{
    private NodeEntry() { }

    /// <summary>A turn starts on <paramref name="Basis"/>. <paramref name="Session"/> is the session it continues, or null for a fresh one.</summary>
    public sealed record TurnStarted(Basis Basis, string? Session) : NodeEntry
    {
        /// <summary>Routing started this fresh turn for the next round of the node's feedback loop.</summary>
        public bool Round { get; init; }
    }

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

    /// <summary>
    /// The node's worktree holds a conflicting merge, which <paramref name="Description"/> names, and the session's next
    /// commit resolves it on the conflict's parents.
    /// </summary>
    public sealed record Conflicted(string Description) : NodeEntry;

    /// <summary>
    /// The turn ended by sending <paramref name="Text"/>, a reviewer's findings or the person's note, back along the feedback
    /// edge to the node's predecessors, which continue their sessions with it.
    /// </summary>
    public sealed record Feedback(string Text) : NodeEntry;
}

/// <summary>
/// A node's log folded: its results, its session and the basis that session started on, the basis of its last turn, how
/// that turn ended, whether it has no end yet, whether the session resolves a conflict, and how many rounds of feedback it
/// sent since it last completed or waited for the person.
/// </summary>
internal sealed record NodeRecord(ImmutableList<NodeResult> History, string? Session, Basis? SessionBasis, Basis? TurnBasis, NodeEntry? End, bool Open)
{
    public static NodeRecord Empty { get; } = new([], null, null, null, null, false);

    public bool Resolving { get; init; }

    /// <summary>The rounds of feedback in the node's current loop. Any turn but routing's next round starts a new loop.</summary>
    public int Rounds { get; init; }

    /// <summary>The node's last turn continued its session rather than starting a fresh one.</summary>
    public bool Continued { get; init; }

    public NodeRecord Apply(NodeEntry entry) => entry switch
    {
        NodeEntry.TurnStarted { Session: null } start => this with
        {
            Session = null, SessionBasis = start.Basis, TurnBasis = start.Basis, Open = true, Resolving = false, Rounds = start.Round ? Rounds : 0, Continued = false,
        },
        NodeEntry.TurnStarted start => this with { TurnBasis = start.Basis, Open = true, Rounds = 0, Continued = true },
        NodeEntry.Agent { Event: AgentEvent.SessionStarted started } => this with { Session = started.SessionId },
        NodeEntry.Completed completed => this with { History = History.Add(completed.Result), End = entry, Open = false, Resolving = false, Rounds = 0 },
        NodeEntry.Waiting => this with { End = entry, Open = false, Rounds = 0 },
        NodeEntry.Feedback => this with { End = entry, Open = false, Rounds = Rounds + 1 },
        NodeEntry.Stopped => this with { End = entry, Open = false, Rounds = 0 },
        NodeEntry.Failed => this with { End = entry, Open = false },
        NodeEntry.Conflicted => this with { Resolving = true },
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

    /// <summary>Raised after each entry is written, on the thread that wrote it.</summary>
    public event Action? Appended;

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

        Appended?.Invoke();
    }

    private void Add(NodeEntry entry)
    {
        _entries = _entries.Add(entry);
        Record = Record.Apply(entry);
    }
}
