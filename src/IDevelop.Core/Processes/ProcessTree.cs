using System.ComponentModel;
using System.Diagnostics;

namespace IDevelop.Processes;

internal static class ProcessTree
{
    /// <summary>Stops the process and each process it started. Returns the failure, or null when there is none.</summary>
    public static string? Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            return null;
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or AggregateException)
        {
            return e.Message;
        }
    }

    public static void KillQuietly(Process process) => Kill(process);
}
