using IDevelop.Workflows;

namespace IDevelop.Execution;

internal static partial class RunReducer
{
    /// <summary>The newest request of an Approval node, or null before its first.</summary>
    internal static GateState? LatestGate(RunRecord record, TaskId task) =>
        record.Gates.Values.Where(gate => gate.Request.Task == task).MaxBy(gate => gate.Request.Sequence);

    /// <summary>
    /// Whether the inputs a request fixed were superseded: a provided result is stale or no longer its task's current one.
    /// Context that arrives later supersedes nothing, as for a reserved attempt. A node takes a newer request only once its
    /// last one is superseded, or once the result of its approval is stale.
    /// </summary>
    internal static bool Superseded(RunRecord record, GateState gate)
    {
        var stale = record.StaleResults;
        var current = record.CurrentResults;
        return record.Inputs[gate.Request.Inputs].Bindings.OfType<InputBinding.Provided>().Any(binding =>
            stale.Contains(binding.Result) || current.GetValueOrDefault(binding.Edge.From)?.Id != binding.Result);
    }

    /// <summary>The request the person can still answer, or that they sent back, while its inputs are current.</summary>
    internal static GateState? LiveGate(RunRecord record, TaskId task) =>
        LatestGate(record, task) is { Decision: null or GateDecision.SentBack } gate && !Superseded(record, gate) ? gate : null;

    /// <summary>A join that an Approval node's request composes from its current inputs' code.</summary>
    private static bool GateJoin(RunRecord record, MaterializationPlan.Join join) =>
        record.Revision.Snapshot.Tasks.TryGetValue(join.Task, out var task) && task.Blueprint.Work is WorkSpec.Person &&
        RunStore.Inputs(record, join.Task, record.Revision.Id, join.Inputs) is { Rejection: null, Inputs: { } capture } &&
        Same(InputMaterial.Sources(record, capture.Bindings), join.Sources);

    private static RunProblem? GateRequestProblem(RunRecord record, RunEvent.GateRequested requested)
    {
        var request = requested.Request;
        var inputs = requested.Inputs;
        if (record.Schema != 3) return RunProblem.UnsupportedSchema;
        if (record.Phase != RunPhase.Approved) return RunProblem.RunStopped;
        if (request.Revision != record.Revision.Id) return RunProblem.RevisionConflict;
        if (!record.Revision.Snapshot.Tasks.TryGetValue(request.Task, out var definition)) return RunProblem.IdentityMismatch;
        if (definition.Blueprint.Work is not WorkSpec.Person) return RunProblem.UnsupportedWork;
        if (!RunScope.InFlow(record).Contains(request.Task)) return RunProblem.NotRequested;
        if (record.Gates.ContainsKey(request.Id) || record.Inputs.ContainsKey(inputs.Id) ||
            record.Gates.Values.Any(gate => gate.Request.Result == request.Result) || record.Results.Any(result => result.Id == request.Result))
            return RunProblem.StartConflict;
        if (inputs.Id != request.Inputs || inputs.Task != request.Task || inputs.Revision != request.Revision) return RunProblem.InputConflict;
        if (request.Sequence != record.Gates.Values.Count(gate => gate.Request.Task == request.Task) + 1) return RunProblem.InvalidData;
        if (LiveGate(record, request.Task) is not null ||
            record.CurrentResults.TryGetValue(request.Task, out var current) && !record.StaleResults.Contains(current.Id))
            return RunProblem.StartConflict;
        if (InputProblem(record, inputs, fresh: true) is { } input) return input.Problem;
        var sources = InputMaterial.Sources(record, inputs.Bindings);
        var join = (inputs.Code as CodeSelection.Joined)?.Join;
        try
        {
            var expected = InputMaterial.Build(record, inputs.Id, request.Task, request.Revision, inputs.Bindings, sources,
                InputMaterial.Review(record.Revision.Snapshot, request.Task, inputs.Bindings), join);
            if (!SameInput(expected, inputs)) return RunProblem.InputConflict;
        }
        catch (ArgumentException) { return RunProblem.InputConflict; }
        if (join is not null && (record.Plans.GetValueOrDefault(join.Operation) is not MaterializationPlan.Join plan ||
            plan.Task != request.Task || plan.Inputs != inputs.Id || !Same(plan.Sources, sources) || plan.Commit != join.Commit ||
            plan.Recipe.Tree != join.Tree || plan.Ref != join.Ref || !ObservedMove(record, join.Operation, plan.Ref, plan.Previous, plan.Commit)))
            return RunProblem.InputConflict;
        return GateForwarding.Artifacts(record, inputs, request.Result).Collision is null ? null : RunProblem.InputConflict;
    }

    /// <summary>An approval's result: the current request's own id and inputs, and exactly what those inputs forward.</summary>
    private static RunProblem? HumanResultProblem(RunRecord record, RunEvent.ResultAccepted accepted, TaskDefinition task, ResultOrigin.Human human)
    {
        var result = accepted.Result;
        if (record.Schema != 3) return RunProblem.UnsupportedSchema;
        if (task.Blueprint.Work is not WorkSpec.Person) return RunProblem.UnsupportedResult;
        if (record.Phase != RunPhase.Approved) return RunProblem.RunStopped;
        if (!record.Gates.TryGetValue(human.Request, out var gate)) return RunProblem.UnknownInput;
        if (gate.Request.Task != result.Task || gate.Request.Revision != result.Revision || gate.Request.Inputs != result.Inputs ||
            gate.Request.Result != result.Id)
            return RunProblem.InputConflict;
        if (gate.Decision is not null || Superseded(record, gate)) return RunProblem.StaleInput;
        return result.Report == GateForwarding.Report(record, accepted.Inputs) ? null : RunProblem.OutcomeMismatch;
    }

    private static (RunRecord Record, RunProblem? Problem) SendBack(RunRecord record, RunEntry entry, RunEvent.GateSentBack sent)
    {
        if (record.Schema != 3) return (record, RunProblem.UnsupportedSchema);
        if (record.Phase != RunPhase.Approved) return (record, RunProblem.RunStopped);
        if (!record.Gates.TryGetValue(sent.Request, out var gate)) return (record, RunProblem.UnknownInput);
        if (gate.Request.Inputs != sent.Inputs) return (record, RunProblem.InputConflict);
        if (gate.Decision is not null || Superseded(record, gate)) return (record, RunProblem.StaleInput);
        return (Decide(record, sent.Request, new GateDecision.SentBack(sent.Reason, entry.Operation)), null);
    }

    private static RunRecord Decide(RunRecord record, GateId gate, GateDecision decision) =>
        record with { Gates = record.Gates.SetItem(gate, record.Gates[gate] with { Decision = decision }) };
}
