using System.Text;

namespace IDevelop.Projects;

/// <summary>The layout of a project's <c>.idp</c> folder, which the workflow document and the attempts share.</summary>
internal static class DataFolder
{
    public const string Name = ".idp";

    private static readonly string[] IgnoredLines = ["*.tmp", "attempts/", "runs/"];

    public static string Workflows(string projectFolder) => Path.Combine(projectFolder, Name, "workflows");

    public static string Attempts(string projectFolder) => Path.Combine(projectFolder, Name, "attempts");

    public static string Runs(string projectFolder) => Path.Combine(projectFolder, Name, "runs");

    /// <summary>The project's blueprint library, which Git tracks.</summary>
    public static string Blueprints(string projectFolder) => Path.Combine(projectFolder, Name, "blueprints");

    /// <summary>
    /// Makes <c>.idp/.gitignore</c> ignore temporary files, attempts, and runs. It keeps the user's lines and appends the
    /// missing ones when an older project gains execution records.
    /// </summary>
    public static void EnsureGitIgnore(string projectFolder)
    {
        var path = Path.Combine(projectFolder, Name, ".gitignore");
        var text = File.Exists(path) ? File.ReadAllText(path) : "";
        var present = text.Split('\n').Select(line => line.Trim()).ToHashSet();
        var missing = IgnoredLines.Where(line => !present.Contains(line)).ToArray();
        if (missing.Length == 0)
        {
            return;
        }

        var separator = text.Length == 0 || text.EndsWith('\n') ? "" : "\n";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.Replace(path, Encoding.UTF8.GetBytes(text + separator + string.Concat(missing.Select(line => line + "\n"))));
    }
}
