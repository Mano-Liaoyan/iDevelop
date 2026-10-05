using System.Collections.Immutable;
using System.Text.Json;

namespace IDevelop.Execution;

/// <summary>
/// Everything iDevelop knows about one client, as data and pure functions. No member starts a process or touches a file.
/// The client directory and the run supervisor do that the same way for every row.
/// </summary>
internal sealed record ClientDefinition
{
    /// <summary>The name the interface shows.</summary>
    public required string Name { get; init; }

    /// <summary>The name the workflow file and the attempt log store.</summary>
    public required string WireName { get; init; }

    /// <summary>The command looked up on the search path.</summary>
    public required string Command { get; init; }

    public required CatalogSource Catalog { get; init; }

    /// <summary>Readiness can depend on the catalog, because Pi signs in per provider.</summary>
    public required Func<ImmutableArray<ModelOption>, ImmutableArray<ReadinessProbe>> Readiness { get; init; }

    public required Func<LaunchRequest, LaunchArguments> Launch { get; init; }

    /// <summary>One stdout line to normalized events. It may throw <see cref="JsonException"/> on a line that is not JSON.</summary>
    public required Func<string, ImmutableArray<AgentEvent>> Interpret { get; init; }

    /// <summary>The command a person types to open a session in the client's own terminal interface.</summary>
    public required Func<string, string> Terminal { get; init; }
}

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

internal sealed record LaunchRequest(string Model, string? Reasoning, string Prompt)
{
    /// <summary>The client's own id of the session this turn continues, or null for a new session.</summary>
    public string? ResumeSession { get; init; }
}

internal sealed record LaunchArguments(ImmutableArray<string> Arguments, string Stdin);

/// <summary>A model a client offers. <see cref="ReasoningLevels"/> is empty when the model takes no reasoning setting.</summary>
public sealed record ModelOption(string Id, string Name, ImmutableArray<string> ReasoningLevels)
{
    public string? DefaultReasoning { get; init; }

    /// <summary>Pi only: the provider whose sign-in this model needs.</summary>
    public string? Provider { get; init; }

    /// <summary>Why this model cannot run now, such as a Pi provider that is signed out, or null.</summary>
    public string? Problem { get; init; }

    // ImmutableArray compares by reference, so equality compares the levels themselves.
    public bool Equals(ModelOption? other) =>
        other is not null && Id == other.Id && Name == other.Name && ReasoningLevels.SequenceEqual(other.ReasoningLevels) &&
        DefaultReasoning == other.DefaultReasoning && Provider == other.Provider && Problem == other.Problem;

    public override int GetHashCode() => HashCode.Combine(Id, Name, DefaultReasoning, Provider, Problem);
}
