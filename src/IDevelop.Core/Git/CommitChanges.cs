using System.Collections.Immutable;
using System.Globalization;

namespace IDevelop.Git;

/// <summary>One file a commit changed. A binary file has no line counts.</summary>
public sealed record FileChange(string Path, int? Added, int? Deleted);

/// <summary>Reads what a commit changed, for a person who judges a result.</summary>
public static class CommitChanges
{
    /// <summary>The files <paramref name="commit"/> changed against its first parent, or none when Git cannot read them.</summary>
    public static ImmutableArray<FileChange> Read(string projectFolder, string commit, IReadOnlyDictionary<string, string> environment)
    {
        var result = GitRepository.Run(["show", "--numstat", "--no-renames", "--first-parent", "--format=", commit], projectFolder, GitOperation.Metadata, GitLimits.Default, environment);
        return result.ExitCode == 0 ? Parse(result.Text) : [];
    }

    /// <summary>
    /// The lines <paramref name="commit"/> changed in <paramref name="path"/> against its first parent, from its first hunk on, at
    /// most <paramref name="limit"/>. None when Git cannot read them or the file has no text lines.
    /// </summary>
    public static ImmutableArray<string> ReadDiff(string projectFolder, string commit, string path, int limit, IReadOnlyDictionary<string, string> environment)
    {
        var result = GitRepository.Run(["show", "--no-renames", "--first-parent", "--no-color", "--format=", commit, "--", path], projectFolder, GitOperation.Metadata, GitLimits.Default, environment);
        return result.ExitCode == 0 ? Hunks(result.Text, limit) : [];
    }

    internal static ImmutableArray<string> Hunks(string diff, int limit) =>
    [
        .. diff.Split('\n').Select(line => line.TrimEnd('\r')).SkipWhile(line => !line.StartsWith("@@", StringComparison.Ordinal))
            .Where((line, index) => line.Length > 0).Take(limit),
    ];

    // Each line is "added<tab>deleted<tab>path", with a dash for each count of a binary file.
    internal static ImmutableArray<FileChange> Parse(string numstat) =>
    [
        .. numstat.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('\t', 3))
            .Where(parts => parts.Length == 3)
            .Select(parts => new FileChange(parts[2], Count(parts[0]), Count(parts[1]))),
    ];

    private static int? Count(string text) => int.TryParse(text, CultureInfo.InvariantCulture, out var count) ? count : null;
}
