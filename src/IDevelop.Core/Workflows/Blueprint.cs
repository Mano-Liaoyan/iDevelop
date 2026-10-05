using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace IDevelop.Workflows;

/// <summary>A blueprint version, written <c>id@version</c>. A node records the key of the blueprint it was placed from.</summary>
public readonly partial record struct BlueprintKey(string Id, int Version) : IComparable<BlueprintKey>
{
    public int CompareTo(BlueprintKey other)
    {
        var byId = string.CompareOrdinal(Id, other.Id);
        return byId != 0 ? byId : Version.CompareTo(other.Version);
    }

    public override string ToString() => $"{Id}@{Version}";

    /// <summary>Null unless the text is a valid id, <c>@</c>, and a version from 1.</summary>
    public static BlueprintKey? Parse(string text)
    {
        var at = text.LastIndexOf('@');
        return at > 0 && IsId(text[..at]) && int.TryParse(text[(at + 1)..], System.Globalization.NumberStyles.None, null, out var version) && version >= 1
            ? new BlueprintKey(text[..at], version)
            : null;
    }

    /// <summary>A letter or digit, then letters, digits, <c>.</c>, <c>_</c>, and <c>-</c>.</summary>
    public static bool IsId(string id) => IdPattern().IsMatch(id);

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]*\z")]
    private static partial Regex IdPattern();
}

/// <summary>
/// What a node does. A closed set: a new work needs code, which is one <see cref="WorkSpec"/> case, one value here, and
/// one implementation of <c>INodeWork</c>. The build fails on an enum switch that misses a value.
/// </summary>
public enum WorkKind { Agent }

/// <summary>What an agent work may do in the project folder.</summary>
public enum AgentAccess { ReadOnly, Edit }

/// <summary>A work and the settings the blueprint fixes for it.</summary>
public abstract record WorkSpec
{
    private WorkSpec() { }

    public abstract WorkKind Kind { get; }

    /// <summary>The template's own variables beside the title, the fields, and the inputs.</summary>
    public abstract ImmutableArray<string> Variables { get; }

    /// <summary>One agent session. <paramref name="Proposes"/> says whether it proposes graph edits.</summary>
    public sealed record Agent(AgentAccess Access, bool Proposes, PromptTemplate Template) : WorkSpec
    {
        public override WorkKind Kind => WorkKind.Agent;

        public override ImmutableArray<string> Variables => [];
    }
}

/// <summary>One line or several.</summary>
public enum FieldShape { Line, Text }

/// <summary>A value a blueprint asks for. <paramref name="Key"/> is the template variable that inserts it.</summary>
public sealed record FieldSpec(string Key, string Label, FieldShape Shape, bool Required, string Default)
{
    public string Default { get; } = Default.Replace("\r\n", "\n").Replace('\r', '\n');
}

/// <summary>
/// When the agent waits for the person. Autonomous never waits. May ask waits when the agent ends a turn with a
/// question. Chat waits after every turn.
/// </summary>
public enum ConversationMode { Autonomous, MayAsk, Chat }

/// <summary>A node's agent and conversation mode, which a blueprint seeds and a node may change.</summary>
public sealed record NodeSettings(ExecutionSettings? Execution, ConversationMode Conversation);

/// <summary>
/// A node type: data that fixes a node's structure. The work, its access, the fields, and the templates are fixed by the
/// version. The title, the field values, and the settings are seeded from it, and each node may change them. The
/// constructor checks every rule, so every instance is valid.
/// </summary>
public sealed partial record Blueprint
{
    /// <summary>The prefix of built-in blueprint ids, which no user blueprint may take.</summary>
    public const string BuiltInPrefix = "idevelop.";

    /// <summary>Variables every template may read: the node's title and what earlier nodes handed on.</summary>
    public static readonly ImmutableArray<string> CommonVariables = ["title", "inputs"];

    /// <exception cref="BlueprintException">The blueprint breaks a rule. The message is for the user.</exception>
    public Blueprint(BlueprintKey key, string name, WorkSpec work, ImmutableArray<FieldSpec> fields, NodeSettings defaults)
    {
        if (!BlueprintKey.IsId(key.Id) || key.Version < 1)
        {
            throw new BlueprintException($"\"{key}\" is not a blueprint id and version.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new BlueprintException($"Blueprint {key} has no name.");
        }

        if (fields.FirstOrDefault(field => string.IsNullOrWhiteSpace(field.Label)) is { } unlabeled)
        {
            throw new BlueprintException($"Blueprint {key}'s field \"{unlabeled.Key}\" has no label.");
        }

        HashSet<string> known = [.. CommonVariables, .. work.Variables];
        foreach (var field in fields)
        {
            if (!FieldKey().IsMatch(field.Key))
            {
                throw new BlueprintException($"Blueprint {key} has a field key \"{field.Key}\", which is not a letter followed by letters and digits.");
            }

            if (!known.Add(field.Key))
            {
                throw new BlueprintException($"Blueprint {key} uses the name \"{field.Key}\" twice.");
            }
        }

        foreach (var template in Templates(work))
        {
            if (template.Variables.FirstOrDefault(variable => !known.Contains(variable)) is { } unknown)
            {
                throw new BlueprintException($"Blueprint {key}'s template reads {{{{{unknown}}}}}, which is not a field.");
            }
        }

        Key = key;
        Name = name.Trim();
        Work = work;
        Fields = fields;
        Defaults = defaults;
    }

    public BlueprintKey Key { get; }

    public string Name { get; }

    public string Description { get; init => field = value.Replace("\r\n", "\n").Replace('\r', '\n'); } = "";

    /// <summary>The blueprint this one was derived from, if any.</summary>
    public BlueprintKey? DerivedFrom { get; init; }

    public WorkSpec Work { get; }

    /// <summary>In the order the inspector shows them.</summary>
    public ImmutableArray<FieldSpec> Fields { get; }

    public NodeSettings Defaults { get; }

    public bool IsBuiltIn => Key.Id.StartsWith(BuiltInPrefix, StringComparison.Ordinal);

    public FieldSpec? Field(string key) => Fields.FirstOrDefault(field => field.Key == key);

    // ImmutableArray compares by reference, so equality compares the fields themselves.
    public bool Equals(Blueprint? other) =>
        other is not null && Key == other.Key && Name == other.Name && Description == other.Description && DerivedFrom == other.DerivedFrom &&
        Work == other.Work && Fields.SequenceEqual(other.Fields) && Defaults == other.Defaults;

    public override int GetHashCode() => HashCode.Combine(Key, Name, Work);

    private static IEnumerable<PromptTemplate> Templates(WorkSpec work) => work.Kind switch
    {
        WorkKind.Agent => [((WorkSpec.Agent)work).Template],
    };

    [GeneratedRegex(@"\A[A-Za-z][A-Za-z0-9]*\z")]
    private static partial Regex FieldKey();
}

/// <summary>A blueprint that breaks a rule. The message is for the user.</summary>
public sealed class BlueprintException(string message) : Exception(message);
