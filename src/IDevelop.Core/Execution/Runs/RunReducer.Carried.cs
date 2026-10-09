using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

internal static partial class RunReducer
{
    /// <summary>The operation of the join a carried result's inputs record, derived from the event that carries it.</summary>
    internal static OperationId CarriedJoinOperation(OperationId carrying, ResultId result) =>
        OperationIds.Derive(carrying, "carry-join/" + result.Value.ToString("D"));

    /// <summary>The result and inputs ids a carried task takes in the event <paramref name="carrying"/>.</summary>
    internal static (ResultId Result, InputId Inputs) CarriedIds(OperationId carrying, TaskId task) =>
        (new(OperationIds.Derive(carrying, "carry/" + task.Value.ToString("D")).Value),
            new(OperationIds.Derive(carrying, "carry-inputs/" + task.Value.ToString("D")).Value));

    /// <summary>The record with <paramref name="carried"/> accepted, as the event that carries it accepts it.</summary>
    internal static RunRecord WithCarried(RunRecord record, IncludedResult carried) => record with
    {
        Results = record.Results.Add(carried.Result),
        Inputs = record.Inputs.Add(carried.Inputs.Id, carried.Inputs),
    };

    /// <summary>
    /// Why a result carried from an earlier run (#90) does not fit <paramref name="record"/>, the run as it is just before
    /// it, or null. The run must have started from a node, and the task must be one it cannot start. Each result the
    /// carried one took must already be the current, non-stale result of this run, so a carried chain comes in the order its
    /// inputs need. Its inputs are what the run's own materialization would build from those results, its code is the
    /// task's own, replayed onto those inputs and kept under the run's carried refs, or what it forwards, and its artifacts
    /// are stored under its own id. Whether the earlier result still counts, and the Git objects, are checked before the
    /// event is written; a replay cannot read them.
    /// </summary>
    /// <param name="carrying">The operation of the approval or request that carries it.</param>
    internal static RunProblem? CarriedProblem(RunRecord record, IncludedResult carried, OperationId carrying)
    {
        var (result, inputs) = (carried.Result, carried.Inputs);
        var snapshot = record.Revision.Snapshot;
        if (record.Requested is null || result.Origin is not ResultOrigin.Carried) return RunProblem.UnsupportedResult;
        if (!snapshot.Tasks.TryGetValue(result.Task, out var task)) return RunProblem.IdentityMismatch;
        if (RunScope.InFlow(record).Contains(result.Task) || record.Results.Any(other => other.Id == result.Id || other.Task == result.Task) ||
            record.Inputs.ContainsKey(inputs.Id) || record.Attempts.Values.Any(attempt => attempt.Task == result.Task) ||
            record.Gates.Values.Any(gate => gate.Request.Task == result.Task))
            return RunProblem.StartConflict;
        if ((result.Id, result.Inputs) != CarriedIds(carrying, result.Task) || result.Revision != record.Revision.Id || inputs.Task != result.Task ||
            inputs.Revision != result.Revision || result.Supersedes is not null)
            return RunProblem.InputConflict;
        if (InputProblem(record, inputs, fresh: false) is { } input) return input.Problem;
        var stale = record.StaleResults;
        var current = record.CurrentResults;
        if (inputs.Bindings.OfType<InputBinding.Provided>().Any(binding => stale.Contains(binding.Result) ||
            current.GetValueOrDefault(binding.Edge.From)?.Id != binding.Result))
            return RunProblem.StaleInput;
        var sources = InputMaterial.Sources(record, inputs.Bindings);
        var join = (inputs.Code as CodeSelection.Joined)?.Join;
        if (join is not null && (join.Operation != CarriedJoinOperation(carrying, result.Id) || join.Ref != RunLayout.CarriedJoin(record.Id, result.Id)))
            return RunProblem.InputConflict;
        try
        {
            var expected = InputMaterial.Build(record, inputs.Id, result.Task, inputs.Revision, inputs.Bindings, sources,
                InputMaterial.Review(snapshot, result.Task, inputs.Bindings), join);
            if (!SameInput(expected, inputs)) return RunProblem.InputConflict;
        }
        catch (ArgumentException)
        {
            return RunProblem.InputConflict;
        }
        CodeOutput? forwarded = inputs.Code is CodeSelection.Single or CodeSelection.Joined ? new CodeOutput.Forwarded(inputs.Id) : null;
        var code = task.Blueprint.Work switch
        {
            WorkSpec.Agent { Access: AgentAccess.Edit } => result.Code is CodeOutput.Produced { Code: var owned } && owned.Owner == result.Task &&
                owned.AttemptBase == inputs.CodeBase && owned.ResultRef == RunLayout.CarriedCode(record.Id, result.Id),
            WorkSpec.Review => inputs.Review is not null && Same(result.Code, forwarded),
            _ => Same(result.Code, forwarded),
        };
        if (!code) return RunProblem.InputConflict;
        if (result.Artifacts.Any(artifact => artifact.StoredPath != RunStorage.ArtifactPath(result.Id, artifact.Name)) ||
            result.Artifacts.Select(artifact => artifact.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Artifacts.Length)
            return RunProblem.InputConflict;
        return null;
    }

    /// <summary>The record with each of <paramref name="carried"/> accepted in order, or the first problem and its task.</summary>
    private static (RunRecord Record, RunProblem? Problem, TaskId? Task) Carry(RunRecord record, ImmutableArray<IncludedResult>? carried,
        OperationId carrying)
    {
        foreach (var item in carried ?? [])
        {
            if (CarriedProblem(record, item, carrying) is { } problem) return (record, problem, item.Result.Task);
            record = WithCarried(record, item);
        }
        return (record, null, null);
    }
}
