using System.Diagnostics;

namespace IDevelop.TestSupport;

/// <summary>Child processes write executables so concurrent forks cannot inherit a writable handle and cause ETXTBSY.</summary>
internal static class Executable
{
    public static void Write(string path, string contents)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, contents);
            return;
        }

        Run("/bin/sh", "-c", "printf '%s' \"$1\" > \"$2\"", "sh", contents, path);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public static void Copy(string source, string path)
    {
        if (OperatingSystem.IsWindows())
        {
            File.Copy(source, path, overwrite: true);
            return;
        }

        Run("/bin/cp", "-f", source, path);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void Run(string command, params string[] arguments)
    {
        var start = new ProcessStartInfo(command) { UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {command}.");
        if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{command} did not exit.");
        }

        if (process.ExitCode != 0) throw new InvalidOperationException($"{command} exited with code {process.ExitCode}.");
    }
}
