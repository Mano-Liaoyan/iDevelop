using IDevelop.Execution;
using IDevelop.Projects;

namespace IDevelop.Engine;

/// <summary>
/// One window per project: the window that opens a project holds an operating-system lock on <c>.idp/window.lock</c> until
/// it closes the project, so a second window shows that the project is already open in another window.
/// </summary>
public sealed class ProjectLock : IDisposable
{
    private readonly HeldFile _file;

    private ProjectLock(HeldFile file) => _file = file;

    /// <summary>The lock, or null when another window holds it.</summary>
    public static ProjectLock? TryTake(string projectFolder)
    {
        var file = HeldFile.TryOpen(Path.Combine(projectFolder, DataFolder.Name, "window.lock"));
        DataFolder.EnsureGitIgnore(projectFolder);
        return file is null ? null : new(file);
    }

    public void Dispose() => _file.Dispose();
}
