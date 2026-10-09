using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using IDevelop.Projects;

namespace IDevelop.Execution;

internal sealed record GitResult(int ExitCode, byte[] Stdout, string Stderr)
{
    public string Text => Encoding.UTF8.GetString(Stdout);
}

internal abstract record GitRead<T>
{
    private GitRead() { }
    internal sealed record Read(T Value) : GitRead<T>;
    internal sealed record Failed(MaterializationProblem Problem, string Detail) : GitRead<T>
    {
        public ImmutableArray<string> Refs { get; init; } = [];
    }
}

internal abstract record RepositoryOpen
{
    private RepositoryOpen() { }
    internal sealed record Opened(GitRepository Repository) : RepositoryOpen;
    internal sealed record Refused(MaterializationProblem Problem, string Detail) : RepositoryOpen;
}

internal sealed record GitCommit(TreeId Tree, ImmutableArray<CommitId> Parents);
internal sealed record GitWorktree(string Path, CommitId? Head, string? Branch, bool Locked, string? LockReason);
internal sealed record GitCapture(TreeId Tree, Digest? IndexBefore, Digest? IndexAfter);

internal abstract record GitAncestry
{
    private GitAncestry() { }
    internal sealed record Yes : GitAncestry;
    internal sealed record No : GitAncestry;
    internal sealed record Failed(string Detail) : GitAncestry;
}

internal abstract record RefMove
{
    private RefMove() { }
    internal sealed record Moved : RefMove;
    internal sealed record AlreadyAtTarget : RefMove;
    internal sealed record Conflict(CommitId? Observed) : RefMove;
    internal sealed record Failed(string Detail) : RefMove;
}

internal abstract record IndexAlignment
{
    private IndexAlignment() { }
    internal sealed record Aligned : IndexAlignment;
    internal sealed record AlreadyAligned : IndexAlignment;
    internal sealed record Unexpected(Digest? Observed) : IndexAlignment;
    internal sealed record Failed(string Detail) : IndexAlignment;
}

internal enum GitPathType { RegularFile, Directory, Link, Other }
internal enum PathRemoval { Removed, Absent, Unexpected }
internal enum GitOperation { Metadata, Worktree, Network }
internal sealed record GitLimits(TimeSpan Metadata, TimeSpan Worktree, TimeSpan Network)
{
    public static GitLimits Default { get; } = new(TimeSpan.FromSeconds(60), TimeSpan.FromHours(1), TimeSpan.FromHours(4));
}

internal sealed partial class GitRepository
{
    /// <summary>How long a timed-out call waits for Git to stop and for its pipes to close.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);

    private readonly GitLimits _limits;
    private readonly IReadOnlyDictionary<string, string> _environment;

    private GitRepository(string projectFolder, string commonDirectory, string version, IReadOnlyDictionary<string, string> environment, GitLimits limits)
    {
        ProjectFolder = projectFolder;
        CommonDirectory = commonDirectory;
        Version = version;
        _environment = new Dictionary<string, string>(environment);
        _limits = limits;
    }

    public string ProjectFolder { get; }
    public string CommonDirectory { get; }
    public string Version { get; }

    public static RepositoryOpen Open(string projectFolder) => Open(projectFolder, new Dictionary<string, string>());

    internal static RepositoryOpen Open(string projectFolder, IReadOnlyDictionary<string, string> environment, GitLimits? limits = null)
    {
        limits ??= GitLimits.Default;
        var version = Run(["version"], projectFolder, GitOperation.Metadata, limits, environment);
        if (version.ExitCode != 0)
        {
            return new RepositoryOpen.Refused(MaterializationProblem.GitFailed, version.Stderr);
        }
        if (CheckVersion(version.Text) is { } refusal)
        {
            return refusal;
        }
        var prefix = Run(["rev-parse", "--show-prefix"], projectFolder, GitOperation.Metadata, limits, environment);
        if (prefix.ExitCode != 0 || prefix.Text.TrimEnd('\r', '\n').Length != 0)
        {
            var root = Run(["rev-parse", "--show-toplevel"], projectFolder, GitOperation.Metadata, limits, environment);
            return new RepositoryOpen.Refused(MaterializationProblem.NotRepositoryRoot,
                root.ExitCode == 0 ? root.Text.TrimEnd('\r', '\n') : prefix.Stderr);
        }
        var toplevel = Run(["rev-parse", "--show-toplevel"], projectFolder, GitOperation.Metadata, limits, environment);
        var common = Run(["rev-parse", "--path-format=absolute", "--git-common-dir"], projectFolder, GitOperation.Metadata, limits, environment);
        if (toplevel.ExitCode != 0 || common.ExitCode != 0)
        {
            return new RepositoryOpen.Refused(MaterializationProblem.GitFailed, toplevel.Stderr + common.Stderr);
        }
        return new RepositoryOpen.Opened(new(toplevel.Text.TrimEnd('\r', '\n'), common.Text.TrimEnd('\r', '\n'),
            version.Text.TrimEnd('\r', '\n'), environment, limits));
    }

    internal static Version? ParseVersion(string text)
    {
        var match = Regex.Match(text, @"^git version (\d+)\.(\d+)\.(\d+)(?:\D|$)", RegexOptions.CultureInvariant);
        return match.Success && System.Version.TryParse($"{match.Groups[1]}.{match.Groups[2]}.{match.Groups[3]}", out var version)
            ? version : null;
    }

    internal static RepositoryOpen.Refused? CheckVersion(string text) => ParseVersion(text) is not { } version || version < new Version(2, 39, 0)
        ? new(MaterializationProblem.GitVersionUnsupported, text.TrimEnd('\r', '\n')) : null;

    /// <summary>How long a step waits for the mutation lock unless it names its own patience.</summary>
    internal static readonly TimeSpan MutationPatience = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The repository's mutation lock, or null when another holder, in this process or another, keeps it for longer than
    /// <paramref name="patience"/>, <see cref="MutationPatience"/> by default. The persistent lock file is never deleted,
    /// so concurrent owners cannot lock different files at the same path.
    /// </summary>
    public FileStream? TakeMutationLock(TimeSpan? patience = null)
    {
        var limit = patience ?? MutationPatience;
        var folder = Directory.CreateDirectory(Path.Combine(CommonDirectory, "idevelop")).FullName;
        var waited = Stopwatch.StartNew();
        do
        {
            try
            {
                return new FileStream(Path.Combine(folder, "mutation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                Thread.Sleep(20);
            }
        } while (waited.Elapsed < limit);
        return null;
    }

    public GitRead<CommitId?> ResolveCommit(string revision)
    {
        var result = Git(ProjectFolder, GitOperation.Metadata, ["rev-parse", "--verify", "--quiet", revision + "^{commit}"]);
        return result.ExitCode switch
        {
            0 => new GitRead<CommitId?>.Read(new(result.Text.Trim())),
            1 => new GitRead<CommitId?>.Read(null),
            _ => Failure<CommitId?>(result),
        };
    }

    public GitRead<CommitId?> ReadRef(string name)
    {
        var symbolic = Git(ProjectFolder, GitOperation.Metadata, ["symbolic-ref", "--quiet", name]);
        if (symbolic.ExitCode == 0)
            return new GitRead<CommitId?>.Failed(MaterializationProblem.UncertainOwnership,
                $"Ref {name} is symbolic to {symbolic.Text.TrimEnd('\r', '\n')}.") { Refs = [name] };
        if (symbolic.ExitCode != 1) return Failure<CommitId?>(symbolic) with { Refs = [name] };
        var result = Git(ProjectFolder, GitOperation.Metadata, ["rev-parse", "--verify", "--quiet", name]);
        return result.ExitCode switch
        {
            0 => new GitRead<CommitId?>.Read(new(result.Text.Trim())),
            1 => new GitRead<CommitId?>.Read(null),
            _ => Failure<CommitId?>(result) with { Refs = [name] },
        };
    }

    public GitRead<GitCommit> ReadCommit(CommitId commit)
    {
        var result = Git(ProjectFolder, GitOperation.Metadata, ["cat-file", "commit", commit.Hex]);
        if (result.ExitCode != 0)
        {
            return Failure<GitCommit>(result);
        }
        var headers = result.Text.Split('\n').TakeWhile(line => line.Length != 0).ToArray();
        return new GitRead<GitCommit>.Read(new(new(headers.Single(line => line.StartsWith("tree ", StringComparison.Ordinal))[5..]),
            [.. headers.Where(line => line.StartsWith("parent ", StringComparison.Ordinal)).Select(line => new CommitId(line[7..]))]));
    }

    public GitRead<string> ObjectType(string objectId) => ReadText(Git(ProjectFolder, GitOperation.Metadata, ["cat-file", "-t", objectId]), trim: true);

    public GitAncestry IsAncestor(CommitId ancestor, CommitId descendant)
    {
        var result = Git(ProjectFolder, GitOperation.Metadata, ["merge-base", "--is-ancestor", ancestor.Hex, descendant.Hex]);
        return result.ExitCode switch { 0 => new GitAncestry.Yes(), 1 => new GitAncestry.No(), _ => new GitAncestry.Failed(result.Stderr) };
    }

    public GitRead<string?> SymbolicHead(string checkout)
    {
        var result = Git(checkout, GitOperation.Metadata, ["symbolic-ref", "--quiet", "HEAD"]);
        return result.ExitCode switch
        {
            0 => new GitRead<string?>.Read(result.Text.TrimEnd('\r', '\n')),
            1 => new GitRead<string?>.Read(null),
            _ => Failure<string?>(result),
        };
    }

    public GitRead<CommitId?> ResolveCheckoutHead(string checkout)
    {
        var result = Git(checkout, GitOperation.Metadata, ["rev-parse", "--verify", "--quiet", "HEAD"]);
        return result.ExitCode switch
        {
            0 => new GitRead<CommitId?>.Read(new(result.Text.Trim())),
            1 => new GitRead<CommitId?>.Read(null),
            _ => Failure<CommitId?>(result),
        };
    }

    public GitResult AttachHead(string checkout, string branch) => Git(checkout, GitOperation.Metadata, ["symbolic-ref", "HEAD", branch]);

    public GitRead<ImmutableArray<StageEntry>> UnmergedEntries(string checkout)
    {
        var result = Git(checkout, GitOperation.Worktree, ["ls-files", "-u", "-z"]);
        if (result.ExitCode != 0)
        {
            return Failure<ImmutableArray<StageEntry>>(result);
        }
        var entries = NulFields(result).Select(entry =>
        {
            var tab = entry.IndexOf('\t');
            var header = entry[..tab].Split(' ');
            return new StageEntry(header[0], header[1], int.Parse(header[2], CultureInfo.InvariantCulture), entry[(tab + 1)..]);
        });
        return new GitRead<ImmutableArray<StageEntry>>.Read([.. entries]);
    }

    public GitRead<byte[]> Status(string checkout)
    {
        var index = VisibleIndex(checkout, _environment, _limits);
        if (index is GitRead<byte[]>.Failed) return index;
        var result = Git(checkout, GitOperation.Worktree, ["status", "--porcelain=v1", "-z", "--untracked-files=all"]);
        return result.ExitCode == 0 ? new GitRead<byte[]>.Read(result.Stdout) : Failure<byte[]>(result);
    }

    public GitRead<ImmutableArray<string>> UntrackedFiles(string checkout) => ReadPaths(Git(checkout, GitOperation.Worktree, ["ls-files", "--others", "--exclude-standard", "-z"]));

    public GitRead<ImmutableArray<string>> IgnoredFiles(string checkout) =>
        ReadPaths(Git(checkout, GitOperation.Worktree, ["ls-files", "--others", "--ignored", "--exclude-standard", "-z"]));

    /// <summary>The ignored entries of a work tree, a wholly ignored folder as one entry ending in a slash.</summary>
    public GitRead<ImmutableArray<string>> IgnoredEntries(string checkout) =>
        ReadPaths(Git(checkout, GitOperation.Worktree, ["ls-files", "--others", "--ignored", "--exclude-standard", "--directory", "-z"]));

    public GitRead<ImmutableArray<string>> TreeFiles(CommitId commit) =>
        ReadPaths(Git(ProjectFolder, GitOperation.Metadata, ["ls-tree", "-r", "--name-only", "-z", commit.Hex]));

    public GitRead<SortedDictionary<string, CommitId>> RefSnapshot(params string[] patterns) => RefSnapshot(patterns, null);

    public GitRead<SortedDictionary<string, CommitId>> RefSnapshot(string[] patterns, string? excludedPrefix)
    {
        var result = Git(ProjectFolder, GitOperation.Metadata, ["for-each-ref", "--format=%(refname)%00%(objectname)%00%(symref)", .. patterns]);
        if (result.ExitCode != 0)
        {
            return Failure<SortedDictionary<string, CommitId>>(result);
        }
        var refs = new SortedDictionary<string, CommitId>(StringComparer.Ordinal);
        foreach (var line in result.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('\0');
            if (excludedPrefix is not null && fields[0].StartsWith(excludedPrefix, StringComparison.Ordinal)) continue;
            if (fields[2].Length != 0)
                return new GitRead<SortedDictionary<string, CommitId>>.Failed(MaterializationProblem.UncertainOwnership,
                    $"Ref {fields[0]} is symbolic to {fields[2]}.") { Refs = [fields[0]] };
            refs.Add(fields[0], new(fields[1]));
        }
        return new GitRead<SortedDictionary<string, CommitId>>.Read(refs);
    }

    public GitResult CheckIgnore(string checkout, string path) => Git(checkout, GitOperation.Metadata, ["check-ignore", "-q", "--", path]);

    public GitRead<ImmutableArray<string>> TrackedFiles(string checkout, params string[] paths) => ReadPaths(Git(checkout, GitOperation.Worktree, ["ls-files", "-z", "--", .. paths]));

    public GitRead<ImmutableArray<GitWorktree>> Worktrees()
    {
        var result = Git(ProjectFolder, GitOperation.Metadata, ["worktree", "list", "--porcelain", "-z"]);
        if (result.ExitCode != 0)
        {
            return Failure<ImmutableArray<GitWorktree>>(result);
        }
        var worktrees = ImmutableArray.CreateBuilder<GitWorktree>();
        string? path = null, branch = null, reason = null;
        CommitId? head = null;
        var locked = false;
        foreach (var field in result.Text.Split('\0'))
        {
            if (field.Length == 0 && path is not null)
            {
                worktrees.Add(new(path, head, branch, locked, reason));
                path = branch = reason = null;
                head = null;
                locked = false;
            }
            else if (field.StartsWith("worktree ", StringComparison.Ordinal)) path = field[9..];
            else if (field.StartsWith("HEAD ", StringComparison.Ordinal)) head = new(field[5..]);
            else if (field.StartsWith("branch ", StringComparison.Ordinal)) branch = field[7..];
            else if (field == "locked" || field.StartsWith("locked ", StringComparison.Ordinal))
            {
                locked = true;
                reason = field.Length > 6 ? field[7..] : null;
            }
        }
        return new GitRead<ImmutableArray<GitWorktree>>.Read(worktrees.ToImmutable());
    }

    public GitRead<string> CheckoutCommonDirectory(string checkout) =>
        ReadText(Git(checkout, GitOperation.Metadata, ["rev-parse", "--path-format=absolute", "--git-common-dir"]), trim: true);

    public GitRead<string> IndexPath(string checkout) => ReadText(Git(checkout, GitOperation.Metadata, ["rev-parse", "--path-format=absolute", "--git-path", "index"]), trim: true);

    public GitRead<ImmutableArray<string>> DiffTreePaths(TreeId first, TreeId second)
    {
        var result = Git(ProjectFolder, GitOperation.Metadata, ["diff-tree", "-r", "--name-only", "-z", first.Hex, second.Hex]);
        return result.ExitCode == 0
            ? new GitRead<ImmutableArray<string>>.Read([.. NulFields(result).Order(StringComparer.Ordinal)])
            : Failure<ImmutableArray<string>>(result);
    }

    public GitRead<Digest?> IndexDigest(string checkout) => IndexBytes(checkout) switch
    {
        GitRead<byte[]?>.Read read => new GitRead<Digest?>.Read(read.Value is { } bytes ? Revision.Hash(bytes) : null),
        GitRead<byte[]?>.Failed failed => new GitRead<Digest?>.Failed(failed.Problem, failed.Detail),
        _ => throw new InvalidOperationException(),
    };

    public GitRead<byte[]?> IndexBytes(string checkout)
    {
        var index = IndexPath(checkout);
        if (index is not GitRead<string>.Read path) return ConvertFailure<string, byte[]?>(index);
        try
        {
            if (!File.Exists(path.Value)) return new GitRead<byte[]?>.Read(null);
            var staged = Git(checkout, GitOperation.Metadata, ["ls-files", "--stage", "-v", "-z"]);
            return staged.ExitCode == 0 ? new GitRead<byte[]?>.Read(staged.Stdout) : Failure<byte[]?>(staged);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new GitRead<byte[]?>.Failed(MaterializationProblem.GitFailed, error.Message);
        }
    }

    public GitRead<GitCapture> Capture(string checkout, params string[] excludedPaths)
    {
        var before = IndexDigest(checkout);
        if (before is not GitRead<Digest?>.Read initial) return ConvertFailure<Digest?, GitCapture>(before);
        var visible = VisibleIndex(checkout, _environment, _limits);
        if (visible is GitRead<byte[]>.Failed) return ConvertFailure<byte[], GitCapture>(visible);
        var snapshot = WriteTree(checkout, [".idp", ".worktrees", .. excludedPaths], _environment, _limits);
        if (snapshot is not GitRead<TreeId>.Read tree) return ConvertFailure<TreeId, GitCapture>(snapshot);
        var after = IndexDigest(checkout);
        return after is GitRead<Digest?>.Read final
            ? new GitRead<GitCapture>.Read(new(tree.Value, initial.Value, final.Value))
            : ConvertFailure<Digest?, GitCapture>(after);
    }

    /// <summary>
    /// <see cref="GitTree"/>'s snapshot. Unlike capture, it takes the work tree's content under assume-unchanged and
    /// skip-worktree entries. It refuses a skip-worktree file missing from the work tree, as a sparse checkout leaves it,
    /// because the rebuilt index would record that file as deleted.
    /// </summary>
    internal static GitRead<TreeId> Snapshot(string folder, IEnumerable<string> excludedPaths,
        IReadOnlyDictionary<string, string> environment, GitLimits limits)
    {
        var listed = Run(["ls-files", "-v", "-z", "--", .. Pathspec(excludedPaths)], folder, GitOperation.Worktree, limits, environment);
        if (listed.ExitCode != 0) return Failure<TreeId>(listed);
        foreach (var path in NulFields(listed).Where(entry => char.ToUpperInvariant(entry[0]) == 'S').Select(entry => entry[2..]))
        {
            var full = Path.Combine(folder, path);
            if (!Path.Exists(full))
                return new GitRead<TreeId>.Failed(MaterializationProblem.DirtyWorktree, $"The index marks {path} skip-worktree, and the work tree has no such file.");
        }
        return WriteTree(folder, excludedPaths, environment, limits);
    }

    private static string[] Pathspec(IEnumerable<string> excludedPaths) =>
        [".", .. excludedPaths.Distinct(StringComparer.Ordinal).Select(exclusion => ":(exclude)" + exclusion)];

    private static GitRead<TreeId> WriteTree(string folder, IEnumerable<string> excludedPaths,
        IReadOnlyDictionary<string, string> environment, GitLimits limits)
    {
        var index = ReadText(Run(["rev-parse", "--path-format=absolute", "--git-path", "index"], folder, GitOperation.Metadata, limits, environment), trim: true);
        if (index is not GitRead<string>.Read path) return ConvertFailure<string, TreeId>(index);
        var temporary = Path.Combine(Path.GetTempPath(), $"idevelop-index-{Guid.NewGuid():N}");
        try
        {
            if (File.Exists(path.Value)) File.Copy(path.Value, temporary);
            var snapshotEnvironment = new Dictionary<string, string>(environment) { ["GIT_INDEX_FILE"] = temporary };
            var pathspec = Pathspec(excludedPaths);
            var entries = Run(["ls-files", "--stage", "-z", "--full-name", "--", .. pathspec], folder, GitOperation.Worktree, limits, snapshotEnvironment);
            if (entries.ExitCode != 0) return Failure<TreeId>(entries);
            var invalidated = Run(["update-index", "-z", "--index-info"], folder, GitOperation.Worktree, limits, snapshotEnvironment, entries.Stdout);
            if (invalidated.ExitCode != 0) return Failure<TreeId>(invalidated);
            var added = Run(["add", "--all", "--", .. pathspec], folder, GitOperation.Worktree, limits, snapshotEnvironment);
            if (added.ExitCode != 0) return Failure<TreeId>(added);
            var tree = Run(["write-tree"], folder, GitOperation.Worktree, limits, snapshotEnvironment);
            return tree.ExitCode == 0 ? new GitRead<TreeId>.Read(new(tree.Text.Trim())) : Failure<TreeId>(tree);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new GitRead<TreeId>.Failed(MaterializationProblem.GitFailed, error.Message);
        }
        finally
        {
            DeleteTemporary(temporary);
            DeleteTemporary(temporary + ".lock");
        }
    }

    /// <summary>A file that a scanner holds open on Windows stays in the temporary folder rather than failing the call.</summary>
    private static void DeleteTemporary(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    public GitRead<TreeId> WriteTree(IEnumerable<StageEntry> entries)
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"idevelop-index-{Guid.NewGuid():N}");
        try
        {
            var environment = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = temporary };
            var bytes = Encoding.UTF8.GetBytes(string.Concat(entries.Select(entry =>
                $"{entry.Mode} {entry.Object} {entry.Stage.ToString(CultureInfo.InvariantCulture)}\t{entry.Path}\0")));
            var indexed = Git(ProjectFolder, GitOperation.Worktree, ["update-index", "-z", "--index-info"], environment, bytes);
            if (indexed.ExitCode != 0) return Failure<TreeId>(indexed);
            var tree = Git(ProjectFolder, GitOperation.Worktree, ["write-tree"], environment);
            return tree.ExitCode == 0 ? new GitRead<TreeId>.Read(new(tree.Text.Trim())) : Failure<TreeId>(tree);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new GitRead<TreeId>.Failed(MaterializationProblem.GitFailed, error.Message);
        }
        finally
        {
            File.Delete(temporary);
            File.Delete(temporary + ".lock");
        }
    }

    internal static ImmutableArray<StageEntry> ParseIndex(byte[] bytes)
    {
        var entries = ImmutableArray.CreateBuilder<StageEntry>();
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException error) { throw new IOException("The recorded index has a non-UTF-8 path.", error); }
        var offset = 0;
        while (offset < text.Length)
        {
            var end = text.IndexOf('\0', offset);
            if (end < 0) throw new IOException("The recorded index has an incomplete entry.");
            var tab = text.IndexOf('\t', offset, end - offset);
            if (tab < 0) throw new IOException("The recorded index has an invalid entry.");
            var header = text[offset..tab].Split(' ');
            if (header.Length != 4 || header[0].Length != 1 || header[1].Length != 6 ||
                !header[1].All(character => character is >= '0' and <= '7') || !Revision.IsCommit(header[2]) ||
                !int.TryParse(header[3], NumberStyles.None, CultureInfo.InvariantCulture, out var stage) || stage is < 0 or > 3 || tab + 1 == end)
                throw new IOException("The recorded index has an invalid entry.");
            if (stage == 0) entries.Add(new(header[1], header[2], stage, text[(tab + 1)..end]));
            offset = end + 1;
        }
        return entries.ToImmutable();
    }

    public GitRead<CommitId> CreateCommit(CommitRecipe recipe)
    {
        var author = Identity(recipe.Author);
        var committer = Identity(recipe.Committer);
        if (author is null || committer is null)
        {
            return new GitRead<CommitId>.Failed(MaterializationProblem.GitFailed, "Commit identities must have the form Name <email>.");
        }
        var date = $"@{recipe.Timestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)} +0000";
        var environment = new Dictionary<string, string>
        {
            ["GIT_AUTHOR_NAME"] = author.Value.Name, ["GIT_AUTHOR_EMAIL"] = author.Value.Email, ["GIT_AUTHOR_DATE"] = date,
            ["GIT_COMMITTER_NAME"] = committer.Value.Name, ["GIT_COMMITTER_EMAIL"] = committer.Value.Email, ["GIT_COMMITTER_DATE"] = date,
        };
        var result = Git(ProjectFolder, GitOperation.Metadata, ["-c", "commit.gpgSign=false", "commit-tree", recipe.Tree.Hex,
            .. recipe.Parents.SelectMany(parent => new[] { "-p", parent.Hex }), "-F", "-"], environment, Encoding.UTF8.GetBytes(recipe.Message));
        return result.ExitCode == 0 ? new GitRead<CommitId>.Read(new(result.Text.Trim())) : Failure<CommitId>(result);
    }

    public RefMove MoveRef(RefChange change)
    {
        var result = Git(ProjectFolder, GitOperation.Metadata, ["update-ref", "--no-deref", change.Ref, change.Target.Hex, change.Expected?.Hex ?? new string('0', 40)]);
        if (result.ExitCode == 0)
        {
            return new RefMove.Moved();
        }
        return ReadRef(change.Ref) switch
        {
            GitRead<CommitId?>.Read observed when observed.Value == change.Target => new RefMove.AlreadyAtTarget(),
            GitRead<CommitId?>.Read observed when observed.Value == change.Expected => new RefMove.Failed(result.Stderr),
            GitRead<CommitId?>.Read observed => new RefMove.Conflict(observed.Value),
            GitRead<CommitId?>.Failed failure => new RefMove.Failed(failure.Detail),
            _ => throw new InvalidOperationException(),
        };
    }

    public GitResult DeleteRef(string name, CommitId expected) =>
        Git(ProjectFolder, GitOperation.Metadata, ["update-ref", "--no-deref", "-d", name, expected.Hex]);

    public IndexAlignment AlignIndex(string checkout, Digest? expected, TreeId target)
    {
        if (VisibleIndex(checkout, _environment, _limits) is GitRead<byte[]>.Failed hidden) return new IndexAlignment.Failed(hidden.Detail);
        var digest = IndexDigest(checkout);
        if (digest is GitRead<Digest?>.Failed failure)
        {
            return new IndexAlignment.Failed(failure.Detail);
        }
        var observed = ((GitRead<Digest?>.Read)digest).Value;
        var tree = ReadIndexTree(checkout);
        if (tree is GitRead<TreeId>.Read current && current.Value == target)
        {
            return new IndexAlignment.AlreadyAligned();
        }
        if (observed != expected)
        {
            return new IndexAlignment.Unexpected(observed);
        }
        var read = Git(checkout, GitOperation.Worktree, ["read-tree", target.Hex]);
        if (read.ExitCode != 0)
        {
            return new IndexAlignment.Failed(read.Stderr);
        }
        var refresh = Git(checkout, GitOperation.Worktree, ["update-index", "-q", "--refresh"]);
        return refresh.ExitCode == 0 ? new IndexAlignment.Aligned() : new IndexAlignment.Failed(refresh.Stderr);
    }

    public GitRead<TreeId> ReadIndexTree(string checkout)
    {
        var index = IndexPath(checkout);
        if (index is not GitRead<string>.Read path)
        {
            return ConvertFailure<string, TreeId>(index);
        }
        var temporary = Path.Combine(Path.GetTempPath(), $"idevelop-index-{Guid.NewGuid():N}");
        try
        {
            if (File.Exists(path.Value)) File.Copy(path.Value, temporary);
            var tree = Git(checkout, GitOperation.Worktree, ["write-tree"], new Dictionary<string, string> { ["GIT_INDEX_FILE"] = temporary });
            return tree.ExitCode == 0 ? new GitRead<TreeId>.Read(new(tree.Text.Trim())) : Failure<TreeId>(tree);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new GitRead<TreeId>.Failed(MaterializationProblem.GitFailed, error.Message);
        }
        finally
        {
            DeleteTemporary(temporary);
            DeleteTemporary(temporary + ".lock");
        }
    }

    public GitResult AddWorktree(string relativePath, string branchShortName, CommitId start) =>
        Git(ProjectFolder, GitOperation.Worktree, ["worktree", "add", "-b", branchShortName, relativePath, start.Hex]);

    public GitResult AddWorktreeForExistingBranch(string path, string branch) => Git(ProjectFolder, GitOperation.Worktree, ["worktree", "add", path, branch]);

    public GitResult LockWorktree(string path, string reason)
    {
        var result = Git(ProjectFolder, GitOperation.Metadata, ["worktree", "lock", "--reason", reason, path]);
        if (result.ExitCode != 0 && Worktrees() is GitRead<ImmutableArray<GitWorktree>>.Read list)
        {
            var fullPath = Path.GetFullPath(Path.Combine(ProjectFolder, path));
            if (list.Value.Any(worktree => Path.GetFullPath(worktree.Path) == fullPath && worktree.Locked && worktree.LockReason == reason))
            {
                return new(0, [], "");
            }
        }
        return result;
    }

    /// <summary>The lines of <c>git submodule status --recursive</c>, one per submodule.</summary>
    public GitRead<string> SubmoduleStatus(string checkout) => ReadText(Git(checkout, GitOperation.Worktree, ["submodule", "status", "--recursive"]), trim: false);

    public GitResult InitializeSubmodules(string checkout)
    {
        var status = Git(checkout, GitOperation.Worktree, ["submodule", "status", "--recursive"]);
        if (status.ExitCode != 0) return status;
        if (status.Text.Split('\n').Any(line => line.StartsWith('+') || line.StartsWith('U'))) return new(0, [], "");
        return Git(checkout, GitOperation.Network, ["submodule", "update", "--init", "--recursive"]);
    }

    public void EnsureExcluded()
    {
        var folder = Directory.CreateDirectory(Path.Combine(CommonDirectory, "info")).FullName;
        var path = Path.Combine(folder, "exclude");
        var contents = File.Exists(path) ? File.ReadAllBytes(path) : [];
        var lines = Encoding.UTF8.GetString(contents).Split('\n').Select(line => line.TrimEnd('\r')).ToHashSet(StringComparer.Ordinal);
        var missing = new[] { "/.worktrees/", "/.idp/inputs/", "/.idp/outbox/" }.Where(line => !lines.Contains(line)).ToArray();
        if (missing.Length == 0)
        {
            return;
        }
        var separator = contents.Length != 0 && contents[^1] != (byte)'\n' ? "\n" : "";
        AtomicFile.Replace(path, [.. contents, .. Encoding.UTF8.GetBytes(separator + string.Join('\n', missing) + "\n")]);
    }

    /// <summary>Call only after verifying retained salvage, quiescence, ownership, and an unchanged inventory.</summary>
    public GitResult ResetCheckout(string checkout, CommitId commit) => Git(checkout, GitOperation.Worktree, ["reset", "--hard", "--no-recurse-submodules", commit.Hex]);

    /// <summary>The caller holds ownership and proves quiescence; matching content alone does not exclude concurrent writers.</summary>
    public PathRemoval RemovePath(string checkout, string relativePath, Digest expected, GitPathType expectedType)
    {
        if (expectedType != GitPathType.RegularFile || Path.IsPathRooted(relativePath))
        {
            return PathRemoval.Unexpected;
        }
        var parts = relativePath.Replace('\\', '/').Split('/');
        if (parts.Any(part => part is "" or "." or ".." || part.Contains(':')))
        {
            return PathRemoval.Unexpected;
        }
        var path = checkout;
        foreach (var part in parts)
        {
            path = Path.Combine(path, part);
            if (File.Exists(path) || Directory.Exists(path))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    return PathRemoval.Unexpected;
                }
            }
        }
        if (Directory.Exists(path))
        {
            return PathRemoval.Unexpected;
        }
        if (!File.Exists(path))
        {
            return PathRemoval.Absent;
        }
        try
        {
            RegularFile.Verify(path);
        }
        catch (IOException)
        {
            return PathRemoval.Unexpected;
        }
        if (HashFile(path) != expected)
        {
            return PathRemoval.Unexpected;
        }
        File.Delete(path);
        return PathRemoval.Removed;
    }

    private GitResult Git(string checkout, GitOperation operation, string[] arguments, IReadOnlyDictionary<string, string>? overlay = null, byte[]? stdin = null,
        IReadOnlyDictionary<string, string>? pinnedGitEnvironment = null)
    {
        var environment = new Dictionary<string, string>(_environment);
        if (overlay is not null)
        {
            foreach (var (key, value) in overlay) environment[key] = value;
        }
        return Run(arguments, checkout, operation, _limits, environment, stdin, pinnedGitEnvironment);
    }

    internal static GitResult Run(string[] arguments, string workingDirectory, GitOperation operation, GitLimits limits,
        IReadOnlyDictionary<string, string>? environment = null, byte[]? stdin = null,
        IReadOnlyDictionary<string, string>? pinnedGitEnvironment = null)
    {
        var patience = operation switch
        {
            GitOperation.Metadata => limits.Metadata,
            GitOperation.Worktree => limits.Worktree,
            GitOperation.Network => limits.Network,
        };
        var start = new ProcessStartInfo("git", ["-c", "advice.graftFileDeprecated=false", "-c", "core.sparseCheckout=false", "-c", "core.ignoreStat=false", "-c", "core.commitGraph=false", "-c", "core.fsmonitor=false", "-c", "core.checkStat=default", "-c", "core.trustctime=true", .. arguments])
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (environment is not null)
        {
            foreach (var (key, value) in environment) start.Environment[key] = value;
        }
        if (pinnedGitEnvironment is not null)
        {
            foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
                start.Environment.Remove(key);
            foreach (var (key, value) in pinnedGitEnvironment) start.Environment[key] = value;
        }
        // The launcher needs a full path. Only PATH is searched: Process.Start would also look beside the app and in the
        // current folder, and a git found there could be one planted in a repository.
        if (!OperatingSystem.IsWindows())
        {
            var searchPath = environment is not null && environment.TryGetValue("PATH", out var given) ? given : System.Environment.GetEnvironmentVariable("PATH");
            if (CommandResolver.Create((searchPath ?? "").Split(Path.PathSeparator), []).Resolve("git")?.Path is not { } git)
                return new(-1, [], "git was not found on PATH.");
            start.FileName = git;
        }
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["LC_ALL"] = "C";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
        start.Environment["GIT_GRAFT_FILE"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        try
        {
            var elapsed = Stopwatch.StartNew();
            TimeSpan Remaining()
            {
                var remaining = patience - elapsed.Elapsed;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
            using var process = GitProcess.Start(start);
            using var stop = new CancellationTokenSource();
            using var output = new MemoryStream();
            using var stdoutPipe = process.Output;
            using var stderrPipe = process.Error;
            using var stdinPipe = process.Input;
            var stdout = Task.Run(() => stdoutPipe.CopyToAsync(output, stop.Token));
            var stderr = Task.Run(() => stderrPipe.ReadToEndAsync(stop.Token));
            var input = Task.Run(async () =>
            {
                if (stdin is not null) await stdinPipe.WriteAsync(stdin, stop.Token).ConfigureAwait(false);
                stdinPipe.Close();
            });
            var pipes = Task.WhenAll(stdout, stderr, input);
            try
            {
                if (process.WaitForExit(Remaining()) && Task.WaitAll([stdout, stderr, input], Remaining()))
                    return new(process.ExitCode, output.ToArray(), stderr.Result);
            }
            catch (AggregateException error) when (error.GetBaseException() is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                if (input.IsFaulted && input.Exception?.GetBaseException() is IOException && stdout.IsCompletedSuccessfully && stderr.IsCompletedSuccessfully)
                    return new(-1, output.ToArray(), stderr.Result + error.GetBaseException().Message);
                throw error.GetBaseException();
            }
            // The group or job also stops what a hook started after its parent exited, which no tree kill finds. A process
            // that left them can still hold the pipes open. On Windows, closing a pipe does not end a read blocked on it,
            // so the reads are canceled first.
            process.Stop();
            process.WaitForExit(Settle);
            if (Task.WaitAny([pipes], Settle) < 0) stop.Cancel();
            return new(-1, [], (stderr.IsCompletedSuccessfully ? stderr.Result : "") + "Git timed out.");
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new(-1, [], error.Message);
        }
    }

    /// <summary>
    /// One Git process and what contains it: its own session and process group on Linux and macOS, a job on Windows.
    /// Only a timeout stops them. The job never kills on close, so Git and its hooks finish even when iDevelop exits or
    /// dies during the call, and a hook's background work outlives a call that ends on its own, as it did before.
    /// </summary>
    private sealed class GitProcess : IDisposable
    {
        private readonly Process? _process;
        private readonly ProcessJob? _job;
        private readonly ProcessGroup? _group;

        private GitProcess(ProcessGroup group)
        {
            _group = group;
            Input = group.Input;
            Output = group.Output;
            Error = new StreamReader(group.Error, Encoding.UTF8);
        }

        private GitProcess(Process process)
        {
            _process = process;
            _job = OperatingSystem.IsWindows() ? ProcessJob.Assign(process, killOnClose: false) : null;
            Input = process.StandardInput.BaseStream;
            Output = process.StandardOutput.BaseStream;
            Error = process.StandardError;
        }

        public Stream Input { get; }
        public Stream Output { get; }
        public StreamReader Error { get; }
        public int ExitCode => _group?.Exited.Result ?? _process!.ExitCode;

        public static GitProcess Start(ProcessStartInfo start)
        {
            if (!OperatingSystem.IsWindows())
            {
                // Without the launcher, Process.Start gets the same full path and starts Git without a group.
                try { return new GitProcess(ProcessGroup.Start(start)); }
                catch (NotSupportedException) { }
            }
            return new GitProcess(Process.Start(start)!);
        }

        public bool WaitForExit(TimeSpan timeout) => _group?.Exited.Wait(timeout) ?? _process!.WaitForExit(timeout);

        public void Stop()
        {
            if (_group is not null)
            {
                _group.Stop();
                return;
            }
            _job?.Terminate();
            ProcessCheck.KillTreeQuietly(_process!);
        }

        public void Dispose()
        {
            _job?.Dispose();
            _group?.Dispose();
            _process?.Dispose();
        }
    }

    private static Digest? HashFile(string path) => File.Exists(path) ? new(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))) : null;

    private static (string Name, string Email)? Identity(string text)
    {
        var match = Regex.Match(text, @"^([^<>\r\n]+) <([^<>\r\n]+)>$", RegexOptions.CultureInvariant);
        return match.Success ? (match.Groups[1].Value, match.Groups[2].Value) : null;
    }

    private static GitRead<byte[]> VisibleIndex(string checkout, IReadOnlyDictionary<string, string> environment, GitLimits limits)
    {
        var result = Run(["ls-files", "--stage", "-v", "-z"], checkout, GitOperation.Worktree, limits, environment);
        if (result.ExitCode != 0) return Failure<byte[]>(result);
        foreach (var entry in NulFields(result).Where(entry => char.IsLower(entry[0]) || entry[0] == 'S'))
        {
            var path = entry[(entry.IndexOf('\t') + 1)..];
            return new GitRead<byte[]>.Failed(MaterializationProblem.DirtyWorktree,
                $"The index hides changes to {path} with assume-unchanged or skip-worktree.");
        }
        return new GitRead<byte[]>.Read(result.Stdout);
    }

    private static string[] NulFields(GitResult result) => result.Text.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    private static GitRead<ImmutableArray<string>> ReadPaths(GitResult result) => result.ExitCode == 0
        ? new GitRead<ImmutableArray<string>>.Read([.. NulFields(result)]) : Failure<ImmutableArray<string>>(result);
    private static GitRead<string> ReadText(GitResult result, bool trim = false) => result.ExitCode == 0
        ? new GitRead<string>.Read(trim ? result.Text.TrimEnd('\r', '\n') : result.Text) : Failure<string>(result);
    private static GitRead<T>.Failed Failure<T>(GitResult result) => new(MaterializationProblem.GitFailed, result.Stderr);
    private static GitRead<TOut>.Failed ConvertFailure<TIn, TOut>(GitRead<TIn> result)
    {
        var failure = (GitRead<TIn>.Failed)result;
        return new(failure.Problem, failure.Detail);
    }
}
