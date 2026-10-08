using System.Diagnostics;

namespace IDevelop.Desktop.Tests;

/// <summary>Runs the run racer, which takes a run's control in its own process and exits at a named step, as a crash ends the app.</summary>
internal static class RunRacerProcess
{
    private static readonly string Dll = Path.Combine(AppContext.BaseDirectory, "IDevelop.RunRacer.dll");

    private static readonly string Dotnet = Path.GetFullPath(Path.Combine(
        System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..",
        OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));

    /// <summary>Runs the racer with <paramref name="args"/> and <paramref name="environment"/> on top of this process's own, and waits up to a minute.</summary>
    public static (int Exit, string Output) Run(IReadOnlyDictionary<string, string> environment, params string[] args)
    {
        Assert.True(File.Exists(Dll), Dll);
        var start = new ProcessStartInfo(Dotnet) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        start.ArgumentList.Add(Dll);
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var racer = Process.Start(start)!;
        var errors = racer.StandardError.ReadToEndAsync();
        var output = racer.StandardOutput.ReadToEndAsync();
        if (!racer.WaitForExit(TimeSpan.FromMinutes(1)))
        {
            racer.Kill(entireProcessTree: true);
            Assert.Fail("The racer did not exit.");
        }

        return (racer.ExitCode, output.Result + errors.Result);
    }
}
