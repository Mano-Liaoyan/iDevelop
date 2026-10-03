namespace IDevelop.Core.Tests;

internal sealed class TempFolder : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "idevelop-tests", Guid.NewGuid().ToString("N"));

    public string Create(string name)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    public string CopyOf(string source)
    {
        var folder = Create(Path.GetFileName(source));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(folder, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        return folder;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
