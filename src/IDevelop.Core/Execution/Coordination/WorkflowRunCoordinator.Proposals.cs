using IDevelop.Nodes;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>
/// A run's planner's latest proposal as the run holds it: read from the planner's newest attempt in the run with the
/// blueprints of the run's current revision, which an amendment against <paramref name="Revision"/> reads it with too.
/// </summary>
/// <param name="Accepted">An amendment of the run already took this proposal's turn.</param>
internal sealed record RunProposal(AttemptRecord Log, ProposalRead Read, RevisionId Revision, bool Accepted);

/// <summary>What the person's review of a run planner's proposal reads, and how the document follows an amendment (#84).</summary>
internal sealed partial class WorkflowRunCoordinator
{
    /// <summary>
    /// The latest proposal of <paramref name="planner"/>'s newest attempt in this run, or null when it has none or the
    /// journal or the log cannot be read. It reads them off the loop.
    /// </summary>
    public RunProposal? PlannerProposal(TaskId planner)
    {
        if (Record() is not { } record || !RunProjection.LatestAttempts(record).TryGetValue(planner, out var newest) ||
            Log(record.Attempts[newest]) is not { } log)
        {
            return null;
        }

        var snapshot = record.Revision.Snapshot;
        var read = Proposal.Read(log, key => RunPlanning.Find(snapshot, key));
        if (read is ProposalRead.None) return null;
        var accepted = read is ProposalRead.Ready { Proposal: var proposal } && record.Receipts.Values.Any(entry =>
            entry.Event is RunEvent.Amended { Origin: AmendmentOrigin.Planner origin } && origin.Attempt == proposal.Attempt && origin.Turn == proposal.Turn);
        return new(log, read, record.Revision.Id, accepted);
    }

    /// <summary>How <paramref name="document"/> follows the run's recorded amendments, or <see cref="AmendmentProjection.Current"/> when the journal cannot be read.</summary>
    public AmendmentProjection Follow(Workflow document) =>
        Record() is { } record ? AmendmentProjection.Of(document, record) : new AmendmentProjection.Current();
}
