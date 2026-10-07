using IDevelop.Workflows;

namespace IDevelop.Execution;

internal sealed class RunStorage(string project, WorkflowId workflow, RunId run)
{
    public string Folder { get; } = SafePath(project, $".idp/runs/{workflow}/{run}");

    public static string ArtifactPath(ResultId result, string name) => $"results/{result.Value:D}/artifacts/{name}";

    public EvidenceFile WriteEvidence(OperationId operation, string name, byte[] bytes)
    {
        var relative = $"evidence/{operation.Value:D}/{name}";
        Publish(Folder, relative, bytes, Revision.Hash(bytes), bytes.LongLength);
        return new(relative, Revision.Hash(bytes), bytes.LongLength);
    }

    public byte[] ReadArtifact(ResultId result, ArtifactRecord artifact)
    {
        if (artifact.StoredPath != ArtifactPath(result, artifact.Name))
        {
            throw new IOException($"Result {result.Value:D}: invalid stored path {artifact.StoredPath}.");
        }
        return Read(Folder, artifact.StoredPath, artifact.Content, artifact.ByteLength);
    }

    internal static byte[] Read(string root, string relative, Digest digest, long length)
    {
        var path = SafePath(root, relative);
        RegularFile.Verify(path);
        var bytes = File.ReadAllBytes(path);
        Verify(bytes, digest, length);
        return bytes;
    }

    internal static void Publish(string root, string relative, byte[] bytes, Digest digest, long length)
    {
        Verify(bytes, digest, length);
        var path = SafePath(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        path = SafePath(root, relative);
        if (File.Exists(path))
        {
            Read(root, relative, digest, length);
            return;
        }
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, ".idp-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            Verify(File.ReadAllBytes(temporary), digest, length);
            SafePath(root, relative);
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { Read(root, relative, digest, length); }
        }
        finally { File.Delete(temporary); }
    }

    internal static string SafePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.Contains(':') || Path.IsPathRooted(relative) ||
            relative.Any(char.IsControl) || relative.Split('/').Any(part => part is "" or "." or ".." ||
                part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0 || Reserved(part)))
        {
            throw new IOException($"Unsafe storage path {relative}.");
        }
        var path = Path.GetFullPath(root);
        CheckPath(path);
        foreach (var part in relative.Split('/'))
        {
            if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any(entry =>
                string.Equals(Path.GetFileName(entry), part, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(entry) != part))
            {
                throw new IOException($"Case collision at {relative}.");
            }
            path = Path.Combine(path, part);
            CheckPath(path);
        }
        return path;
    }

    private static bool Reserved(string part)
    {
        var name = part.Split('.')[0].ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" ||
            name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) &&
            name[3] is >= '1' and <= '9';
    }

    private static void CheckPath(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) &&
            (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0 ||
            new FileInfo(path).LinkTarget is not null)
        {
            throw new IOException($"Nonregular storage path {path}.");
        }
    }

    private static void Verify(byte[] bytes, Digest digest, long length)
    {
        if (bytes.LongLength != length || Revision.Hash(bytes) != digest)
        {
            throw new IOException("Stored bytes differ from the recorded digest or length.");
        }
    }
}
