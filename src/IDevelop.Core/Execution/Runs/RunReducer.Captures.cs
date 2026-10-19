using System.Collections.Immutable;

namespace IDevelop.Execution;

/// <summary>How a turn's capture differs from what its client's exit left: the refs that moved, and why.</summary>
internal sealed record CaptureDivergence(ImmutableArray<string> Refs, string Detail);

internal static partial class RunReducer
{
    internal static RunProblem? AppendProblem(RunRecord record, RunEvent e)
    {
        if (e is RunEvent.TurnClosed turn && record.RootExits.ContainsKey(turn.Key) && !CaptureSettled(record, turn.Key))
            return RunProblem.SettlementPending;
        if (e is RunEvent.AttemptClosed attempt && record.Claims.Keys.Any(launch =>
            launch.Attempt == attempt.Attempt && record.Settling(launch)) &&
            (attempt.End is not AttemptEnd.Recovered || HasUnsettledCapture(record, attempt.Attempt)))
            return RunProblem.SettlementPending;
        // A first claim waits while a producer it depends on still has an unresolved claim.
        if (e is RunEvent.TurnClaimed { Key.Turn: 1 } claim && ProducerHold(record, claim.Inputs))
            return RunProblem.UnresolvedOwnership;
        return null;
    }

    internal static bool HasUnsettledCapture(RunRecord record, AttemptId attempt) =>
        record.Claims.Keys.Any(launch => launch.Attempt == attempt && record.Settling(launch) && !CaptureSettled(record, launch));

    /// <summary>
    /// The operation a turn's capture records its block under when it could not be taken. A turn has one capture, so the
    /// id follows from the turn.
    /// </summary>
    internal static OperationId CaptureBlock(LaunchKey launch) =>
        OperationIds.Derive(new OperationId(launch.Attempt.Value), "capture-blocked/" + launch.Turn);

    /// <summary>The block that says why the turn's capture could not be taken, or null.</summary>
    internal static MaterializationBlock? CaptureFailure(RunRecord record, LaunchKey launch)
    {
        var operation = CaptureBlock(launch);
        return record.Blocks.Values.Select(state => state.Block).FirstOrDefault(block => block.Operation == operation);
    }

    /// <summary>Whether the turn's capture is recorded: taken, or blocked with why it could not be.</summary>
    internal static bool CaptureSettled(RunRecord record, LaunchKey launch) =>
        record.Captures.ContainsKey(launch) || CaptureFailure(record, launch) is not null;

    /// <summary>
    /// How the capture differs from its turn's root observation, or null when the worktree is still on the commit and HEAD
    /// the client's exit left and the run's refs are as the journal explains them.
    /// </summary>
    internal static CaptureDivergence? Divergence(RunRecord record, CaptureObservation capture)
    {
        var root = record.RootExits[capture.Launch];
        ImmutableArray<string> refs = [];
        if (capture.Tip != root.Tip)
            refs = refs.Add(record.Preparations[capture.Launch].Location.Owner.Branch ?? "HEAD");
        if (capture.Head != root.Head && !refs.Contains("HEAD")) refs = refs.Add("HEAD");
        if (!refs.IsEmpty) return new([.. refs.Order(StringComparer.Ordinal)], "The branch tip or HEAD differs from the root observation.");
        return capture.UnexplainedRefs.IsEmpty ? null
            : new([.. capture.UnexplainedRefs.Order(StringComparer.Ordinal)], "The capture contains unexplained shared refs.");
    }
}

internal static partial class RunValidation
{
    private static bool Capture(CaptureObservation observation) => observation.Capture.Value != Guid.Empty &&
        Key(observation.Launch) && Checkpoint(observation.Log) && observation.Completed >= observation.Started &&
        Recipe(observation.Recipe) && (observation.Candidate is null || Revision.IsCommit(observation.Candidate.Value.Hex)) &&
        (observation.Tip is null || Revision.IsCommit(observation.Tip.Value.Hex)) &&
        (observation.Head is null || !string.IsNullOrWhiteSpace(observation.Head)) &&
        (observation.Index is null || Evidence(observation.Index) &&
            observation.Index.RelativePath == RunStorage.CapturePath(observation.Capture, "index")) &&
        (observation.IndexTree is null || Revision.IsCommit(observation.IndexTree.Value.Hex)) &&
        !observation.Artifacts.IsDefault && observation.Artifacts.All(file => Artifact(file) &&
            file.StoredPath == RunStorage.CapturePath(observation.Capture, "artifacts/" + file.Name)) &&
        observation.Artifacts.Select(file => file.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == observation.Artifacts.Length &&
        Evidence(observation.SharedRefs) &&
        observation.SharedRefs.RelativePath == RunStorage.CapturePath(observation.Capture, "refs.json") &&
        !observation.UnexplainedRefs.IsDefault && observation.UnexplainedRefs.All(Reference);
}
