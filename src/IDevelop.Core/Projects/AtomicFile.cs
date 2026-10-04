namespace IDevelop.Projects;

public static class AtomicFile
{
    /// <summary>Writes the contents to a temporary file beside the path, flushes it to disk, then moves it over the path,
    /// so a crash leaves either the old file or the new one.</summary>
    public static void Replace(string path, ReadOnlySpan<byte> contents)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }
}
