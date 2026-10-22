using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace IDevelop.Execution;

/// <summary>
/// A command that Linux or macOS starts through posix_spawn with POSIX_SPAWN_SETSID, so the child leads a new session and
/// a new process group before the command runs. It has no controlling terminal, so a read from a terminal fails at once,
/// as it does under a desktop launch, instead of stopping it in the background when iDevelop runs in a terminal. Where
/// posix_spawn refuses that flag, POSIX_SPAWN_SETPGROUP with group ID 0 still makes the group, and
/// <see cref="SessionDetail"/> says why the child shares iDevelop's session. Every process it starts stays in that group
/// unless it calls setsid or setpgid, including one whose parent has exited, which Process.Kill(entireProcessTree) cannot
/// find there. The group ID is the root's process ID. It keeps what Process.Start gives a child: the working folder, the
/// arguments, the environment, three redirected pipes, the signal mask, and ignored signals.
///
/// The root is not reaped when it exits, so its ID, and with it the group ID, cannot be reused while the root is a zombie.
/// Cleanup reaps it, and from then on the ID is reserved only while a member lives: each later signal follows a check
/// that the group still exists, and a group once seen empty is never signaled again. <see cref="Dispose"/> signals
/// nothing.
/// </summary>
internal sealed class ProcessGroup : IDisposable
{
    internal static bool LaunchFailure { get; set; }

    internal static int? SignalFailure { get; set; }

    internal static bool SessionFailure { get; set; }

    private const int Sigkill = 9;
    private const int Sigterm = 15;
    private const int Eperm = 1;
    private const int Esrch = 3;
    private const int Eintr = 4;
    private const short SetProcessGroup = 0x02;
    private const int Einval = 22;
    private const int ProcessIdType = 1;
    private const int ExitedChild = 4;
    private const int NoHang = 1;
    private const int ChildExited = 1;
    private const int ChildKilled = 2;
    private const int ChildDumped = 3;

    // posix_spawnattr_t and posix_spawn_file_actions_t are opaque: 336 and 80 bytes on glibc, a pointer on macOS.
    private const int OpaqueSize = 1024;

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan KillPatience = TimeSpan.FromSeconds(2);

    private readonly Lock _gate = new();
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Process? _root;
    private bool _exited;
    private bool _reaped;
    private bool _empty;
    private bool _disposed;

    private ProcessGroup(int id, string? sessionDetail, Stream input, Stream output, Stream error)
    {
        Id = id;
        SessionDetail = sessionDetail;
        Input = input;
        Output = output;
        Error = error;
        try
        {
            _root = Process.GetProcessById(id);
        }
        catch (ArgumentException)
        {
        }

        Identity = _root is null ? new ProcessIdentity(id, DateTimeOffset.UtcNow) : ProcessCheck.Identify(_root);
        new Thread(WaitForRoot) { IsBackground = true, Name = $"Process group {id}" }.Start();
    }

    /// <summary>The root's process ID, which is also the group ID.</summary>
    public int Id { get; }

    public ProcessIdentity Identity { get; }

    /// <summary>Why the root shares iDevelop's session and terminal, or null when it leads a session of its own.</summary>
    public string? SessionDetail { get; }

    public Stream Input { get; }

    public Stream Output { get; }

    public Stream Error { get; }

    /// <summary>The root's exit code, or 128 plus the signal that ended it, as Process.ExitCode gives on Linux and macOS.</summary>
    public Task<int> Exited => _exit.Task;

    public bool HasExited
    {
        get
        {
            lock (_gate)
            {
                return _exited;
            }
        }
    }

    /// <exception cref="Win32Exception">posix_spawn refused the command, with the message Process.Start would give.</exception>
    /// <exception cref="NotSupportedException">This system cannot start a command in a new process group this way.</exception>
    public static ProcessGroup Start(ProcessStartInfo start)
    {
        if (LaunchFailure)
        {
            throw new NotSupportedException("The process group launcher is switched off.");
        }

        if (OperatingSystem.IsWindows() || !Path.IsPathRooted(start.FileName))
        {
            throw new NotSupportedException($"posix_spawn needs a full path, not {start.FileName}.");
        }

        AnonymousPipeServerStream[] pipes =
        [
            new(PipeDirection.Out, HandleInheritability.None),
            new(PipeDirection.In, HandleInheritability.None),
            new(PipeDirection.In, HandleInheritability.None),
        ];
        (int Id, string? SessionDetail) spawned;
        try
        {
            // Both ends are close-on-exec, so no other child inherits them. Only the child's copies at 0, 1, and 2 stay open.
            spawned = Spawn(start, [.. pipes.Select(pipe => (int)pipe.ClientSafePipeHandle.DangerousGetHandle())]);
        }
        catch
        {
            foreach (var pipe in pipes)
            {
                pipe.Dispose();
            }

            throw;
        }
        finally
        {
            foreach (var pipe in pipes)
            {
                pipe.DisposeLocalCopyOfClientHandle();
            }
        }

        return new ProcessGroup(spawned.Id, spawned.SessionDetail, pipes[0], pipes[1], pipes[2]);
    }

    /// <summary>
    /// Kills every process in the group and the root's tree, which also reaches a child that left the group while its
    /// parent lives. Once the root is reaped it does nothing, because cleanup then owns the group.
    /// </summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_disposed || _reaped)
            {
                return;
            }

            // The tree first, while the root lives: once it exits, its children belong to init and no tree reaches them.
            if (_root is not null)
            {
                ProcessCheck.KillTreeQuietly(_root);
            }

            if (SignalFailure is null)
            {
                _ = Kill(-Id, Sigkill);
            }
        }
    }

    /// <summary>
    /// Sends SIGTERM to the group, waits until it is empty or <paramref name="grace"/> has passed on
    /// <paramref name="clock"/>, then sends SIGKILL to whatever is left and waits up to two more seconds. Cancellation
    /// ends the grace early. The outcome is data: a step records each signal and why it failed.
    /// </summary>
    public async Task<Cleanup> CleanUpAsync(TimeSpan grace, TimeProvider clock, CancellationToken ct)
    {
        using var stopWaiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // The grace starts before the signal, so a test that sees SIGTERM arrive can move its clock past the grace.
        var graceOver = Task.Delay(grace, clock, stopWaiting.Token);
        List<CleanupStep> steps = [new(clock.GetUtcNow(), "terminateGroup", Signal(Sigterm))];
        while (HasMembers() && !graceOver.IsCompleted)
        {
            await Task.WhenAny(graceOver, Task.Delay(Poll, CancellationToken.None)).ConfigureAwait(false);
        }

        await stopWaiting.CancelAsync().ConfigureAwait(false);
        if (HasMembers())
        {
            steps.Add(new CleanupStep(clock.GetUtcNow(), "killGroup", Signal(Sigkill)));
            var killed = Stopwatch.StartNew();
            while (HasMembers() && killed.Elapsed < KillPatience)
            {
                await Task.Delay(Poll, CancellationToken.None).ConfigureAwait(false);
            }
        }

        bool released;
        lock (_gate)
        {
            released = _disposed;
        }

        if (released)
        {
            return new Cleanup(CleanupResult.Incomplete, "The turn's process was released during cleanup.", [.. steps]);
        }

        return HasMembers()
            ? new Cleanup(CleanupResult.Incomplete, $"Process group {Id} still had processes after cleanup.", [.. steps])
            : new Cleanup(CleanupResult.Completed, null, [.. steps]);
    }

    /// <summary>Reaps an exited root. It signals nothing, and it leaves the pipes to their readers, as Process.Dispose does.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Reap();
            _root?.Dispose();
        }
    }

    private string? Signal(int signal)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return "The process group was released.";
            }

            if (SignalFailure is { } failure)
            {
                return Describe(failure);
            }

            if (_empty || _reaped && !Alive())
            {
                return Describe(Esrch);
            }

            return Kill(-Id, signal) == 0 ? null : Describe(Marshal.GetLastPInvokeError());
        }
    }

    /// <summary>Reaps an exited root first, so a zombie root no longer counts as a member.</summary>
    private bool HasMembers()
    {
        lock (_gate)
        {
            if (_disposed || _empty)
            {
                return false;
            }

            if (!_exited)
            {
                return true;
            }

            Reap();
            return Alive();
        }
    }

    // The caller holds the gate, and the root is reaped. Signal 0 checks for a member without sending anything.
    private bool Alive()
    {
        if (Kill(-Id, 0) == 0 || Marshal.GetLastPInvokeError() == Eperm)
        {
            return true;
        }

        _empty = true;
        return false;
    }

    // The caller holds the gate.
    private void Reap()
    {
        if (!_exited || _reaped)
        {
            return;
        }

        _reaped = true;
        while (WaitPid(Id, 0, NoHang) < 0 && Marshal.GetLastPInvokeError() == Eintr)
        {
        }
    }

    private void WaitForRoot()
    {
        // siginfo_t is 128 bytes on Linux and 104 on macOS. si_code is at 8; si_status follows si_pid and si_uid.
        var info = new byte[128];
        var statusOffset = OperatingSystem.IsMacOS() || IntPtr.Size == 4 ? 20 : 24;
        while (true)
        {
            Array.Clear(info);
            // WNOWAIT leaves the root a zombie, which keeps its ID and the group ID from being reused.
            if (WaitId(ProcessIdType, (uint)Id, info, ExitedChild | NoWaitFlag) == 0)
            {
                var code = BitConverter.ToInt32(info, 8);
                if (code is ChildExited or ChildKilled or ChildDumped)
                {
                    var status = BitConverter.ToInt32(info, statusOffset);
                    Exit(code == ChildExited ? status & 0xff : 128 + status, reaped: false);
                    return;
                }

                // macOS can return for a stopped child even without WSTOPPED. The stop stays reported, so wait before asking again.
                Thread.Sleep(Poll);
                continue;
            }

            if (Marshal.GetLastPInvokeError() == Eintr)
            {
                continue;
            }

            // ECHILD: something else in this process reaped the root, so the ID is no longer reserved by a zombie.
            Exit(-1, reaped: true);
            return;
        }
    }

    private void Exit(int code, bool reaped)
    {
        lock (_gate)
        {
            _exited = true;
            _reaped |= reaped;
            if (_disposed)
            {
                Reap();
            }
        }

        _exit.TrySetResult(code);
    }

    private static int NoWaitFlag => OperatingSystem.IsMacOS() ? 0x20 : 0x01000000;

    private static string Describe(int error) => new Win32Exception(error).Message;

    // POSIX_SPAWN_SETSID is 0x80 on glibc 2.26 and later and on musl, and 0x400 in macOS's sys/spawn.h.
    private static short NewSession => OperatingSystem.IsMacOS() ? (short)0x400 : (short)0x80;

    private static (int Id, string? SessionDetail) Spawn(ProcessStartInfo start, int[] pipes)
    {
        var fileActions = Marshal.AllocHGlobal(OpaqueSize);
        var attributes = Marshal.AllocHGlobal(OpaqueSize);
        List<nint> strings = [];
        try
        {
            unsafe
            {
                NativeMemory.Clear((void*)fileActions, OpaqueSize);
                NativeMemory.Clear((void*)attributes, OpaqueSize);
            }

            Check(FileActionsInit(fileActions));
            try
            {
                Check(AttributesInit(attributes));
                try
                {
                    for (var target = 0; target < pipes.Length; target++)
                    {
                        Check(FileActionsAddDup2(fileActions, pipes[target], target));
                    }

                    if (!string.IsNullOrEmpty(start.WorkingDirectory))
                    {
                        Check(FileActionsAddChdir(fileActions, start.WorkingDirectory));
                    }

                    // A session leader's group ID is its own ID. Without a new session, group 0 does the same. The child
                    // joins either before it runs the command. The two flags never go together: a session leader cannot
                    // change its group.
                    string? sessionDetail = null;
                    var session = SessionFailure ? Einval : AttributesSetFlags(attributes, NewSession);
                    if (session != 0)
                    {
                        sessionDetail = "The client shares iDevelop's terminal session, so a read from that terminal would stop it. " +
                            $"posix_spawn refused POSIX_SPAWN_SETSID: {Describe(session)}";
                        Check(AttributesSetFlags(attributes, SetProcessGroup));
                        Check(AttributesSetGroup(attributes, 0));
                    }

                    var arguments = Strings([start.FileName, .. start.ArgumentList], strings);
                    var environment = Strings(start.Environment.Select(pair => pair.Key + "=" + pair.Value), strings);
                    var result = PosixSpawn(out var id, start.FileName, fileActions, attributes, arguments, environment);
                    if (result != 0)
                    {
                        var folder = string.IsNullOrEmpty(start.WorkingDirectory) ? Directory.GetCurrentDirectory() : start.WorkingDirectory;
                        throw new Win32Exception(result,
                            $"An error occurred trying to start process '{start.FileName}' with working directory '{folder}'. {Describe(result)}");
                    }

                    return (id, sessionDetail);
                }
                finally
                {
                    _ = AttributesDestroy(attributes);
                }
            }
            finally
            {
                _ = FileActionsDestroy(fileActions);
            }
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
            // posix_spawn_file_actions_addchdir_np needs glibc 2.29 or macOS 10.15.
            throw new NotSupportedException(e.Message, e);
        }
        finally
        {
            foreach (var text in strings)
            {
                Marshal.FreeCoTaskMem(text);
            }

            Marshal.FreeHGlobal(attributes);
            Marshal.FreeHGlobal(fileActions);
        }
    }

    // A null-terminated array of UTF-8 strings, which the caller frees through the list.
    private static nint[] Strings(IEnumerable<string> values, List<nint> owned)
    {
        List<nint> array = [];
        foreach (var value in values)
        {
            var text = Marshal.StringToCoTaskMemUTF8(value);
            owned.Add(text);
            array.Add(text);
        }

        array.Add(0);
        return [.. array];
    }

    private static void Check(int result)
    {
        if (result != 0)
        {
            throw new Win32Exception(result);
        }
    }

    [DllImport("libc", EntryPoint = "posix_spawn")]
    private static extern int PosixSpawn(out int id, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, nint fileActions, nint attributes,
        nint[] arguments, nint[] environment);

    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_init")]
    private static extern int FileActionsInit(nint fileActions);

    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_destroy")]
    private static extern int FileActionsDestroy(nint fileActions);

    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_adddup2")]
    private static extern int FileActionsAddDup2(nint fileActions, int descriptor, int target);

    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_addchdir_np")]
    private static extern int FileActionsAddChdir(nint fileActions, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport("libc", EntryPoint = "posix_spawnattr_init")]
    private static extern int AttributesInit(nint attributes);

    [DllImport("libc", EntryPoint = "posix_spawnattr_destroy")]
    private static extern int AttributesDestroy(nint attributes);

    [DllImport("libc", EntryPoint = "posix_spawnattr_setflags")]
    private static extern int AttributesSetFlags(nint attributes, short flags);

    [DllImport("libc", EntryPoint = "posix_spawnattr_setpgroup")]
    private static extern int AttributesSetGroup(nint attributes, int group);

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int id, int signal);

    [DllImport("libc", EntryPoint = "waitid", SetLastError = true)]
    private static extern int WaitId(int idType, uint id, [Out] byte[] info, int options);

    [DllImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    private static extern int WaitPid(int id, nint status, int options);
}
