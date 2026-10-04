using System.ComponentModel;
using System.Diagnostics;

namespace IDevelop.Execution;

/// <summary>A process id and its start time. After a crash, the pair tells the client apart from a process that reused its id.</summary>
internal readonly record struct ProcessIdentity(int Id, DateTimeOffset StartedAt);

internal enum ProcessMatch { Gone, Same, Reused }

internal static class ProcessCheck
{
    // Linux derives a start time from the boot time and clock ticks, so two reads of one process can differ slightly.
    private static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(1);

    public static ProcessIdentity Identify(Process process)
    {
        try
        {
            return new ProcessIdentity(process.Id, new DateTimeOffset(process.StartTime.ToUniversalTime()));
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            // It already exited, so the identity only has to tell it apart from a later process with the same id.
            return new ProcessIdentity(process.Id, DateTimeOffset.UtcNow);
        }
    }

    public static ProcessMatch Match(ProcessIdentity identity)
    {
        using var process = Find(identity.Id);
        return process is null ? ProcessMatch.Gone : Compare(process, identity);
    }

    /// <summary>
    /// Stops the tree only if the process still matches at the moment of the kill. It does not wait for the exit, because
    /// opening a project calls it on the UI thread.
    /// </summary>
    public static void KillTree(ProcessIdentity identity)
    {
        using var process = Find(identity.Id);
        if (process is null || Compare(process, identity) != ProcessMatch.Same)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or AggregateException)
        {
        }
    }

    private static Process? Find(int id)
    {
        try
        {
            return Process.GetProcessById(id);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static ProcessMatch Compare(Process process, ProcessIdentity identity)
    {
        try
        {
            if (process.HasExited)
            {
                return ProcessMatch.Gone;
            }

            var started = new DateTimeOffset(process.StartTime.ToUniversalTime());
            return (started - identity.StartedAt).Duration() <= StartTolerance ? ProcessMatch.Same : ProcessMatch.Reused;
        }
        catch (InvalidOperationException)
        {
            return ProcessMatch.Gone;
        }
        catch (Win32Exception)
        {
            // Another user's process: it cannot be proved to be the client, so it is left alone.
            return ProcessMatch.Reused;
        }
    }
}
