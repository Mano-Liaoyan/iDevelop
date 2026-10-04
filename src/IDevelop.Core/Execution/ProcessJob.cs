using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace IDevelop.Execution;

/// <summary>
/// A Windows job object that holds one child and every process it starts, including one whose parent has exited, which
/// Process.Kill(entireProcessTree) cannot find. Closing the job stops them all, unless <see cref="KeepProcessesOnClose"/>
/// came first, and Windows closes it when iDevelop exits. No process may leave the job, because Git Bash, which Pi runs
/// its commands in, starts each command outside a job that allows it.
/// </summary>
internal sealed class ProcessJob : IDisposable
{
    private const uint KillOnJobClose = 0x2000;
    private const int ExtendedLimitInformation = 9;

    private readonly nint _handle;

    private ProcessJob(nint handle) => _handle = handle;

    /// <summary>Null when the process could not join a job, such as one that already exited. Stopping its tree still works.</summary>
    public static ProcessJob? Assign(Process process)
    {
        var handle = CreateJobObjectW(0, null);
        if (handle == 0)
        {
            return null;
        }

        if (SetLimits(handle, KillOnJobClose) && AssignProcessToJobObject(handle, process.SafeHandle))
        {
            return new ProcessJob(handle);
        }

        CloseHandle(handle);
        return null;
    }

    public void Terminate() => TerminateJobObject(_handle, uint.MaxValue);

    /// <summary>Closing the job then leaves its processes running.</summary>
    public void KeepProcessesOnClose() => SetLimits(_handle, 0);

    private static bool SetLimits(nint job, uint flags)
    {
        var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = flags } };
        return SetInformationJobObject(job, ExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>());
    }

    public void Dispose() => CloseHandle(_handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObjectW(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimits info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(nint job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    // JOBOBJECT_BASIC_LIMIT_INFORMATION and JOBOBJECT_EXTENDED_LIMIT_INFORMATION. Only the limit flags are set.
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
