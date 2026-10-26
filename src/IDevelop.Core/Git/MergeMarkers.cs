using System.Collections.Immutable;

namespace IDevelop.Git;

/// <summary>Whether conflicting files still hold Git's conflict markers.</summary>
internal static class MergeMarkers
{
    /// <summary>
    /// The conflicting files in <paramref name="checkout"/> that still hold a conflict, in the order given: a line that opens
    /// Git's markers and one that closes them. A file the agent deleted holds none, and a path outside the checkout is skipped.
    /// </summary>
    public static ImmutableArray<string> Remaining(string checkout, IEnumerable<string> paths)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(checkout)) + Path.DirectorySeparatorChar;
        return [.. paths.Where(path => Path.GetFullPath(Path.Combine(root, path)) is var full
            && full.StartsWith(root, StringComparison.Ordinal) && File.Exists(full) && Holds(File.ReadLines(full)))];
    }

    private static bool Holds(IEnumerable<string> lines)
    {
        var opened = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("<<<<<<< ", StringComparison.Ordinal) || line == "<<<<<<<") opened = true;
            else if (opened && (line.StartsWith(">>>>>>> ", StringComparison.Ordinal) || line == ">>>>>>>")) return true;
        }

        return false;
    }
}
