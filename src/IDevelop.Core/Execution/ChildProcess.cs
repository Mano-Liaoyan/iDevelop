using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace IDevelop.Execution;

/// <summary>A process id and its start time. After a crash, the pair tells the client apart from a process that reused its id.</summary>
internal readonly record struct ProcessIdentity(int Id, DateTimeOffset StartedAt);

/// <summary>The message is for the user.</summary>
internal sealed class LaunchException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// A started command with redirected pipes. It knows nothing about any client: runs and probes both use it.
/// Text crosses the pipes as UTF-8 without a byte order mark on every platform. On Windows the process and everything
/// it starts share a job, so disposing it, or iDevelop exiting, stops whatever is still running, unless
/// <see cref="LeaveDescendantsRunning"/> came first.
/// </summary>
internal sealed class ChildProcess : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Lock _gate = new();
    private readonly Process _process;
    private readonly ProcessJob? _job;
    private bool _disposed;

    private ChildProcess(Process process)
    {
        _process = process;
        _job = OperatingSystem.IsWindows() ? ProcessJob.Assign(process) : null;
        Identity = ProcessCheck.Identify(process);
    }

    public ProcessIdentity Identity { get; }

    /// <exception cref="LaunchException">The command did not start, or a batch shim was given an unsafe argument.</exception>
    public static ChildProcess Start(ResolvedCommand command, IReadOnlyList<string> arguments, string workingDirectory)
    {
        if (command.UnsafeArgument(arguments) is { } argument)
        {
            throw new LaunchException(
                $"{command.Path} runs through cmd.exe, which could misread the argument {argument}, so iDevelop did not start it.");
        }

        var start = new ProcessStartInfo(command.Path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        foreach (var value in arguments)
        {
            start.ArgumentList.Add(value);
        }

        if (command.SearchPath is { } searchPath)
        {
            start.Environment["PATH"] = searchPath;
        }

        if (OperatingSystem.IsWindows())
        {
            // cmd.exe, which runs a .cmd client such as an npm shim, otherwise looks for a bare command such as node in
            // the current folder before PATH. For a run, that folder is the project. Every command the agent runs
            // inherits the variable too, so a step that calls a bare build.cmd from the project folder needs .\build.cmd
            // under a run. That failure names the command, while a command planted in a shared repository would run
            // unnoticed.
            start.Environment["NoDefaultCurrentDirectoryInExePath"] = "1";
        }

        try
        {
            return new ChildProcess(Process.Start(start) ?? throw new LaunchException($"{command.Path} did not start."));
        }
        catch (Win32Exception e)
        {
            throw new LaunchException($"{command.Path} did not start: {e.Message}", e);
        }
    }

    /// <summary>A client that exits before it reads its input is not an error here. Its exit tells the story.</summary>
    public async Task WriteStdinAsync(string text, bool close)
    {
        try
        {
            await _process.StandardInput.WriteAsync(text);
            await _process.StandardInput.FlushAsync();
            if (close)
            {
                _process.StandardInput.Close();
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
        }
    }

    /// <summary>Completes at the end of stdout. Lines can be large, so there is no length cap.</summary>
    public Task ReadStdoutAsync(Action<string> onLine) => ReadLinesAsync(_process.StandardOutput, onLine);

    public Task ReadStderrAsync(Action<string> onLine) => ReadLinesAsync(_process.StandardError, onLine);

    public async Task<int> WaitForExitAsync()
    {
        await _process.WaitForExitAsync();
        return _process.ExitCode;
    }

    /// <summary>
    /// Stops the process and every process it started. A process that already exited is not an error. After
    /// <see cref="Dispose"/> it does nothing, so it never reaches a process that reused the id.
    /// </summary>
    public void StopTree()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _job?.Terminate();
            try
            {
                // The job holds the process only from just after its start, and Linux and macOS have no job.
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception or AggregateException)
            {
                // Already exited, or a descendant could not be stopped. Neither is the caller's to handle.
            }
        }
    }

    /// <summary>Lets the processes it started outlive <see cref="Dispose"/> and iDevelop.</summary>
    public void LeaveDescendantsRunning()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _job?.KeepProcessesOnClose();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _disposed = true;
                _job?.Dispose();
                _process.Dispose();
            }
        }
    }

    private static async Task ReadLinesAsync(StreamReader reader, Action<string> onLine)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                onLine(line);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
        }
    }
}

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

internal static class Probes
{
    // After the probe exits, a process it started may still hold the pipes open.
    private static readonly TimeSpan OutputGrace = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Runs a probe in the temporary folder, so it never runs inside a project. On timeout it stops the tree and returns
    /// what it read with TimedOut set. A probe with DoneWhen gets the end of its input at its answer, and is stopped only
    /// if it has not exited 5 seconds later.
    /// </summary>
    /// <exception cref="LaunchException">The command did not start.</exception>
    public static async Task<ProbeOutput> RunAsync(ResolvedCommand command, Probe probe)
    {
        using var child = ChildProcess.Start(command, probe.Arguments, Path.GetTempPath());
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Pi's RPC mode may stop at the end of its input before it answers, so a probe that waits for an answer keeps stdin open.
        var input = child.WriteStdinAsync(probe.Stdin ?? "", close: probe.DoneWhen is null);
        var output = child.ReadStdoutAsync(line =>
        {
            lock (stdout)
            {
                stdout.Append(line).Append('\n');
            }

            if (probe.DoneWhen?.Invoke(line) == true)
            {
                answered.TrySetResult();
            }
        });
        var errors = child.ReadStderrAsync(line =>
        {
            lock (stderr)
            {
                stderr.Append(line).Append('\n');
            }
        });

        using var limit = new CancellationTokenSource(probe.Timeout);
        var exit = child.WaitForExitAsync();
        var first = await Task.WhenAny(exit, answered.Task, Task.Delay(Timeout.Infinite, limit.Token));
        var ended = first == exit;
        if (first == answered.Task)
        {
            // Pi's RPC documentation asks for this orderly shutdown. Stopping Pi's tree instead made the next pi command
            // take about 30 seconds to exit, which timed out its sign-in checks.
            await input;
            await child.WriteStdinAsync("", close: true);
            ended = await Task.WhenAny(exit, Task.Delay(ShutdownGrace)) == exit;
        }

        if (!ended)
        {
            child.StopTree();
        }

        await Task.WhenAny(Task.WhenAll(output, errors), Task.Delay(OutputGrace));
        int? exitCode = ended ? await exit : null;
        return new ProbeOutput(exitCode, Snapshot(stdout), Snapshot(stderr), TimedOut: first != exit && first != answered.Task);
    }

    private static string Snapshot(StringBuilder text)
    {
        lock (text)
        {
            return text.ToString();
        }
    }
}
