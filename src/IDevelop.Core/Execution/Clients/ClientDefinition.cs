using System.Collections.Immutable;
using System.Text.Json;

namespace IDevelop.Execution;

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
