namespace IDevelop.Projects;

/// <summary>How the app names a project folder and tells two names of one folder apart.</summary>
public static class ProjectFolders
{
    /// <summary>Windows and macOS file systems ignore case by default, and Linux ones do not.</summary>
    public static readonly StringComparer Comparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static string Identity(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    /// <summary>
    /// The folder as its file system spells it. Each part takes the one entry that matches it ignoring case, and keeps its
    /// own spelling when no entry or several do. Case sensitivity belongs to the volume rather than the platform, and a Mac
    /// volume can hold Repo and repo side by side.
    /// </summary>
    public static string OnDisk(string folder)
    {
        var full = Identity(folder);
        var root = Path.GetPathRoot(full)!;
        var resolved = OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            resolved = Path.Combine(resolved, Spelling(resolved, part));
        return resolved;
    }

    /// <summary>The folder's own name, or the whole path for a drive's root.</summary>
    public static string Name(string folder) => Path.GetFileName(folder) is { Length: > 0 } name ? name : folder;

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
