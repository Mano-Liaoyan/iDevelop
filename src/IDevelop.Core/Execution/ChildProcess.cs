using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace IDevelop.Execution;

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

    private static readonly TimeSpan OutputGrace = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();
    private readonly Process _process;
    private readonly ProcessJob? _job;
    private Task _stdout = Task.CompletedTask;
    private Task _stderr = Task.CompletedTask;
    private bool _disposed;

    private ChildProcess(Process process)
    {
        _process = process;
        _job = OperatingSystem.IsWindows() ? ProcessJob.Assign(process) : null;
        Identity = ProcessCheck.Identify(process);
    }

    public ProcessIdentity Identity { get; }

    /// <summary>True once the process exited, even while a process it started still holds its pipes open.</summary>
    public bool HasExited
    {
        get
        {
            lock (_gate)
            {
                return _disposed || _process.HasExited;
            }
        }
    }

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

    public async Task<bool> WriteInputAsync(string text, bool close, TimeSpan timeout, CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(timeout);
        try
        {
            await _process.StandardInput.WriteAsync(text.AsMemory(), bounded.Token);
            await _process.StandardInput.FlushAsync(bounded.Token);
            if (close)
            {
                _process.StandardInput.Close();
            }

            return true;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Reads stdout in the background, one call per line. Lines can be large, so there is no length cap.</summary>
    public void ReadStdout(Action<string> onLine) => _stdout = ReadLinesAsync(_process.StandardOutput, onLine);

    public void ReadStderr(Action<string> onLine) => _stderr = ReadLinesAsync(_process.StandardError, onLine);

    public async Task<int> WaitForExitAsync()
    {
        await _process.WaitForExitAsync();
        return _process.ExitCode;
    }

    /// <summary>
    /// Completes at the end of stdout and stderr, or 5 seconds after the call, whichever comes first. After the process
    /// exits or is stopped, a process it started may still hold the pipes open.
    /// </summary>
    public Task WaitForOutputAsync(TimeSpan? timeout = null) => Task.WhenAny(Task.WhenAll(_stdout, _stderr), Task.Delay(timeout ?? OutputGrace));

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
            // The job holds the process only from just after its start, and Linux and macOS have no job.
            ProcessCheck.KillTreeQuietly(_process);
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
