namespace IDevelop.TestSupport;

internal sealed class TempFolder(string? root = null) : IDisposable
{
    private readonly string _root = root ?? NewRoot();

    /// <summary>A new folder's path under the temporary folder. Nothing creates it yet.</summary>
    public static string NewRoot() => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "idevelop-tests", Guid.NewGuid().ToString("N"));

    public string Create(string name)
    {
        var folder = System.IO.Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>A file's path in a new folder of its own under the root. Nothing creates the file.</summary>
    public string Path(string name) => System.IO.Path.Combine(Create(Guid.NewGuid().ToString("N")), name);

    public string CopyOf(string source)
    {
        var folder = Create(System.IO.Path.GetFileName(source));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = System.IO.Path.Combine(folder, System.IO.Path.GetRelativePath(source, file));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        return folder;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            // Git writes its objects read-only, and Windows deletes no read-only file. Links stay as they are, so a
            // target outside the folder keeps its attributes.
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var file in new DirectoryInfo(_root).EnumerateFiles("*", options))
            {
                if (file.Attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    file.Attributes &= ~FileAttributes.ReadOnly;
                }
            }

            Directory.Delete(_root, recursive: true);
        }
    }
}
