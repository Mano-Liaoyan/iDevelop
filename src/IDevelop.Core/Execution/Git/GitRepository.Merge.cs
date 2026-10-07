using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace IDevelop.Execution;

internal abstract record TreeMerge
{
    private TreeMerge() { }
    internal sealed record Clean(TreeId Tree) : TreeMerge;
    internal sealed record Conflicted(ImmutableArray<StageEntry> Stages, ImmutableArray<MergeMessage> Messages, byte[] Stdout, string Stderr) : TreeMerge;
    internal sealed record Failed(string Detail) : TreeMerge;
}

internal sealed partial class GitRepository
{
    public GitRead<ImmutableArray<DateTimeOffset>> CommitterTimestamps(ImmutableArray<CommitId> commits)
    {
        if (commits.IsEmpty) return new GitRead<ImmutableArray<DateTimeOffset>>.Read([]);
        var result = Git(ProjectFolder, GitOperation.Metadata, ["log", "--no-walk=unsorted", "--format=%H%x09%ct", .. commits.Select(commit => commit.Hex)]);
        if (result.ExitCode != 0) return Failure<ImmutableArray<DateTimeOffset>>(result);
        var lines = result.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != commits.Length)
            return new GitRead<ImmutableArray<DateTimeOffset>>.Failed(MaterializationProblem.GitFailed, "Git returned a different committer timestamp count.");
        var timestamps = ImmutableArray.CreateBuilder<DateTimeOffset>(commits.Length);
        for (var index = 0; index < commits.Length; index++)
        {
            var fields = lines[index].Split('\t');
            if (fields.Length != 2 || !string.Equals(fields[0], commits[index].Hex, StringComparison.OrdinalIgnoreCase) ||
                !long.TryParse(fields[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var seconds) ||
                seconds < DateTimeOffset.MinValue.ToUnixTimeSeconds() || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
                return new GitRead<ImmutableArray<DateTimeOffset>>.Failed(MaterializationProblem.GitFailed, "Git returned malformed or out-of-order committer timestamps.");
            timestamps.Add(DateTimeOffset.FromUnixTimeSeconds(seconds));
        }
        return new GitRead<ImmutableArray<DateTimeOffset>>.Read(timestamps.ToImmutable());
    }

    public static ImmutableArray<ConfigEntry> MergeSettings { get; } =
    [
        new("core.attributesFile", OperatingSystem.IsWindows() ? "NUL" : "/dev/null"),
        new("merge.conflictStyle", "merge"),
        new("merge.renames", "true"),
        new("merge.directoryRenames", "conflict"),
        new("merge.renormalize", "false"),
    ];

    public TreeMerge MergeTrees(CommitId ours, CommitId theirs, CommitId attributeSource)
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"idevelop-merge-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(temporary, "refs"));
            File.WriteAllText(Path.Combine(temporary, "HEAD"), "ref: refs/heads/none\n");
            File.WriteAllText(Path.Combine(temporary, "config"), ours.Hex.Length == 64
                ? "[core]\nbare = true\nrepositoryformatversion = 1\n[extensions]\nobjectFormat = sha256\n"
                : "[core]\nbare = true\nrepositoryformatversion = 0\n");
            var environment = new Dictionary<string, string>
            {
                ["GIT_DIR"] = temporary,
                ["GIT_COMMON_DIR"] = temporary,
                ["GIT_OBJECT_DIRECTORY"] = Path.Combine(CommonDirectory, "objects"),
                ["GIT_SHALLOW_FILE"] = Path.Combine(CommonDirectory, "shallow"),
                ["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
                ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["GIT_ATTR_NOSYSTEM"] = "1",
            };
            var result = Git(ProjectFolder, GitOperation.Worktree,
                [.. MergeSettings.SelectMany(setting => new[] { "-c", setting.Key + "=" + setting.Value }),
                    "--attr-source=" + attributeSource.Hex, "merge-tree", "--write-tree", "-z", "--messages", ours.Hex, theirs.Hex], pinnedGitEnvironment: environment);
            if (result.ExitCode is not (0 or 1) || !ParseMerge(result.Stdout, out var tree, out var stages, out var messages))
                return new TreeMerge.Failed(result.Stderr.Length == 0 ? "Git returned malformed merge-tree output." : result.Stderr);
            return result.ExitCode switch
            {
                0 when stages.IsEmpty => new TreeMerge.Clean(tree),
                0 => new TreeMerge.Failed("Git returned conflict stages for a clean merge."),
                1 => new TreeMerge.Conflicted(stages, messages, result.Stdout, result.Stderr),
                _ => throw new InvalidOperationException(),
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new TreeMerge.Failed(error.Message);
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
        }
    }

    private static bool ParseMerge(byte[] output, out TreeId tree, out ImmutableArray<StageEntry> stages, out ImmutableArray<MergeMessage> messages)
    {
        tree = default;
        stages = [];
        messages = [];
        if (output.Length == 0 || output[^1] != 0) return false;
        var text = Encoding.UTF8.GetString(output);
        var fields = text.Split('\0');
        if (!MergeObject(fields[0])) return false;
        tree = new(fields[0]);
        var entries = ImmutableArray.CreateBuilder<StageEntry>();
        var index = 1;
        while (index < fields.Length - 1 && fields[index].Length != 0)
        {
            var field = fields[index++];
            var tab = field.IndexOf('\t');
            if (tab < 0 || tab == field.Length - 1) return false;
            var header = field[..tab].Split(' ');
            if (header.Length != 3 || (header[0].Length != 6 || !header[0].All(character => character is >= '0' and <= '7')) ||
                !MergeObject(header[1]) || header[2] is not ("1" or "2" or "3")) return false;
            entries.Add(new(header[0], header[1], int.Parse(header[2], CultureInfo.InvariantCulture), field[(tab + 1)..]));
        }
        if (index >= fields.Length - 1) return false;
        index++;
        var information = ImmutableArray.CreateBuilder<MergeMessage>();
        while (index < fields.Length - 1)
        {
            if (!int.TryParse(fields[index++], NumberStyles.None, CultureInfo.InvariantCulture, out var count) ||
                count < 0 || count > fields.Length - index - 3) return false;
            var paths = fields.AsSpan(index, count).ToArray();
            if (paths.Any(path => path.Length == 0)) return false;
            index += count;
            var type = fields[index++];
            if (type.Length == 0) return false;
            information.Add(new([.. paths], type, fields[index++]));
        }
        stages = entries.ToImmutable();
        messages = information.ToImmutable();
        return index == fields.Length - 1;
    }

    private static bool MergeObject(string text) => text.Length is 40 or 64 && text.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
}
