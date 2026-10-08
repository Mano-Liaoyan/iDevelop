using System.Diagnostics;

namespace IDevelop.Core.Tests.Runs;

internal sealed class Racer : IDisposable
{
    private static readonly string Dll = Path.Combine(AppContext.BaseDirectory, "IDevelop.RunRacer.dll");
    private static readonly string Dotnet = Path.GetFullPath(Path.Combine(
        System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..",
        OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));

    private readonly Process _process;

    public int ExitCode => _process.ExitCode;

    public Racer(params string[] args)
    {
        Assert.True(File.Exists(Dll), Dll);
        var start = new ProcessStartInfo(Dotnet)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(Dll);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        _process = Process.Start(start)!;
    }

    public async System.Threading.Tasks.Task<string> Line() =>
        await _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)) ??
        "<eof:" + await _process.StandardError.ReadToEndAsync() + ">";

    public async System.Threading.Tasks.Task Exit() => await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

    public void Kill()
    {
        _process.Kill(entireProcessTree: true);
        _process.WaitForExit(10_000);
    }

    public void Dispose()
    {
        if (!_process.HasExited) Kill();
        _process.Dispose();
    }
}
