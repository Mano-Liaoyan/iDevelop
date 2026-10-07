using IDevelop.Projects;
using IDevelop.Workflows;

namespace IDevelop.Execution;

internal sealed class StandaloneLease : IDisposable
{
    private readonly HeldFile _file;
    private StandaloneLease(string project, TaskId task, HeldFile file) { Project = project; Task = task; _file = file; }
    public string Project { get; }
    public TaskId Task { get; }
    public bool Held => _file.Held;
    public static StandaloneLease? TryTake(string projectFolder, TaskId task)
    {
        var project = Path.GetFullPath(projectFolder);
        return HeldFile.TryOpen(LockPath(project, task)) is { } file ? new(project, task, file) : null;
    }
    internal static string LockPath(string project, TaskId task) =>
        Path.Combine(AttemptLog.TaskFolder(DataFolder.Attempts(project), task), "run.lock");
    public void Dispose() => _file.Dispose();
}

