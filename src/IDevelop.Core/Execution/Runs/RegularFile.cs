using System.Runtime.InteropServices;

namespace IDevelop.Execution;

internal static class RegularFile
{
    public static void Verify(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0 ||
            new FileInfo(path).LinkTarget is not null)
            throw new IOException($"Artifact path is not a regular file: {path}.");
        if (OperatingSystem.IsWindows()) return;
        // Managed attributes do not distinguish Unix FIFOs and devices; inspect type before opening to avoid blocking on a pipe.
        var status = new byte[512];
        var linux = OperatingSystem.IsLinux();
        var success = linux ? Statx(-100, path, 0x100, 1, status) : Lstat(path, status);
        var mode = BitConverter.ToUInt16(status, linux ? 28 : 4);
        if (success != 0 || (mode & 0xf000) != 0x8000)
            throw new IOException($"Artifact path is not a regular file: {path}.");
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, [Out] byte[] status);

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
    private static extern int Lstat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] status);
}
