using System.Diagnostics;

namespace IDevelop.TestSupport;

internal static class Processes
{
    public static void AssertGone(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            Assert.True(process.WaitForExit(TimeSpan.FromSeconds(60)), $"process {pid} is still running");
        }
        catch (ArgumentException)
        {
        }
    }
}
