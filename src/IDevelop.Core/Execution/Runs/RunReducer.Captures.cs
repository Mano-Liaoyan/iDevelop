namespace IDevelop.Execution;

internal static partial class RunReducer
{
    internal static RunProblem? AppendProblem(RunRecord record, RunEvent e)
    {
        if (record.Schema != 3) return null;
        if (e is RunEvent.TurnClosed { Capture: null } turn && record.RootExits.ContainsKey(turn.Key))
            return RunProblem.SettlementPending;
        if (e is RunEvent.AttemptClosed attempt && record.Claims.Keys.Any(launch =>
            launch.Attempt == attempt.Attempt && record.Settling(launch)) &&
            (attempt.End is not AttemptEnd.Recovered || !FailedSettlements(record, attempt.Attempt)))
            return RunProblem.SettlementPending;
        return null;
    }

    internal static bool FailedSettlements(RunRecord record, AttemptId attempt) =>
        record.Claims.Keys.Where(launch => launch.Attempt == attempt && record.Settling(launch)).All(launch =>
            record.Dispositions.Values.Any(disposed => disposed.Launch == launch && disposed.Disposition is CaptureDisposition.Failed));

    internal static bool CaptureForAnotherId(RunRecord record, LaunchKey launch, CaptureId capture) =>
        record.Captures.Any(pair => pair.Key != capture && pair.Value.Any(observation => observation.Launch == launch)) ||
        record.Dispositions.Any(pair => pair.Key != capture && pair.Value.Launch == launch);
}

internal static class CaptureComparison
{
    internal static bool Index(CaptureObservation first, CaptureObservation second) =>
        first.Index?.Content == second.Index?.Content && first.Index?.ByteLength == second.Index?.ByteLength &&
        first.IndexTree == second.IndexTree;

    internal static bool Artifacts(CaptureObservation first, CaptureObservation second) =>
        first.Artifacts.OrderBy(file => file.Name, StringComparer.Ordinal).Select(file => (file.Name, file.Content, file.ByteLength))
            .SequenceEqual(second.Artifacts.OrderBy(file => file.Name, StringComparer.Ordinal)
                .Select(file => (file.Name, file.Content, file.ByteLength)));

    internal static bool Content(CaptureObservation first, CaptureObservation second) =>
        RunReducer.Same(first.Recipe, second.Recipe) && first.Candidate == second.Candidate && Index(first, second) &&
        first.Report == second.Report && Artifacts(first, second);

    internal static bool Matches(CaptureObservation first, CaptureObservation second, RunEvent.RootExitObserved root) =>
        first.Tip == root.Tip && second.Tip == root.Tip && first.Head == root.Head && second.Head == root.Head &&
        first.UnexplainedRefs.IsEmpty && second.UnexplainedRefs.IsEmpty && Content(first, second);
}

internal static partial class RunValidation
{
    private static bool Capture(CaptureObservation observation) => observation.Capture.Value != Guid.Empty &&
        (!observation.Recovery || observation.Ordinal == 2) && Key(observation.Launch) && Checkpoint(observation.Log) && observation.Completed >= observation.Started &&
        Recipe(observation.Recipe) && Revision.IsCommit(observation.Candidate.Hex) &&
        (observation.Tip is null || Revision.IsCommit(observation.Tip.Value.Hex)) &&
        (observation.Head is null || !string.IsNullOrWhiteSpace(observation.Head)) &&
        (observation.Index is null || Evidence(observation.Index) &&
            observation.Index.RelativePath == RunStorage.CapturePath(observation.Capture, observation.Ordinal, "index")) &&
        (observation.IndexTree is null || Revision.IsCommit(observation.IndexTree.Value.Hex)) &&
        !observation.Artifacts.IsDefault && observation.Artifacts.All(file => Artifact(file) &&
            file.StoredPath == RunStorage.CapturePath(observation.Capture, observation.Ordinal, "artifacts/" + file.Name)) &&
        observation.Artifacts.Select(file => file.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == observation.Artifacts.Length &&
        Evidence(observation.SharedRefs) &&
        observation.SharedRefs.RelativePath == RunStorage.CapturePath(observation.Capture, observation.Ordinal, "refs.json") &&
        !observation.UnexplainedRefs.IsDefault && observation.UnexplainedRefs.All(Reference);

    private static bool Disposition(CaptureDisposition disposition) => disposition switch
    {
        CaptureDisposition.Matched => true,
        CaptureDisposition.Diverged diverged => Enum.IsDefined(diverged.Problem) && diverged.Detail is not null &&
            !diverged.Paths.IsDefault && diverged.Paths.All(Path) && !diverged.Refs.IsDefault &&
            diverged.Refs.All(reference => reference == "HEAD" || Reference(reference)),
        CaptureDisposition.Failed failed => Enum.IsDefined(failed.Problem) && failed.Detail is not null &&
            !failed.Evidence.IsDefault && failed.Evidence.All(Evidence),
        _ => false,
    };
}
