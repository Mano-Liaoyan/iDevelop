using IDevelop.Projects;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>
/// Holds <c>.idp/attempts/&lt;task-id&gt;/run.lock</c> exclusively without waiting. The file is never deleted, so all
/// owners lock the same file, and the operating system releases the handle when its holder dies.
/// Lock order is coordinator permit, task lease, repository mutation lock, then journal write lock. Never acquire an
/// earlier lock while holding a later one.
/// </summary>
internal sealed class TaskLease : IDisposable
{
    private FileStream? _handle;

    private TaskLease(string project, TaskId task, CoordinatorPermit? permit, FileStream handle)
    {
        Project = project;
        Task = task;
        Permit = permit;
        _handle = handle;
    }

    public string Project { get; }

    public TaskId Task { get; }

    public CoordinatorPermit? Permit { get; }

    public bool Held => Volatile.Read(ref _handle) is not null;

    public static TaskLease? TryTake(string projectFolder, TaskId task) => TryTake(projectFolder, task, null);

    internal static TaskLease? TryTake(string projectFolder, TaskId task, CoordinatorPermit? permit)
    {
        var project = Path.GetFullPath(projectFolder);
        var folder = Directory.CreateDirectory(AttemptLog.TaskFolder(DataFolder.Attempts(project), task)).FullName;
        try
        {
            return new(project, task, permit,
                new FileStream(Path.Combine(folder, "run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public TaskLease Transfer()
    {
        var handle = Interlocked.Exchange(ref _handle, null) ?? throw new InvalidOperationException("The task lease is not held.");
        return new(Project, Task, Permit, handle);
    }

    public void Dispose() => Interlocked.Exchange(ref _handle, null)?.Dispose();
}

internal abstract record LeaseTake
{
    private LeaseTake() { }

    internal sealed record Taken(TaskLease Lease) : LeaseTake;

    internal sealed record Busy : LeaseTake;
}
