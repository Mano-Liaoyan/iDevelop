using System.Collections.Immutable;

namespace IDevelop.Execution;

/// <summary>
/// The person's confirmation of <paramref name="Preview"/> with the base they chose. <paramref name="Command"/> identifies
/// the confirmation: repeating it returns the run it approved.
/// </summary>
internal sealed record RunConfirmation(RunPreflight Preview, BaseChoice Choice, OperationId Command)
{
    /// <summary>The reports the preview offered that the person includes: planners from <see cref="RunPreflight.Planners"/>
    /// and other reports from <see cref="RunPreflight.Reusable"/>, at most one per task.</summary>
    public ImmutableArray<ReportInclusion> Include { get; init; } = [];
}

internal enum ApprovalProblem
{
    /// <summary>The preview has gaps, or offers no such base.</summary>
    NotConfirmable,

    /// <summary>The confirmation carries no command ID, so a repeat could not be told from a new confirmation.</summary>
    ConfirmationRequired,

    /// <summary>The run journals or the approval intent could not be read or written. Nothing was approved.</summary>
    StorageUnavailable,

    /// <summary>Another confirmation of the workflow held the approval lock too long.</summary>
    ApprovalBusy,

    /// <summary>Git failed while building the snapshot.</summary>
    GitFailed,

    /// <summary>
    /// A report the person included cannot be: the preview did not offer it on this base, or its validation failed at
    /// approval. Nothing was approved. The detail names the <see cref="RunProblem"/>. Running the planner again takes a
    /// confirmation without it.
    /// </summary>
    InclusionRefused,
}

internal abstract record RunApproval
{
    private RunApproval() { }

    /// <summary>The one run this confirmation approved. <paramref name="Existing"/> when an earlier, matching confirmation approved it.</summary>
    internal sealed record Approved(RunId Run, RunBase Base, bool Existing) : RunApproval;

    /// <summary>The preview no longer shows what would be approved. Nothing was approved; <paramref name="Current"/> replaces it.</summary>
    internal sealed record Changed(RunPreflight Current) : RunApproval;

    internal sealed record Refused(ApprovalProblem Problem, string Detail) : RunApproval
    {
        /// <summary>The preflight as it is now, when one was built, so the person sees the gap that refused it.</summary>
        public RunPreflight? Current { get; init; }
    }

    /// <summary>Another run of the workflow, of different content, is still active. Nothing was approved.</summary>
    internal sealed record Busy(RunId Active) : RunApproval;
}

internal abstract record WorkflowStart
{
    private WorkflowStart() { }

    /// <summary>
    /// The approved run's coordinator in this window, and the outcome of resuming it. A coordinator that another window
    /// controls only reads the run, and resuming it is <see cref="RunCommand.Unavailable"/>.
    /// </summary>
    internal sealed record Started(WorkflowRunCoordinator Coordinator, bool Existing, RunCommand Resume) : WorkflowStart;

    internal sealed record Changed(RunPreflight Current) : WorkflowStart;

    internal sealed record Refused(ApprovalProblem Problem, string Detail) : WorkflowStart
    {
        /// <summary>The preflight as it is now, when one was built, so the person sees the gap that refused it.</summary>
        public RunPreflight? Current { get; init; }
    }

    internal sealed record Busy(RunId Active) : WorkflowStart;

    /// <summary>The run was approved, but this window could not open it.</summary>
    internal sealed record Unopened(RunId Run, RunRejection Reason) : WorkflowStart;
}
