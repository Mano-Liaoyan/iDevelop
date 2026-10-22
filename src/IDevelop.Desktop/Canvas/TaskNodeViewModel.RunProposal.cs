using IDevelop.Execution;
using IDevelop.Nodes;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// A planner's proposal in the canvas's run (#84). While the run shows the planner, its Proposal section and ghost cards
/// show the proposal of its newest attempt in the run, which Accept amends the run with.
/// </summary>
public sealed partial class TaskNodeViewModel
{
    private RunProposal? _runProposal;
    private int _runProposalReads;

    /// <summary>The node proposes graph edits, as a Plan or Architect node does.</summary>
    private bool IsPlanner => _task.Blueprint.Work is WorkSpec.Agent { Proposes: true };

    /// <summary>
    /// Reads the planner's proposal in the canvas's run off the window's thread, after each change of the run, since an
    /// amendment of another planner moves the revision the proposal is accepted against. A read that a newer one overtook
    /// is dropped.
    /// </summary>
    internal async Task ReadRunProposalAsync()
    {
        var read = ++_runProposalReads;
        if (!IsPlanner || RunTask is null || _canvas.Run is not { } run)
        {
            ShowRunProposal(null);
            return;
        }

        var (coordinator, task) = (run.Coordinator, Id);
        var proposal = await Task.Run(() => coordinator.PlannerProposal(task));
        if (read == _runProposalReads)
        {
            ShowRunProposal(proposal);
        }
    }

    private void ShowRunProposal(RunProposal? proposal)
    {
        _runProposal = proposal;
        var shown = Proposal;
        ShowProposal();
        if (!ReferenceEquals(shown, Proposal))
        {
            _canvas.ShowGhosts();
        }
    }

    /// <summary>
    /// The run's proposal while the run shows the planner, and whether it is closed: an amendment took it, or the person
    /// marked the planner done without accepting it, as Dismiss does, which a reopened window remembers so; else null.
    /// </summary>
    private (ProposalRead Read, AttemptId Attempt, RunSource Source, bool Closed)? RunProposalShown =>
        RunTask is not null && _runProposal is { } proposal && _canvas.Run is { } run
            ? (proposal.Read, proposal.Log.Id, new RunSource(run.Address, proposal.Revision, new(proposal.Log.Id, proposal.Log.Turns.Count),
                proposal.Log is { Status: AttemptStatus.WaitingForInput, Pending: Pending.Proposal }),
                proposal.Accepted || proposal.Log.Status == AttemptStatus.Succeeded)
            : null;
}
