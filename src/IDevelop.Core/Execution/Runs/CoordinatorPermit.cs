using System.Collections.Immutable;
using IDevelop.Projects;
using IDevelop.Workflows;

namespace IDevelop.Execution;

internal sealed class CoordinatorPermit : IDisposable
{
    private readonly HeldFile _file;

    private CoordinatorPermit(string project, WorkflowId workflow, RunId run, HeldFile file)
    {
        Project = Path.GetFullPath(project);
        Workflow = workflow;
        Run = run;
        _file = file;
    }

    public string Project { get; }

    public WorkflowId Workflow { get; }

    public RunId Run { get; }

    public bool Held => _file.Held;

    internal IDisposable? Use() => _file.Use();

    internal static ControlTake Acquire(RunStore store, WorkflowId workflow, RunId run)
    {
        var read = store.Read(workflow, run);
        if (read is RunRead.Rejected rejected) return new ControlTake.Rejected(rejected.Reason);
        if (((RunRead.Loaded)read).Record.Schema != 3) return new ControlTake.Rejected(new(RunProblem.UnsupportedSchema));
        try
        {
            var path = Path.Combine(DataFolder.Runs(store.Project), workflow.ToString(), run.ToString(), "control.lock");
            var file = HeldFile.TryOpen(path);
            if (file is null) return new ControlTake.Busy();
            var permit = new CoordinatorPermit(store.Project, workflow, run, file);
            try
            {
                var take = store.Fence(permit);
                if (take is not ControlTake.Owned) permit.Dispose();
                return take;
            }
            catch { permit.Dispose(); throw; }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new ControlTake.Rejected(new(RunProblem.StorageUnavailable)); }
    }

    public LeaseTake TakeTask(TaskId task)
    {
        using var scope = Use();
        if (scope is null) return new LeaseTake.Busy();
        return RunLease.TryTake(this, task) is { } lease ? new LeaseTake.Taken(lease) : new LeaseTake.Busy();
    }

    public void Dispose() => _file.Dispose();
}

internal abstract record ControlTake
{
    private ControlTake() { }

    internal sealed record Owned(CoordinatorPermit Permit, ImmutableArray<LaunchKey> Fenced) : ControlTake;

    internal sealed record Busy : ControlTake;

    internal sealed record Rejected(RunRejection Reason) : ControlTake;
}
