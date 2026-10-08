using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>The run a command is meant for. Every command names it, so a window that shows another project or workflow
/// by now cannot reach the wrong run.</summary>
/// <param name="Project">The project folder, as <see cref="Projects.ProjectFolders.OnDisk"/> names it.</param>
internal sealed record RunAddress(string Project, WorkflowId Workflow, RunId Run);

internal abstract record RunOpen
{
    private RunOpen() { }

    /// <summary>The run's coordinator in this window. It controls the run, or only reads it while another window does.</summary>
    internal sealed record Opened(WorkflowRunCoordinator Coordinator) : RunOpen;

    internal sealed record Rejected(RunRejection Reason) : RunOpen;
}

internal abstract record RunCommand
{
    private RunCommand() { }

    internal sealed record Accepted : RunCommand;

    /// <summary>Another window controls the run. Nothing was recorded.</summary>
    internal sealed record Unavailable(string Message) : RunCommand;

    internal sealed record Refused(RunRejection Reason) : RunCommand;
}

internal enum RunStatus { Paused, Running, Waiting, NeedsAttention, Stopping, Stopped, Completed, Failed, Abandoned, Elsewhere }

/// <summary>Where a task of the run stands. Only <see cref="Done"/> hands on to its dependents.</summary>
internal enum TaskState
{
    /// <summary>A dependency predecessor has not handed on yet.</summary>
    Pending,

    /// <summary>It starts when a client slot is free, or after Resume while the run is paused.</summary>
    Ready,

    /// <summary>Its turn is being prepared, checked, claimed, and launched. It holds the run's client slot.</summary>
    Starting,

    /// <summary>Its client's root process runs. It holds the run's client slot.</summary>
    Running,

    /// <summary>Its root exited, and settlement, publication, or the turn's disposition is not finished.</summary>
    Settling,

    /// <summary>Its attempt rests for the person: a question, a review, or queued text.</summary>
    Waiting,

    /// <summary>It has a current accepted result.</summary>
    Done,

    /// <summary>Its attempt ended without a result: failed, cancelled, interrupted, or closed by recovery.</summary>
    Failed,

    /// <summary>A recorded block holds it until a repair or recheck resolves the block.</summary>
    Blocked,

    /// <summary>Its turn's execution is unresolved. Nothing launches it again; a person recovers it.</summary>
    Uncertain,

    /// <summary>Its start was refused without a recorded block.</summary>
    Refused,

    /// <summary>The coordinator does not run this kind of node yet.</summary>
    Unsupported,
}

internal sealed record TaskView(TaskId Task, TaskState State)
{
    /// <summary>The task's newest attempt in this run.</summary>
    public AttemptId? Attempt { get; init; }

    public ResultId? Result { get; init; }

    /// <summary>How the newest attempt ended, once it is closed.</summary>
    public AttemptEnd? End { get; init; }

    /// <summary>The resting attempt's status while <see cref="TaskState.Waiting"/>.</summary>
    public AttemptStatus? Status { get; init; }

    /// <summary>The first unresolved block on the task, while <see cref="TaskState.Blocked"/>.</summary>
    public MaterializationBlock? Block { get; init; }

    /// <summary>Why the start was refused, or why the turn is unresolved.</summary>
    public RunRejection? Refusal { get; init; }

    /// <summary>The client problem behind a refused start, when there is one.</summary>
    public StartProblem? Problem { get; init; }

    public UnresolvedReason? Unresolved { get; init; }

    /// <summary>The tasks that hold back a <see cref="TaskState.Pending"/> task.</summary>
    public ImmutableSortedSet<TaskId> HeldBy { get; init; } = [];
}

/// <summary>What one window knows of a run: the journal and attempt logs, plus the work this window has in flight.</summary>
/// <param name="Slots">Client roots this window is starting or running for the run. At most one.</param>
internal sealed record RunView(RunAddress Address, RunPhase Phase, RunStatus Status, bool Controlled, bool Resumed, int Slots,
    ImmutableSortedDictionary<TaskId, TaskView> Tasks)
{
    /// <summary>Why the journal or a command could not be read or run, for the person.</summary>
    public string? Problem { get; init; }

    /// <summary>Whether this window released the settled run's retention pins.</summary>
    public bool PinsReleased { get; init; }

    public string Label => Status switch
    {
        RunStatus.Paused => "Paused",
        RunStatus.Running => "Running",
        RunStatus.Waiting => "Waiting",
        RunStatus.NeedsAttention => "Needs attention",
        RunStatus.Stopping => "Stopping",
        RunStatus.Stopped => "Stopped",
        RunStatus.Completed => "Completed",
        RunStatus.Failed => "Failed",
        RunStatus.Abandoned => "Abandoned",
        RunStatus.Elsewhere => "Controlled by another window",
        _ => throw new InvalidOperationException(),
    };
}
