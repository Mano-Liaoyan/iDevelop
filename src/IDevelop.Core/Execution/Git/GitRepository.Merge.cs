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

    public TreeMerge MergeTrees(CommitId ours, CommitId theirs)
    {
        var result = Git(ProjectFolder, GitOperation.Worktree, ["merge-tree", "--write-tree", "-z", "--messages", ours.Hex, theirs.Hex]);
        if (result.ExitCode is not (0 or 1) || !ParseMerge(result.Stdout, out var tree, out var stages, out var messages))
        {
            return new TreeMerge.Failed(result.Stderr.Length == 0 ? "Git returned malformed merge-tree output." : result.Stderr);
        }
        return result.ExitCode switch
        {
            0 when stages.IsEmpty => new TreeMerge.Clean(tree),
            0 => new TreeMerge.Failed("Git returned conflict stages for a clean merge."),
            1 => new TreeMerge.Conflicted(stages, messages, result.Stdout, result.Stderr),
            _ => throw new InvalidOperationException(),
        };
    }

    public GitRead<ImmutableArray<ConfigEntry>> MergeConfig()
    {
        var result = Git(ProjectFolder, GitOperation.Metadata, ["config", "-z", "--get-regexp",
            @"^(merge\.|diff\.(renames|renamelimit|algorithm)$|core\.(autocrlf|eol|safecrlf|ignorecase|attributesfile)$)"]);
        if (result.ExitCode == 1 && result.Stdout.Length == 0) return new GitRead<ImmutableArray<ConfigEntry>>.Read([]);
        if (result.ExitCode != 0) return Failure<ImmutableArray<ConfigEntry>>(result);
        var entries = ImmutableArray.CreateBuilder<ConfigEntry>();
        foreach (var field in NulFields(result))
        {
            var newline = field.IndexOf('\n');
            if (newline <= 0) return new GitRead<ImmutableArray<ConfigEntry>>.Failed(MaterializationProblem.GitFailed, "Git returned malformed configuration output.");
            entries.Add(new(field[..newline], field[(newline + 1)..]));
        }
        return new GitRead<ImmutableArray<ConfigEntry>>.Read(entries.ToImmutable());
    }

    private static bool ParseMerge(byte[] output, out TreeId tree, out ImmutableArray<StageEntry> stages, out ImmutableArray<MergeMessage> messages)
    {
        tree = default;
        stages = [];
        messages = [];
        if (output.Length == 0 || output[^1] != 0) return false;
        string text;
        try { text = new UTF8Encoding(false, true).GetString(output); }
        catch (DecoderFallbackException) { return false; }
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
