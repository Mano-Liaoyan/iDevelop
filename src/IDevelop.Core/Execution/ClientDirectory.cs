using System.Collections.Immutable;

namespace IDevelop.Execution;

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
