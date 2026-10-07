using System.Diagnostics;
using System.Text;

namespace IDevelop.Execution;

/// <summary>
/// The project's files as Git trees. A snapshot stages every file that Git does not ignore into a temporary copy of the
/// index and writes it as a tree, so it leaves the person's index, branch, and work tree as they were. The project's
/// <c>.idp</c> folder stays out, so saving the workflow during a turn changes no snapshot. Every method returns null
/// when the folder is not in a Git work tree or Git fails.
/// </summary>
internal static class GitTree
{
    /// <summary>A reviewer reads at most this much of a diff, and a note says where the rest is.</summary>
    private const int DiffLimit = 200_000;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    public static string? Snapshot(string folder)
    {
        if (Git(folder, null, "rev-parse", "--git-path", "index") is not { } indexPath)
        {
            return null;
        }

        var index = Path.GetFullPath(Path.Combine(folder, indexPath.Trim()));
        var temporary = Path.Combine(Path.GetTempPath(), $"idevelop-index-{Guid.NewGuid():N}");
        try
        {
            if (File.Exists(index))
            {
                // A copy keeps the index's file times, so Git hashes only the files that changed.
                File.Copy(index, temporary);
            }

            return Git(folder, temporary, "add", "--all", "--", ".", ":(exclude).idp") is null
                ? null
                : Git(folder, temporary, "write-tree")?.Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The change from one tree to another as a unified diff, cut at <see cref="DiffLimit"/> characters.</summary>
    public static string? Diff(string folder, string from, string to)
    {
        if (Git(folder, null, "diff", "--no-color", "--no-ext-diff", "--no-textconv", from, to) is not { } diff)
        {
            return null;
        }

        return diff.Length <= DiffLimit
            ? diff
            : $"{diff[..DiffLimit]}\n… The diff goes on. Run git diff {from} {to} in the project to read all of it.";
    }

    /// <summary>The recursive entries of a tree or commit outside <c>.idp</c>, sorted, so two snapshots compare by content.</summary>
    internal static string? ContentOutsideData(string folder, string treeOrCommit)
    {
        if (!Revision.IsCommit(treeOrCommit) || Git(folder, null, "ls-tree", "-r", "-z", "--full-tree", treeOrCommit) is not { } entries)
        {
            return null;
        }

        return string.Join('\0', entries.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(entry => entry.IndexOf('\t') is var tab && tab >= 0 &&
                entry[(tab + 1)..] != ".idp" && !entry[(tab + 1)..].StartsWith(".idp/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal));
    }

    private static string? Git(string folder, string? indexFile, params string[] arguments)
    {
        var start = new ProcessStartInfo("git", ["-c", "advice.graftFileDeprecated=false", "-c", "core.commitGraph=false", .. arguments])
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
        start.Environment["GIT_GRAFT_FILE"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        if (indexFile is not null)
        {
            start.Environment["GIT_INDEX_FILE"] = indexFile;
        }

        try
        {
            using var git = Process.Start(start);
            if (git is null)
            {
                return null;
            }

            git.StandardInput.Close();
            var output = git.StandardOutput.ReadToEndAsync();
            _ = git.StandardError.ReadToEndAsync();
            if (!git.WaitForExit(Patience))
            {
                git.Kill(entireProcessTree: true);
                return null;
            }

            return git.ExitCode == 0 ? output.Result : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
