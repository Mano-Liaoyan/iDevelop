using System.Collections.Immutable;
using IDevelop.Workflows;

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

    /// <summary>Creates the protocol state owned by one turn's drain.</summary>
    public required Func<LaunchRequest, TurnProtocol> Protocol { get; init; }

    /// <summary>The command a person types to open a session in the client's own terminal interface.</summary>
    public required Func<string, string> Terminal { get; init; }

    /// <summary>
    /// Whether <see cref="LaunchRequest.ReadOnly"/> starts the client in a mode that changes no file. Without one, a read-only
    /// node would run with write access, so it does not start.
    /// </summary>
    public required bool HasReadOnlyMode { get; init; }

    /// <summary>
    /// The client finds a session only in the folder where it ran, so it cannot continue one after that folder moved. Pi
    /// 1.1.0 then starts an empty session under the same id (probe of 2026-10-09), so iDevelop refuses such a message.
    /// </summary>
    public bool SessionsPerFolder { get; init; }

    /// <summary>
    /// What the client prints on stderr when it found no session under the id it was given and started an empty one under
    /// that id, which it then reports as if it had resumed it. Null when the client never does so.
    /// </summary>
    public string? LostSessionNotice { get; init; }

    /// <summary>How the client lists its skills, or null when it cannot limit a task's skills, as Antigravity CLI cannot.</summary>
    public SkillSource? Skills { get; init; }
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
/// models it covers. <see cref="Problem"/> returns null when the client is ready, or why it is not.
/// </summary>
internal sealed record ReadinessProbe(string? Provider, Probe Probe, Func<ProbeOutput, ReadinessProblem?> Problem);

/// <summary>Why a sign-in check did not pass. <see cref="SignedOut"/> is false when the check itself failed.</summary>
internal sealed record ReadinessProblem(string Reason, bool SignedOut);

internal sealed record LaunchRequest(string Model, string? Reasoning, string Prompt)
{
    /// <summary>The client's own id of the session this turn continues, or null for a new session.</summary>
    public string? ResumeSession { get; init; }

    /// <summary>The turn runs in the client's read-only mode. Pi has none, so its turn can still edit files.</summary>
    public bool ReadOnly { get; init; }

    public string? WorkingFolder { get; init; }

    public EffectivePolicy? Policy { get; init; }

    /// <summary>The skills the node chose, as this machine's client lists them. Null leaves the client's own skills as they are.</summary>
    public SkillLaunch? Skills { get; init; }

    public EffectivePolicy PolicyFor(ClientId client) => Policy ?? ClientPolicy.For(
        client, ReadOnly, ConversationMode.Autonomous, reviewer: false, new HostQuestions.Disabled());
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
