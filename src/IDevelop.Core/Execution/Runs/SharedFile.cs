namespace IDevelop.Execution;

/// <summary>Reads a file another handle may hold open for writing, which <see cref="File.ReadAllBytes"/> refuses on Windows.</summary>
internal static class SharedFile
{
    public static byte[] ReadAllBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
