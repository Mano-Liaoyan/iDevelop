using System.Collections.Immutable;

namespace IDevelop.Execution;

internal static partial class RunReducer
{
    /// <summary>The task's newest result accepted before <paramref name="attempt"/> was reserved, which a fix round follows.</summary>
    internal static ResultRecord? PriorResult(RunRecord record, AttemptId attempt)
    {
        var task = record.Attempts[attempt].Task;
        var reserved = record.Receipts.Values.FirstOrDefault(entry => entry.Event is RunEvent.Reserved r && r.Attempt.Id == attempt)?.Sequence ?? long.MaxValue;
        return record.Receipts.Values.Where(entry => entry.Sequence < reserved && entry.Event is RunEvent.ResultAccepted accepted && accepted.Result.Task == task)
            .OrderBy(entry => entry.Sequence).Select(entry => ((RunEvent.ResultAccepted)entry.Event).Result).LastOrDefault();
    }

    /// <summary>The artifacts of a review fix's prior result, which its own result keeps unless it declares the name again.</summary>
    internal static ImmutableArray<ArtifactRecord> FixArtifacts(RunRecord record, AttemptId attempt) =>
        record.ReviewOf(attempt) is not null && PriorResult(record, attempt) is { } prior ? prior.Artifacts : [];

    /// <summary>
    /// The prior result's artifacts that a review fix's result carries over, stored under <paramref name="result"/>: each one
    /// whose name the fix's own <paramref name="declared"/> artifacts do not take, compared without case.
    /// </summary>
    internal static ImmutableArray<ArtifactRecord> CarriedArtifacts(RunRecord record, AttemptId attempt, ImmutableArray<ArtifactRecord> declared,
        ResultId result) =>
        [.. FixArtifacts(record, attempt).Where(artifact => !declared.Any(own => string.Equals(own.Name, artifact.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(artifact => artifact with { StoredPath = RunStorage.ArtifactPath(result, artifact.Name) })];
}
