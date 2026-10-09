using IDevelop.Workflows;

namespace IDevelop.Execution;

public sealed partial class ProjectRuns
{
    private RunApprovals Approvals => new(_projectFolder, TurnStore, GitEnvironment, _clients.Current, Latest, MaterializerClock ?? TimeProvider,
        point => Probe?.Invoke(point), (task, turn) => MarkDoneCore(task, turn).Problem);

    /// <summary>
    /// What Run Workflow shows for <paramref name="workflow"/> before anything runs. It records nothing; computing the work
    /// tree's tree leaves only unreferenced loose objects in Git's object database, as <see cref="RunPreflight"/> says.
    /// </summary>
    /// <param name="node">The node whose Run asks for the run, or null for Run Workflow, which runs every root (#90).</param>
    internal RunPreflight Preflight(Workflow workflow, TaskId? node = null) => Approvals.Inspect(workflow, node);

    /// <summary>
    /// The workflow's run that is approved or stopping, which a window shows when it opens the project, or null. A run
    /// record that cannot be read counts as none here; the preflight names it as a gap.
    /// </summary>
    internal RunId? ActiveRunOf(WorkflowId workflow)
    {
        try { return Approvals.ActiveRun(workflow); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

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
            case RunApproval.Refused refused: return new WorkflowStart.Refused(refused.Problem, refused.Detail) { Current = refused.Current };
            case RunApproval.Busy busy: return new WorkflowStart.Busy(busy.Active);
        }
        var approved = (RunApproval.Approved)approval;
        RunOpen open;
        try { open = OpenRun(current.Id, approved.Run); }
        catch (ObjectDisposedException) { return new WorkflowStart.Unopened(approved.Run, new(RunProblem.RunStopped)); }
        if (open is RunOpen.Rejected rejected) return new WorkflowStart.Unopened(approved.Run, rejected.Reason);
        Probe?.Invoke("approval.opened.after");
        var coordinator = ((RunOpen.Opened)open).Coordinator;
        var resume = await coordinator.Resume(coordinator.Address, wait).ConfigureAwait(false);
        return new WorkflowStart.Started(coordinator, approved.Existing, resume);
    }
}
