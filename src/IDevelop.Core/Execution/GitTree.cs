namespace IDevelop.Execution;

/// <summary>
/// The project's files as Git trees. A snapshot stages every file that Git does not ignore into a temporary copy of the
/// index and writes it as a tree, so it leaves the person's index, branch, and work tree as they were. The project's
/// <c>.idp</c> folder stays out, so saving the workflow during a turn changes no snapshot. Every method returns null
/// when the folder is not in a Git work tree or Git fails. A snapshot also returns null when the index hides changes
/// with assume-unchanged or skip-worktree.
/// </summary>
internal static class GitTree
{
    /// <summary>A reviewer reads at most this much of a diff, and a note says where the rest is.</summary>
    private const int DiffLimit = 200_000;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);
    private static readonly GitLimits Limits = new(Patience, Patience, Patience);
    private static readonly IReadOnlyDictionary<string, string> Environment = new Dictionary<string, string>();

    public static string? Snapshot(string folder) => GitRepository.Snapshot(folder, [".idp"], Environment, Limits) is GitRead<TreeId>.Read tree
        ? tree.Value.Hex
        : null;

    /// <summary>The change from one tree to another as a unified diff, cut at <see cref="DiffLimit"/> characters.</summary>
    public static string? Diff(string folder, string from, string to)
    {
        var result = GitRepository.Run(["diff", "--no-color", "--no-ext-diff", "--no-textconv", from, to], folder, GitOperation.Worktree, Limits, Environment);
        if (result.ExitCode != 0)
        {
            return null;
        }

        var diff = result.Text;
        return diff.Length <= DiffLimit
            ? diff
            : $"{diff[..DiffLimit]}\n… The diff goes on. Run git diff {from} {to} in the project to read all of it.";
    }

    /// <summary>The recursive entries of a tree or commit outside <c>.idp</c>, sorted, so two snapshots compare by content.</summary>
    internal static string? ContentOutsideData(string folder, string treeOrCommit)
    {
        if (!Revision.IsCommit(treeOrCommit)) return null;
        var result = GitRepository.Run(["ls-tree", "-r", "-z", "--full-tree", treeOrCommit], folder, GitOperation.Metadata, Limits, Environment);
        if (result.ExitCode != 0)
        {
            return null;
        }

        return string.Join('\0', result.Text.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(entry => entry.IndexOf('\t') is var tab && tab >= 0 &&
                entry[(tab + 1)..] != ".idp" && !entry[(tab + 1)..].StartsWith(".idp/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal));
    }
}
