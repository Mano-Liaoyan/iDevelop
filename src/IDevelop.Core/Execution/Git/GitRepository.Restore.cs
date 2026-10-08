using System.Collections.Immutable;

namespace IDevelop.Execution;

internal sealed partial class GitRepository
{
    public GitRead<ImmutableArray<StageEntry>> TreeEntries(TreeId tree)
    {
        var result = Git(ProjectFolder, GitOperation.Metadata, ["ls-tree", "-r", "-z", "--full-tree", tree.Hex]);
        if (result.ExitCode != 0) return Failure<ImmutableArray<StageEntry>>(result);
        var entries = NulFields(result).Select(line =>
        {
            var tab = line.IndexOf('\t');
            var fields = line[..tab].Split(' ');
            return new StageEntry(fields[0], fields[2], 0, line[(tab + 1)..]);
        });
        return new GitRead<ImmutableArray<StageEntry>>.Read([.. entries]);
    }

    public GitRead<byte[]> BlobBytes(string objectId)
    {
        var result = Git(ProjectFolder, GitOperation.Metadata, ["cat-file", "blob", objectId]);
        return result.ExitCode == 0 ? new GitRead<byte[]>.Read(result.Stdout) : Failure<byte[]>(result);
    }

    public GitRead<bool> PlainIndex(string checkout)
    {
        var entries = Git(checkout, GitOperation.Metadata, ["ls-files", "-v", "-z"]);
        if (entries.ExitCode != 0) return Failure<bool>(entries);
        if (NulFields(entries).Any(entry => entry[0] != 'H')) return new GitRead<bool>.Read(false);
        var config = Git(checkout, GitOperation.Metadata, ["config", "--type=bool", "--show-scope", "--get-regexp", "^(core\\.sparsecheckout|core\\.sparsecheckoutcone|index\\.sparse)$"]);
        if (config.ExitCode is not (0 or 1)) return Failure<bool>(config);
        return new GitRead<bool>.Read(!config.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Any(line =>
            !line.StartsWith("command", StringComparison.Ordinal) && (line.EndsWith(" true", StringComparison.OrdinalIgnoreCase) ||
                line.EndsWith(" 1", StringComparison.Ordinal))));
    }
}
