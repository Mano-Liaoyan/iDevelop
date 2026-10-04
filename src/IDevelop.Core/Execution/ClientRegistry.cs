using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>What a client said, normalized. Interpreters produce these, and only the attempt reducer consumes them.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SessionStarted), "sessionStarted")]
[JsonDerivedType(typeof(Reported), "reported")]
[JsonDerivedType(typeof(Message), "message")]
[JsonDerivedType(typeof(ToolStarted), "toolStarted")]
[JsonDerivedType(typeof(Notice), "notice")]
[JsonDerivedType(typeof(Succeeded), "succeeded")]
[JsonDerivedType(typeof(Failed), "failed")]
internal abstract record AgentEvent
{
    private AgentEvent() { }

    public sealed record SessionStarted(string SessionId) : AgentEvent;

    /// <summary>The model or reasoning level the client says it used. A null field leaves the previous value.</summary>
    public sealed record Reported(string? Model, string? Reasoning) : AgentEvent;

    /// <summary>A complete assistant message. The last one is the result when the verdict carries no text.</summary>
    public sealed record Message(string Text) : AgentEvent;

    public sealed record ToolStarted(string Tool, string? Detail) : AgentEvent;

    /// <summary>A diagnostic that does not decide the outcome.</summary>
    public sealed record Notice(string Text) : AgentEvent;

    /// <summary>The client's own verdict. A success still needs exit code 0 to count.</summary>
    public sealed record Succeeded(string? Result) : AgentEvent;

    public sealed record Failed(string Reason) : AgentEvent;
}

/// <summary>A short command a client answers without starting an agent: a model list or a sign-in check.</summary>
internal sealed record Probe(ImmutableArray<string> Arguments)
{
    public string? Stdin { get; init; }

    /// <summary>A probe that keeps running after it answers gets the end of its input at the first line this accepts.</summary>
    public Func<string, bool>? DoneWhen { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary><see cref="ExitCode"/> is null when the probe was stopped, at its timeout or after it answered and did not exit.</summary>
internal sealed record ProbeOutput(int? ExitCode, string Stdout, string Stderr, bool TimedOut);

internal abstract record CatalogSource
{
    private CatalogSource() { }

    public sealed record Fixed(ImmutableArray<ModelOption> Models) : CatalogSource;

    public sealed record Probed(Probe Probe, Func<ProbeOutput, CatalogParse> Parse) : CatalogSource;
}

internal abstract record CatalogParse
{
    private CatalogParse() { }

    public sealed record Models(ImmutableArray<ModelOption> Options) : CatalogParse;

    public sealed record Problem(string Reason) : CatalogParse;
}

/// <summary>
/// A sign-in check. <see cref="Provider"/> is null when it covers the whole client, or names the Pi provider whose
/// models it covers. <see cref="Problem"/> returns null when the client is ready, or the reason it is not.
/// </summary>
internal sealed record ReadinessProbe(string? Provider, Probe Probe, Func<ProbeOutput, string?> Problem);

internal sealed record LaunchRequest(string Model, string? Reasoning, string Prompt);

internal sealed record LaunchArguments(ImmutableArray<string> Arguments, string Stdin);

/// <summary>
/// Everything iDevelop knows about one client, as data and pure functions. No member starts a process or touches a file.
/// The client directory and the run supervisor do that the same way for every row.
/// </summary>
internal sealed record ClientDefinition
{
    /// <summary>The command looked up on the search path.</summary>
    public required string Command { get; init; }

    public required CatalogSource Catalog { get; init; }

    /// <summary>Readiness can depend on the catalog, because Pi signs in per provider.</summary>
    public required Func<ImmutableArray<ModelOption>, ImmutableArray<ReadinessProbe>> Readiness { get; init; }

    public required Func<LaunchRequest, LaunchArguments> Launch { get; init; }

    /// <summary>One stdout line to normalized events. It may throw <see cref="JsonException"/> on a line that is not JSON.</summary>
    public required Func<string, ImmutableArray<AgentEvent>> Interpret { get; init; }
}

internal static class ClientRegistry
{
    /// <summary>Weakest to strongest, so pickers list levels in one order whatever order a client prints them in.</summary>
    private static readonly ImmutableArray<string> LevelOrder = ["off", "none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra"];

    public static ClientDefinition Get(ClientId id) => id switch
    {
        ClientId.ClaudeCode => ClaudeCode.Definition,
        ClientId.Codex => Codex.Definition,
        ClientId.Pi => Pi.Definition,
        ClientId.Antigravity => Antigravity.Definition,
    };

    public static bool IsLevel(string name) => LevelOrder.Contains(name);

    /// <summary>Known levels weakest first, then any others in the order given.</summary>
    public static ImmutableArray<string> SortLevels(IEnumerable<string> levels) =>
        [.. levels.Distinct().OrderBy(level => LevelOrder.IndexOf(level) is var rank and >= 0 ? rank : LevelOrder.Length)];

    /// <summary>For a client that names no default: "medium" when offered, else the nearest level, the stronger on a tie.</summary>
    public static string? DefaultLevel(ImmutableArray<string> levels)
    {
        var medium = LevelOrder.IndexOf("medium");
        return levels
            .Where(IsLevel)
            .OrderBy(level => Math.Abs(LevelOrder.IndexOf(level) - medium))
            .ThenByDescending(level => LevelOrder.IndexOf(level))
            .FirstOrDefault();
    }

    /// <summary>Why a probe did not answer as expected, in the words of its last output line.</summary>
    public static string ProbeFailure(string command, ProbeOutput output)
    {
        var said = LastLine(output.Stderr) ?? LastLine(output.Stdout);
        var ended = output.ExitCode is { } code ? $"{command} exited with code {code}" : $"{command} was stopped";
        return said is null ? $"{ended}." : $"{ended}: {said}";
    }

    public static IEnumerable<string> Lines(string text) =>
        text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Trim().Length > 0);

    private static string? LastLine(string text) => Lines(text).Select(line => line.Trim()).LastOrDefault();
}

/// <summary>Reads client JSON leniently: a missing field or a field of another kind reads as absent.</summary>
internal static class JsonFields
{
    public static JsonElement? Property(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value
            : null;

    public static string? String(this JsonElement element, string name) =>
        element.Property(name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    public static bool? Bool(this JsonElement element, string name) => element.Property(name)?.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    public static IEnumerable<JsonElement> Items(this JsonElement element, string name) =>
        element.Property(name) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];

    public static string? NonBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
