using System.Collections.Immutable;

namespace IDevelop.Execution;

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
