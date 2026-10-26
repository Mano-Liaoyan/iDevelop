using IDevelop.Projects;

namespace IDevelop.Engine;

/// <summary>
/// One window per project: the window that opens a project holds an operating-system lock on <c>.idp/window.lock</c> until
/// it closes the project, so a second window shows that the project is already open in another window.
/// </summary>
public sealed class ProjectLock : IDisposable
{
    private readonly FileStream _file;

    private ProjectLock(FileStream file) => _file = file;

    /// <summary>The lock, or null when another window holds it.</summary>
    /// <exception cref="IOException">The lock file cannot be written, such as in a read-only folder.</exception>
    /// <exception cref="UnauthorizedAccessException">The lock file cannot be written.</exception>
    public static ProjectLock? TryTake(string projectFolder)
    {
        var path = Path.Combine(projectFolder, DataFolder.Name, "window.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        FileStream file;
        try
        {
            file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }

        try
        {
            DataFolder.EnsureGitIgnore(projectFolder);
        }
        catch
        {
            // A lock that cannot be kept as Git ignores it is let go, so no window holds the project.
            file.Dispose();
            throw;
        }

        return new(file);
    }

    public void Dispose() => _file.Dispose();
}
