using IDevelop.Workflows;

namespace IDevelop.Execution;

public sealed partial class ProjectRuns
{
    private RunApprovals Approvals => new(_projectFolder, TurnStore, GitEnvironment, _clients.Current, Latest, MaterializerClock ?? TimeProvider,
        point => Probe?.Invoke(point));

    /// <summary>What Run Workflow shows for <paramref name="workflow"/> before anything runs. It changes nothing.</summary>
    internal RunPreflight Preflight(Workflow workflow) => Approvals.Inspect(workflow);

    /// <summary>
    /// Approves the run that <paramref name="confirmation"/> confirms, against <paramref name="current"/>, the workflow as
    /// the document holds it now, then opens it and authorizes its scheduling. Repeating a confirmation, or confirming the
    /// same content again, returns the one run. Cancelling <paramref name="wait"/> does not revoke a recorded approval.
    /// </summary>
    internal async Task<WorkflowStart> StartWorkflow(Workflow current, RunConfirmation confirmation, CancellationToken wait = default)
    {
        var approval = await Task.Run(() => Approvals.Approve(current, confirmation), CancellationToken.None).WaitAsync(wait).ConfigureAwait(false);
        switch (approval)
        {
            case RunApproval.Changed changed: return new WorkflowStart.Changed(changed.Current);
            case RunApproval.Refused refused: return new WorkflowStart.Refused(refused.Problem, refused.Detail);
            case RunApproval.Busy busy: return new WorkflowStart.Busy(busy.Active);
        }
        var approved = (RunApproval.Approved)approval;
        var open = OpenRun(current.Id, approved.Run);
        if (open is RunOpen.Rejected rejected) return new WorkflowStart.Unopened(approved.Run, rejected.Reason);
        Probe?.Invoke("approval.opened.after");
        var coordinator = ((RunOpen.Opened)open).Coordinator;
        var resume = await coordinator.Resume(coordinator.Address, wait).ConfigureAwait(false);
        return new WorkflowStart.Started(coordinator, approved.Existing, resume);
    }
}
