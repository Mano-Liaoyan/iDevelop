using IDevelop.Projects;

namespace IDevelop.TestSupport;

internal static class FolderSpelling
{
    /// <summary>
    /// The folder as its file system spells it. Each part takes the one entry that matches it ignoring case, and keeps its
    /// own spelling when no entry or several do. Case sensitivity belongs to the volume rather than the platform, and a Mac
    /// volume can hold Repo and repo side by side.
    /// </summary>
    public static string OnDisk(string folder)
    {
        var full = ProjectFolders.Identity(folder);
        var root = Path.GetPathRoot(full)!;
        var resolved = OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            resolved = Path.Combine(resolved, Spelling(resolved, part));
        return resolved;
    }

    private static string Spelling(string parent, string part)
    {
        try
        {
            var matches = new DirectoryInfo(parent).EnumerateFileSystemInfos().Select(entry => entry.Name)
                .Where(name => string.Equals(name, part, StringComparison.OrdinalIgnoreCase)).ToArray();
            return matches is [var only] ? only : part;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return part;
        }
    }
}
