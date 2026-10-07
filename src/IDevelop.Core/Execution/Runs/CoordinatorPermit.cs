using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

internal sealed class CoordinatorPermit : IDisposable
{
    private readonly object _gate = new();
    private FileStream? _handle;

    private CoordinatorPermit(string project, WorkflowId workflow, RunId run, FileStream handle)
    {
        Project = Path.GetFullPath(project);
        Workflow = workflow;
        Run = run;
        _handle = handle;
    }

    public string Project { get; }

    public WorkflowId Workflow { get; }

    public RunId Run { get; }

    public bool Held => Volatile.Read(ref _handle) is not null;

    internal static CoordinatorPermit Open(string project, WorkflowId workflow, RunId run, string folder) =>
        new(project, workflow, run,
            new FileStream(Path.Combine(folder, "control.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));

    public LeaseTake TakeTask(TaskId task)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(!Held, this);
            return TaskLease.TryTake(Project, task, this) is { } lease ? new LeaseTake.Taken(lease) : new LeaseTake.Busy();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            Interlocked.Exchange(ref _handle, null)?.Dispose();
        }
    }
}

internal abstract record ControlTake
{
    private ControlTake() { }

    internal sealed record Owned(CoordinatorPermit Permit, ImmutableArray<LaunchKey> Fenced) : ControlTake;

    internal sealed record Busy : ControlTake;

    internal sealed record Rejected(RunRejection Reason) : ControlTake;
}
