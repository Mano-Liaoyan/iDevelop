using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>
/// What an agreed review hands on besides the code it forwards: its dependency inputs' artifacts, stored under the
/// review's own result with their names, bytes, and digests kept. The reviewer owns none of the code, and the artifacts
/// keep their identity. This is the rule an approval's result follows too.
/// </summary>
internal static class ArtifactForwarding
{
    /// <summary>
    /// The dependency inputs' artifacts, stored under <paramref name="result"/>. An artifact reached through two inputs
    /// counts once. Two different artifacts whose names differ at most in case are a collision: the result could not hand
    /// on both, so <c>Collision</c> names the second.
    /// </summary>
    public static (ImmutableArray<ArtifactRecord> Artifacts, string? Collision) Artifacts(RunRecord record, ImmutableArray<InputBinding> bindings,
        ResultId result)
    {
        var forwarded = ImmutableArray.CreateBuilder<ArtifactRecord>();
        foreach (var binding in bindings.OfType<InputBinding.Provided>().Where(binding => binding.Kind == ConnectionKind.Dependency))
        {
            foreach (var artifact in record.Results.Single(source => source.Id == binding.Result).Artifacts)
            {
                var rehomed = artifact with { StoredPath = RunStorage.ArtifactPath(result, artifact.Name) };
                if (forwarded.FirstOrDefault(prior => string.Equals(prior.Name, artifact.Name, StringComparison.OrdinalIgnoreCase)) is { } prior)
                {
                    if (prior != rehomed) return ([], artifact.Name);
                    continue;
                }
                forwarded.Add(rehomed);
            }
        }
        return (forwarded.ToImmutable(), null);
    }

    /// <summary>The name two different forwarded artifacts share, or null.</summary>
    public static string? Collision(RunRecord record, ImmutableArray<InputBinding> bindings) =>
        Artifacts(record, bindings, new(Guid.Empty)).Collision;

    /// <summary>
    /// Copies each forwarded artifact's verified bytes from the dependency result that stores it to <paramref name="result"/>.
    /// A copy that is already there is checked instead.
    /// </summary>
    public static void Store(RunRecord record, ImmutableArray<InputBinding> bindings, ResultId result, RunStorage storage)
    {
        var (artifacts, collision) = Artifacts(record, bindings, result);
        if (collision is not null) throw new IOException($"Two inputs hand on different artifacts named {collision}.");
        foreach (var artifact in artifacts)
        {
            var source = bindings.OfType<InputBinding.Provided>().Where(binding => binding.Kind == ConnectionKind.Dependency)
                .Select(binding => record.Results.Single(candidate => candidate.Id == binding.Result))
                .First(candidate => candidate.Artifacts.Any(stored => stored.Name == artifact.Name && stored.Content == artifact.Content));
            var bytes = storage.ReadArtifact(source.Id, source.Artifacts.First(stored => stored.Name == artifact.Name && stored.Content == artifact.Content));
            RunStorage.Publish(storage.Folder, artifact.StoredPath, bytes, artifact.Content, artifact.ByteLength);
        }
    }

    /// <summary>The id a review's accepted result takes, derived from the acceptance, under which its artifacts are stored.</summary>
    public static ResultId ReviewResult(OperationId accept) => new(OperationIds.Derive(accept, "review-result").Value);
}
