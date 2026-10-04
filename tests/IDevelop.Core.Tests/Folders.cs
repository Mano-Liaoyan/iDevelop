namespace IDevelop.TestSupport;

internal static class Folders
{
    /// <summary>
    /// The folder as a process started in it reports its current folder. On Linux and macOS that is without links, which
    /// matters on macOS, where the temporary folder sits under /var, a link to /private/var. Windows keeps a junction or
    /// a link as given.
    /// </summary>
    public static string AsCurrentFolder(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (OperatingSystem.IsWindows())
        {
            return full;
        }

        var resolved = Path.GetPathRoot(full)!;
        foreach (var part in full[resolved.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = Path.Combine(resolved, part);
            if (new DirectoryInfo(resolved).ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                resolved = target.FullName;
            }
        }

        return resolved;
    }
}
