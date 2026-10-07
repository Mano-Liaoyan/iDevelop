using IDevelop.Execution;
using IDevelop.Workflows;

namespace IDevelop.Core.Tests.Runs;

internal static class RunStoreExtensions
{
    public static RunDecision Reserve(this RunStore store, WorkflowId workflow, RunId run, OperationId operation, TaskId task,
        RevisionId revision, AttemptCause cause)
    {
        var decision = store.Plan(workflow, run, operation, task, revision, cause);
        if (decision is RunDecision.Rejected)
        {
            return decision;
        }
        var record = decision switch
        {
            RunDecision.Recorded recorded => recorded.Record,
            RunDecision.Existing existing => existing.Record,
            _ => throw new InvalidOperationException("Unexpected plan decision."),
        };
        var planned = decision switch
        {
            RunDecision.Recorded recorded => recorded.Event,
            RunDecision.Existing existing => existing.Event,
            _ => throw new InvalidOperationException("Unexpected plan decision."),
        };
        var preparation = ((RunEvent.Planned)planned).Plan;
        var plan = record.Plans.Single(pair => RunReducer.Same(pair.Value, preparation)).Key;
        return store.Reserve(workflow, run, OperationIds.Derive(operation, "reserve"), plan);
    }
}
