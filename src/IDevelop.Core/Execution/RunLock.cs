using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>
/// <c>.idp/attempts/&lt;task-id&gt;/run.lock</c>, held with FileShare.None for a run's whole life: an exclusive handle on
/// Windows and an exclusive flock on Linux and macOS. The operating system releases it when its holder dies, so holding it
/// is owning the task's run, and an attempt that reads as running while its task's lock is free was left by a crash. The
/// file is never deleted, because then two instances could lock two different files.
/// </summary>
internal sealed class RunLock : IDisposable
{
    private readonly FileStream _handle;

    private RunLock(FileStream handle) => _handle = handle;

    /// <summary>Null when another handle holds it, in this process or another one. Never waits.</summary>
    public static RunLock? TryTake(string attemptsFolder, TaskId task)
    {
        var folder = Directory.CreateDirectory(AttemptLog.TaskFolder(attemptsFolder, task)).FullName;
        try
        {
            return new RunLock(new FileStream(Path.Combine(folder, "run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose() => _handle.Dispose();
}
