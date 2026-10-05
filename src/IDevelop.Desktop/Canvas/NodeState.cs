using IDevelop.Execution;

namespace IDevelop.Desktop.Canvas;

/// <summary>Where a node's run stands, which its ring, tint, glyph, and subtitle show.</summary>
public enum NodeState { Idle, NeedsSetup, Running, RunningElsewhere, Stopping, Waiting, InReview, Succeeded, Failed, Interrupted, Cancelled }

/// <summary>What the node is to another node, drawn over its state: a review's subject, or a planner with an open proposal.</summary>
public enum NodeRole { None, UnderReview, Proposing }

public static class NodeStates
{
    /// <summary>A run that goes on comes first, then an ending that needs the person, then a missing setting.</summary>
    /// <param name="runsElsewhere">Another window started the running attempt.</param>
    /// <param name="problem">Why the node cannot start now, or null.</param>
    public static NodeState Of(AttemptRecord? attempt, bool runsElsewhere, StartProblem? problem)
    {
        var needsSetup = problem is not null && IsSetup(problem);
        return attempt?.Status switch
        {
            AttemptStatus.Running => attempt.Stopping ? NodeState.Stopping : runsElsewhere ? NodeState.RunningElsewhere : NodeState.Running,
            AttemptStatus.WaitingForInput => NodeState.Waiting,
            AttemptStatus.InReview => NodeState.InReview,
            AttemptStatus.Failed => NodeState.Failed,
            AttemptStatus.Interrupted => NodeState.Interrupted,
            AttemptStatus.Succeeded => needsSetup ? NodeState.NeedsSetup : NodeState.Succeeded,
            AttemptStatus.Cancelled => needsSetup ? NodeState.NeedsSetup : NodeState.Cancelled,
            null => needsSetup ? NodeState.NeedsSetup : NodeState.Idle,
        };
    }

    /// <summary>An open proposal outranks a review in progress.</summary>
    public static NodeRole RoleOf(StartProblem? problem, bool hasOpenProposal) =>
        hasOpenProposal ? NodeRole.Proposing : problem is StartProblem.UnderReview ? NodeRole.UnderReview : NodeRole.None;

    /// <summary>A setting the person must give before the node can start, as opposed to a wait for something else.</summary>
    public static bool IsSetup(StartProblem problem) => problem is StartProblem.NoAgent or StartProblem.FieldMissing
        or StartProblem.NoModel or StartProblem.ModelNotOffered or StartProblem.ModelUnready or StartProblem.ReasoningNotOffered
        or StartProblem.NoReadOnlyMode or StartProblem.NoSubject or StartProblem.ClientMissing or StartProblem.ClientUnready;
}
