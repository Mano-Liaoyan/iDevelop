using System.Collections.Immutable;
using System.Text;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>What a Merge node's agent reads about a conflict, and whether its files still hold Git's conflict markers.</summary>
internal static class MergeMarkers
{
    /// <summary>
    /// Whether a conflicting file in <paramref name="checkout"/> still holds a conflict: a line that opens Git's markers and
    /// one that closes them. A file the agent deleted holds none.
    /// </summary>
    public static bool Remain(string checkout, IEnumerable<string> paths) => !Remaining(checkout, paths).IsEmpty;

    /// <summary>The conflicting files in <paramref name="checkout"/> that still hold a conflict, in the order given.</summary>
    public static ImmutableArray<string> Remaining(string checkout, IEnumerable<string> paths) =>
        [.. paths.Where(path => File.Exists(RunStorage.SafePath(checkout, path)) && Holds(File.ReadLines(RunStorage.SafePath(checkout, path))))];

    /// <summary>
    /// The conflicting files that hold no conflict markers in <paramref name="conflicted"/>, Git's merge with its markers:
    /// a binary file, a file one side deleted, or a renamed one.
    /// </summary>
    public static ImmutableArray<string> Unmarked(GitRepository repository, CommitId conflicted, IEnumerable<string> paths) =>
        [.. paths.Where(path => repository.FileAt(conflicted, path) is not { } bytes || !Holds(Lines(bytes)))];

    private static IEnumerable<string> Lines(byte[] bytes) => Encoding.UTF8.GetString(bytes).Split('\n').Select(line => line.TrimEnd('\r'));

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

    /// <summary>
    /// The conflict in the merge <paramref name="task"/> starts from: merging the work of the source at <paramref name="step"/>
    /// into the work merged before it.
    /// </summary>
    public static string DescribeJoin(RunRecord record, TaskId task, ImmutableArray<CodeSource> sources, ImmutableArray<CommitId> parents,
        int step, ImmutableArray<string> paths, ImmutableArray<string> unmarked = default)
    {
        var workflow = record.Revision.Snapshot;
        string Titles(IEnumerable<CommitId> commits) => Join([.. sources.Where(source => commits.Contains(source.Commit))
            .Select(source => workflow.Tasks.GetValueOrDefault(source.Task)?.Title ?? "a removed task").Distinct()]);
        var target = workflow.Tasks.GetValueOrDefault(task)?.Title ?? "a removed task";
        var text = new StringBuilder("## The conflict\n\n")
            .Append($"{target} starts from the merge of the work of {Titles(parents)}. ")
            .Append($"Merging the work of {Titles([parents[step]])} into the work of {Titles(parents.Take(step))} conflicts in these files:\n\n");
        return Files(text, paths, unmarked);
    }

    /// <summary>The conflict in the run's final merge: merging the run's work into <paramref name="branch"/>, which changed since the run started.</summary>
    public static string DescribeBranch(string branch, ImmutableArray<string> paths, ImmutableArray<string> unmarked = default)
    {
        var name = BranchName(branch);
        var text = new StringBuilder("## The conflict\n\n")
            .Append($"The run's finished work merges into {name}, the branch it started from. {name} changed since the run started, ")
            .Append($"and merging the run's work into it conflicts in these files:\n\n");
        return Files(text, paths, unmarked);
    }

    /// <summary>A branch's short name, such as <c>main</c> for <c>refs/heads/main</c>.</summary>
    public static string BranchName(string branch) => branch.StartsWith("refs/heads/", StringComparison.Ordinal) ? branch["refs/heads/".Length..] : branch;

    private static string Files(StringBuilder text, ImmutableArray<string> paths, ImmutableArray<string> unmarked)
    {
        foreach (var path in paths) text.Append($"- `{path}`\n");
        if (unmarked.IsDefaultOrEmpty) return text.ToString();
        text.Append("\nThese files conflict without conflict markers, as a binary file, a file one side deleted, or a renamed file does. ")
            .Append("Check each one yourself:\n\n");
        foreach (var path in unmarked) text.Append($"- `{path}`\n");
        return text.ToString();
    }

    private static string Join(IReadOnlyList<string> titles) => titles.Count switch
    {
        0 => "no task",
        1 => titles[0],
        2 => $"{titles[0]} and {titles[1]}",
        _ => string.Join(", ", titles.Take(titles.Count - 1)) + ", and " + titles[^1],
    };
}
