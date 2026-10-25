using System.Security.Cryptography;
using System.Text;

namespace IDevelop.Execution;

internal static class RunLayout
{
    public static string Key(Guid id, IReadOnlySet<string> taken)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id.ToString("D"))));
        var length = 8;
        while (taken.Contains(hash[..length]) && length < hash.Length)
        {
            length++;
        }
        if (taken.Contains(hash[..length]))
        {
            throw new InvalidOperationException("The full identity hash is already allocated.");
        }
        return hash[..length];
    }

    public static string TaskCheckout(string run, string task) => $".worktrees/{run}/{task}";

    public static string TaskBranch(string run, string task) => $"refs/heads/{TaskBranchShortName(run, task)}";

    public static string TaskBranchShortName(string run, string task) => $"idp/{run}/task/{task}";

    public static string JoinBranch(string run, string task) => $"refs/heads/idp/{run}/join/{task}";

    public static string ResalvageRef(string run, string task, AttemptId attempt, OperationId operation) =>
        $"refs/idp/{run}/resalvage/{task}/{attempt.Value:D}/{operation.Value:D}";

    public static string PinPrefix(string run) => $"refs/idp/{run}/pin/";

    public static string RootPin(string run, string task, LaunchKey launch) =>
        $"{PinPrefix(run)}{task}/{launch.Attempt.Value:D}/{launch.Turn}/root";

    public static string CapturePin(string run, string task, LaunchKey launch) =>
        $"{PinPrefix(run)}{task}/{launch.Attempt.Value:D}/{launch.Turn}/capture";

    public static string CaptureIndexPin(string run, string task, LaunchKey launch) =>
        $"{PinPrefix(run)}{task}/{launch.Attempt.Value:D}/{launch.Turn}/capture-index";

    /// <summary>
    /// The worktree where the person resolves a merge conflict that no Merge node takes, detached at the commit with the
    /// conflict markers, <paramref name="conflicted"/> (#148).
    /// </summary>
    public static string ConflictCheckout(string run, CommitId conflicted) => $".worktrees/{run}/conflict-{conflicted.Hex[..12]}";

    /// <summary>The pin that keeps the merge the person committed for the conflict at <paramref name="conflicted"/>.</summary>
    public static string ConflictPin(string run, CommitId conflicted) => $"{PinPrefix(run)}conflict/{conflicted.Hex}";

    /// <summary>The pin that keeps the commit with the conflict markers at <paramref name="conflicted"/>, which a conflict's worktree starts at.</summary>
    public static string ConflictedPin(string run, CommitId conflicted) => $"{PinPrefix(run)}conflicted/{conflicted.Hex}";

    /// <summary>
    /// The pin that keeps <paramref name="commit"/>, the code of a result the run carries from an earlier run, while the run
    /// uses it, whatever becomes of the earlier run's branch.
    /// </summary>
    public static string CarriedPin(string run, CommitId commit) => $"{PinPrefix(run)}carried/{commit.Hex}";

    public static string ApprovedBase(string run) => $"refs/idp/{run}/base";

    public static string Outbox(AttemptId attempt) => $".idp/outbox/{attempt.Value:D}";

    public static string InputFolder(InputId input) => $".idp/inputs/{input.Value:D}";

    public static IReadOnlySet<string> UsedRunKeys(IReadOnlyDictionary<string, CommitId> refs)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in refs.Keys)
        {
            var prefix = name.StartsWith("refs/heads/idp/", StringComparison.Ordinal) ? "refs/heads/idp/"
                : name.StartsWith("refs/idp/", StringComparison.Ordinal) ? "refs/idp/" : null;
            if (prefix is not null)
            {
                var end = name.IndexOf('/', prefix.Length);
                var key = end < 0 ? name[prefix.Length..] : name[prefix.Length..end];
                if (key.Length != 0) keys.Add(key);
            }
        }
        return keys;
    }
}
