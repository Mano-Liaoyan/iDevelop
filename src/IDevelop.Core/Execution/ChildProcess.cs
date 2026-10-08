using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;

namespace IDevelop.Execution;

internal enum ProcessLifetime { Standalone, Workflow }

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Containment.Job), "job")]
[JsonDerivedType(typeof(Containment.Group), "group")]
[JsonDerivedType(typeof(Containment.None), "none")]
internal abstract record Containment
{
    private Containment() { }

    public sealed record Job : Containment;

    /// <summary>The process group a Linux or macOS workflow turn leads. Its ID is the root's process ID.</summary>
    public sealed record Group(int Id) : Containment;

    public sealed record None(string Reason) : Containment;
}

internal sealed record CleanupStep(DateTimeOffset At, string Action, string? Failure);

internal enum CleanupResult { Completed, Incomplete }

internal sealed record Cleanup(CleanupResult Result, string? Detail, ImmutableArray<CleanupStep> Steps);

/// <summary>The message is for the user.</summary>
internal sealed class LaunchException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// A started command with redirected pipes. It knows nothing about any client: runs and probes both use it.
/// Text crosses the pipes as UTF-8 without a byte order mark on every platform. On Windows the process and everything
/// it starts share a job, so disposing it, or iDevelop exiting, stops whatever is still running, unless
/// <see cref="LeaveDescendantsRunning"/> came first. On Linux and macOS a workflow turn leads its own process group,
/// which only <see cref="StopTree"/> and <see cref="CleanUpAsync"/> signal.
/// </summary>
internal sealed class ChildProcess : IDisposable
{
    private const string NoGroup = "No process group contains this turn's descendants.";

    // Process.Start's own buffer size for redirected pipes.
    private const int PipeBuffer = 4096;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly TimeSpan OutputGrace = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();
    private readonly Process? _process;
    private readonly ProcessJob? _job;
    private readonly ProcessGroup? _group;
    private readonly StreamWriter? _groupInput;
    private readonly StreamReader? _groupOutput;
    private readonly StreamReader? _groupError;
    private Task _stdout = Task.CompletedTask;
    private Task _stderr = Task.CompletedTask;
    private bool _disposed;

    private ChildProcess(Process process, ProcessLifetime lifetime, string? noGroup)
    {
        _process = process;
        _job = OperatingSystem.IsWindows() ? ProcessJob.Assign(process) : null;
        Identity = ProcessCheck.Identify(process);
        Lifetime = lifetime;
        Containment = _job is not null ? new Containment.Job() : new Containment.None(OperatingSystem.IsWindows()
            ? "The client could not join a job object."
            : noGroup is null ? NoGroup : $"{NoGroup} {noGroup}");
    }

    private ChildProcess(ProcessGroup group, ProcessLifetime lifetime)
    {
        _group = group;
        _groupInput = new StreamWriter(group.Input, Utf8, PipeBuffer) { AutoFlush = true };
        _groupOutput = new StreamReader(group.Output, Utf8, detectEncodingFromByteOrderMarks: true, PipeBuffer);
        _groupError = new StreamReader(group.Error, Utf8, detectEncodingFromByteOrderMarks: true, PipeBuffer);
        Identity = group.Identity;
        Lifetime = lifetime;
        Containment = new Containment.Group(group.Id);
    }

    public ProcessIdentity Identity { get; }

    public ProcessLifetime Lifetime { get; }

    public Containment Containment { get; }

    /// <summary>True once the process exited, even while a process it started still holds its pipes open.</summary>
    public bool HasExited
    {
        get
        {
            lock (_gate)
            {
                return _disposed || (_group?.HasExited ?? _process!.HasExited);
            }
        }
    }

    /// <exception cref="LaunchException">The command did not start, or a batch shim was given an unsafe argument.</exception>
    public static ChildProcess Start(ResolvedCommand command, IReadOnlyList<string> arguments, string workingDirectory, ProcessLifetime lifetime)
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
            string? noGroup = null;
            if (lifetime == ProcessLifetime.Workflow && !OperatingSystem.IsWindows())
            {
                try
                {
                    return new ChildProcess(ProcessGroup.Start(start), lifetime);
                }
                catch (NotSupportedException e)
                {
                    // Containment serves cleanup only, so the turn runs without a group and its cleanup says why.
                    noGroup = e.Message;
                }
            }

            return new ChildProcess(Process.Start(start) ?? throw new LaunchException($"{command.Path} did not start."), lifetime, noGroup);
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
            await Input.WriteAsync(text);
            await Input.FlushAsync();
            if (close)
            {
                Input.Close();
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
            await Input.WriteAsync(text.AsMemory(), bounded.Token);
            await Input.FlushAsync(bounded.Token);
            if (close)
            {
                Input.Close();
            }

            return true;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Reads stdout in the background, one call per line. Lines can be large, so there is no length cap.</summary>
    public void ReadStdout(Action<string> onLine) => _stdout = ReadLinesAsync(_groupOutput ?? _process!.StandardOutput, onLine);

    public void ReadStderr(Action<string> onLine) => _stderr = ReadLinesAsync(_groupError ?? _process!.StandardError, onLine);

    public async Task<int> WaitForExitAsync()
    {
        if (_group is not null)
        {
            return await _group.Exited;
        }

        await _process!.WaitForExitAsync();
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

            if (_group is not null)
            {
                _group.Stop();
                return;
            }

            _job?.Terminate();
            // The job holds the process only from just after its start, and a standalone run on Linux or macOS has neither.
            ProcessCheck.KillTreeQuietly(_process!);
        }
    }

    /// <summary>
    /// Stops what the turn left running once its root exited. A job is terminated at once. A group gets SIGTERM, then
    /// SIGKILL after <paramref name="grace"/> on <paramref name="clock"/>. Without either it stops the root's tree and says
    /// why the cleanup is incomplete. After <see cref="Dispose"/> it signals nothing.
    /// </summary>
    public Task<Cleanup> CleanUpAsync(TimeSpan grace, TimeProvider clock, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return Task.FromResult(new Cleanup(CleanupResult.Incomplete, "The turn's process was already released.", []));
            }

            if (_group is not null)
            {
                return _group.CleanUpAsync(grace, clock, ct);
            }

            var at = clock.GetUtcNow();
            if (_job is not null)
            {
                var (succeeded, error) = _job.Terminate();
                var failure = succeeded ? null : $"Win32 error {error}";
                return Task.FromResult(new Cleanup(succeeded ? CleanupResult.Completed : CleanupResult.Incomplete,
                    failure, [new CleanupStep(at, "terminateJob", failure)]));
            }

            return Task.FromResult(new Cleanup(CleanupResult.Incomplete, ((Containment.None)Containment).Reason,
                [new CleanupStep(at, "killTree", ProcessCheck.KillTree(_process!))]));
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
                _group?.Dispose();
                _process?.Dispose();
            }
        }
    }

    private StreamWriter Input => _groupInput ?? _process!.StandardInput;

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
