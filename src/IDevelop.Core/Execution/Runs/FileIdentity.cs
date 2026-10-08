using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace IDevelop.Execution;

internal readonly record struct FileIdentity(ulong Volume, UInt128 File, long ChangeTicks);

internal sealed record LockEvidence(EvidenceFile Bytes, FileIdentity? Identity);

internal enum LockRemoval { Removed, Absent, Changed, Unavailable }

internal static partial class FileIdentities
{
    public static FileIdentity? Of(SafeFileHandle handle)
    {
        try
        {
            if (OperatingSystem.IsLinux())
                return StatxHandle(handle, "", 0x1000, 0x180, out var status) == 0 && (status.Mask & 0x180) == 0x180
                    ? Identity(status) : null;
            if (OperatingSystem.IsWindows())
                return FileIdInfo(handle, 18, out var id, 24) && FileBasicInfo(handle, 0, out var basic, 40)
                    ? new(id.Volume, ((UInt128)id.High << 64) | id.Low, basic.ChangeTime) : null;
            return null;
        }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    public static ulong? VolumeOf(string folder)
    {
        try
        {
            if (OperatingSystem.IsLinux())
                return StatxPath(-100, folder, 0, 0x100, out var status) == 0 ? Identity(status).Volume : null;
            if (OperatingSystem.IsWindows())
            {
                using var handle = CreateFile(folder, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
                return !handle.IsInvalid && FileIdInfo(handle, 18, out var id, 24) ? id.Volume : null;
            }
            return null;
        }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    public static (byte[] Bytes, FileIdentity? Identity)? ReadFile(string path)
    {
        try
        {
            RegularFile.Verify(path);
            using var handle = Open(path);
            Verify(handle, path);
            return (Read(handle), Of(handle));
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public static LockRemoval Remove(string path, byte[] bytes, FileIdentity identity)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return LockRemoval.Unavailable;
        try
        {
            RegularFile.Verify(path);
            using var handle = Open(path);
            Verify(handle, path);
            var observed = Read(handle);
            if (Of(handle) is not { } current) return LockRemoval.Unavailable;
            if (current != identity || !observed.AsSpan().SequenceEqual(bytes)) return LockRemoval.Changed;
            if (OperatingSystem.IsWindows())
            {
                var disposition = new Disposition { DeleteFile = 1 };
                if (!SetDisposition(handle, 4, in disposition, 1)) throw NativeError(path);
            }
            else File.Delete(path);
            return LockRemoval.Removed;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return LockRemoval.Absent;
        }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException)
        {
            return LockRemoval.Unavailable;
        }
    }

    private static SafeFileHandle Open(string path)
    {
        if (!OperatingSystem.IsWindows())
            return File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var handle = CreateFile(path, 0x80010000, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw error switch
        {
            2 => new FileNotFoundException("File is absent.", path),
            3 => new DirectoryNotFoundException(path),
            _ => new IOException($"Cannot open {path}.", new Win32Exception(error)),
        };
    }

    private static void Verify(SafeFileHandle handle, string path)
    {
        if (OperatingSystem.IsLinux())
        {
            if (StatxHandle(handle, "", 0x1000, 1, out var status) != 0 || (status.Mode & 0xf000) != 0x8000)
                throw new IOException($"Path is not a regular file: {path}.");
        }
        else if (OperatingSystem.IsWindows() &&
            (!FileBasicInfo(handle, 0, out var basic, 40) || (basic.Attributes & 0x450) != 0 || GetFileType(handle) != 1))
            throw new IOException($"Path is not a regular file: {path}.");
    }

    private static byte[] Read(SafeFileHandle handle)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = RandomAccess.Read(handle, buffer, bytes.Length)) != 0) bytes.Write(buffer, 0, count);
        return bytes.ToArray();
    }

    private static IOException NativeError(string path) => new($"Cannot remove {path}.", new Win32Exception(Marshal.GetLastPInvokeError()));

    private static FileIdentity Identity(Statx status) => new(((ulong)status.DeviceMajor << 32) | status.DeviceMinor,
        status.Inode, status.ChangeSeconds * 10_000_000 + status.ChangeNanoseconds / 100);

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Statx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(96)] public long ChangeSeconds;
        [FieldOffset(104)] public uint ChangeNanoseconds;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileId
    {
        public ulong Volume;
        public ulong Low;
        public ulong High;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct BasicInfo
    {
        [FieldOffset(24)] public long ChangeTime;
        [FieldOffset(32)] public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Disposition
    {
        public byte DeleteFile;
    }

    [SupportedOSPlatform("linux")]
    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int StatxHandle(SafeFileHandle directory, string path, int flags, uint mask, out Statx status);

    [SupportedOSPlatform("linux")]
    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int StatxPath(int directory, string path, int flags, uint mask, out Statx status);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FileIdInfo(SafeFileHandle handle, int info, out FileId value, uint size);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FileBasicInfo(SafeFileHandle handle, int info, out BasicInfo value, uint size);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetFileType(SafeFileHandle handle);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetDisposition(SafeFileHandle handle, int info, in Disposition value, uint size);
}
