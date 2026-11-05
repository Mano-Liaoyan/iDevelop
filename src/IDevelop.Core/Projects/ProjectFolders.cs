namespace IDevelop.Projects;

/// <summary>How the app names a project folder and tells two names of one folder apart.</summary>
public static class ProjectFolders
{
    /// <summary>Windows and macOS file systems ignore case by default, and Linux ones do not.</summary>
    public static readonly StringComparer Comparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static string Identity(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    /// <summary>The folder's own name, or the whole path for a drive's root.</summary>
    public static string Name(string folder) => Path.GetFileName(folder) is { Length: > 0 } name ? name : folder;
}
