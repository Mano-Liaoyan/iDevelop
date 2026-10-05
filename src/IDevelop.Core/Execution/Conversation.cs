using IDevelop.Workflows;

namespace IDevelop.Execution;

public abstract record SendResult
{
    private SendResult() { }

    /// <summary>The message waits for the running turn to end, or stops it, and then resumes the session.</summary>
    public sealed record Queued : SendResult;

    /// <summary>A new attempt resumes the latest attempt's session with the message. Its status is already Failed when the
    /// client could not be launched.</summary>
    public sealed record Continued(AttemptRecord Attempt) : SendResult;

    public sealed record Refused(SendProblem Problem) : SendResult;
}

/// <summary>Why a message cannot go to a task's agent now. Every case is shown instead of sending.</summary>
public abstract record SendProblem
{
    private SendProblem() { }

    public sealed record EmptyMessage : SendProblem;

    /// <summary>The task has no attempt whose session could continue.</summary>
    public sealed record NeverRan : SendProblem;

    /// <summary>The task's latest attempt ended without its client reporting a session.</summary>
    public sealed record NoSession(ClientId Client) : SendProblem;

    /// <summary>The running attempt's client has not reported its session yet.</summary>
    public sealed record NoSessionYet(ClientId Client) : SendProblem;

    /// <summary>The task now names another client than the one whose session its latest attempt holds.</summary>
    public sealed record ClientChanged(ClientId Ran, ClientId Now) : SendProblem;

    /// <summary>This window's run of the task is stopping, or its last turn just ended with nothing to send.</summary>
    public sealed record Ending(string Title) : SendProblem;

    /// <summary>The start check, another window's run, or the attempt log refused the continuation.</summary>
    public sealed record CannotStart(StartProblem Problem) : SendProblem;
}

public abstract record TerminalResult
{
    private TerminalResult() { }

    /// <summary>
    /// The hand-off is on the latest attempt's record. <paramref name="Command"/> is for the person to run, and it changes
    /// into <paramref name="Folder"/> first.
    /// </summary>
    public sealed record HandedOff(string Folder, string Command) : TerminalResult;

    public sealed record Refused(TerminalProblem Problem) : TerminalResult;
}

/// <summary>Why the task's session cannot go to a terminal now.</summary>
public abstract record TerminalProblem
{
    private TerminalProblem() { }

    public sealed record NeverRan : TerminalProblem;

    public sealed record NoSession(ClientId Client) : TerminalProblem;

    /// <summary>A turn of this task runs in this window, or this window is between two of its turns.</summary>
    public sealed record TurnRunning(string Title) : TerminalProblem;

    /// <summary>Another window's run holds the task, or the attempt log could not be written.</summary>
    public sealed record Blocked(StartProblem Problem) : TerminalProblem;
}
