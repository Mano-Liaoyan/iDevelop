using System.Diagnostics;

namespace IDevelop.TestSupport;

/// <summary>
/// The processes a test started, which it stops at its end. A process that outlives a failed test holds the test host's
/// output open, and dotnet test would wait for it.
/// </summary>
internal sealed class Processes : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private readonly List<int> _ids = [];

    public static void AssertGone(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            Assert.True(process.WaitForExit(Patience), $"process {pid} is still running");
        }
        catch (ArgumentException)
        {
        }
    }

    public void Add(int id) => _ids.Add(id);

    /// <summary>Waits until the fake agent has written the pid of the process it started, and adds that process.</summary>
    public async Task<int> PidAsync(string file)
    {
        using var timeout = new CancellationTokenSource(Patience);
        while (true)
        {
            try
            {
                if (File.Exists(file) && int.TryParse(File.ReadAllText(file), out var pid))
                {
                    Add(pid);
                    return pid;
                }
            }
            catch (IOException)
            {
            }

            await Task.Delay(50, timeout.Token);
        }
    }

    public void Dispose()
    {
        foreach (var id in _ids)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                process.Kill(entireProcessTree: true);
                process.WaitForExit(Patience);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
            }
        }
    }
}
