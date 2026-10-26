using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text;
using IDevelop.Clients;
using IDevelop.Processes;
using IDevelop.Projects;

namespace IDevelop.Git;

internal sealed record GitResult(int ExitCode, byte[] Stdout, string Stderr)
{
    public string Text => Encoding.UTF8.GetString(Stdout);
}

internal abstract record GitRead<T>
{
    private GitRead() { }
    internal sealed record Read(T Value) : GitRead<T>;
    internal sealed record Failed(string Detail) : GitRead<T>;
}

internal abstract record RepositoryOpen
{
    private RepositoryOpen() { }
    internal sealed record Opened(GitRepository Repository) : RepositoryOpen;
    /// <summary>Git cannot open the project. <see cref="InNoWorkTree"/> says the folder is in no Git work tree.</summary>
    internal sealed record Refused(string Detail) : RepositoryOpen
    {
        public bool InNoWorkTree { get; init; }
    }
}

internal sealed record GitCommit(TreeId Tree, ImmutableArray<CommitId> Parents);

internal abstract record GitAncestry
{
    private GitAncestry() { }
    internal sealed record Yes : GitAncestry;
    internal sealed record No : GitAncestry;
    internal sealed record Failed(string Detail) : GitAncestry;
}

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

    private GitRepository(string root, string prefix, string commonDirectory, IReadOnlyDictionary<string, string> environment, GitLimits limits)
    {
        Root = root;
        Prefix = prefix;
        ProjectFolder = ProjectIn(root);
        CommonDirectory = commonDirectory;
        _environment = new Dictionary<string, string>(environment);
        _limits = limits;
    }

    /// <summary>The top of the work tree the project folder is in.</summary>
    public string Root { get; }

    /// <summary>
    /// The project folder's path from <see cref="Root"/> with a trailing slash, as Git spells it, or empty when the project
    /// is the work tree's top. Each checkout of the repository holds the project at the same path.
    /// </summary>
    public string Prefix { get; }

    public string ProjectFolder { get; }
    public string CommonDirectory { get; }

    /// <summary>The project's folder in <paramref name="checkout"/>, a work tree of this repository.</summary>
    public string ProjectIn(string checkout) => Prefix.Length == 0 ? checkout : Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(checkout, Prefix)));

    internal static RepositoryOpen Open(string projectFolder, IReadOnlyDictionary<string, string> environment, GitLimits? limits = null)
    {
        limits ??= GitLimits.Default;
        var version = Run(["version"], projectFolder, GitOperation.Metadata, limits, environment);
        if (version.ExitCode != 0)
        {
            return new RepositoryOpen.Refused(version.Stderr);
        }
        var prefix = Run(["rev-parse", "--show-prefix"], projectFolder, GitOperation.Metadata, limits, environment);
        if (prefix.ExitCode != 0)
        {
            return new RepositoryOpen.Refused(prefix.Stderr) { InNoWorkTree = true };
        }
        var toplevel = Run(["rev-parse", "--show-toplevel"], projectFolder, GitOperation.Metadata, limits, environment);
        var common = Run(["rev-parse", "--path-format=absolute", "--git-common-dir"], projectFolder, GitOperation.Metadata, limits, environment);
        if (toplevel.ExitCode != 0 || common.ExitCode != 0)
        {
            return new RepositoryOpen.Refused(toplevel.Stderr + common.Stderr);
        }
        return new RepositoryOpen.Opened(new(toplevel.Text.TrimEnd('\r', '\n'), prefix.Text.TrimEnd('\r', '\n'),
            common.Text.TrimEnd('\r', '\n'), environment, limits));
    }

    public GitRead<CommitId?> ResolveCommit(string revision)
    {
        var result = Git(Root, GitOperation.Metadata, ["rev-parse", "--verify", "--quiet", revision + "^{commit}"]);
        return result.ExitCode switch
        {
            0 => new GitRead<CommitId?>.Read(new(result.Text.Trim())),
            1 => new GitRead<CommitId?>.Read(null),
            _ => Failure<CommitId?>(result),
        };
    }

    public GitRead<GitCommit> ReadCommit(CommitId commit)
    {
        var result = Git(Root, GitOperation.Metadata, ["cat-file", "commit", commit.Hex]);
        if (result.ExitCode != 0)
        {
            return Failure<GitCommit>(result);
        }
        var headers = result.Text.Split('\n').TakeWhile(line => line.Length != 0).ToArray();
        return new GitRead<GitCommit>.Read(new(new(headers.Single(line => line.StartsWith("tree ", StringComparison.Ordinal))[5..]),
            [.. headers.Where(line => line.StartsWith("parent ", StringComparison.Ordinal)).Select(line => new CommitId(line[7..]))]));
    }

    public GitAncestry IsAncestor(CommitId ancestor, CommitId descendant)
    {
        var result = Git(Root, GitOperation.Metadata, ["merge-base", "--is-ancestor", ancestor.Hex, descendant.Hex]);
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

    /// <summary>
    /// Moves the branch <paramref name="checkout"/> has checked out to <paramref name="commit"/>, a descendant of its HEAD,
    /// and its files with it. Git refuses when that would overwrite a change of the person's.
    /// </summary>
    public GitResult FastForward(string checkout, CommitId commit) =>
        Git(checkout, GitOperation.Worktree, ["merge", "--ff-only", "--no-edit", "--no-stat", commit.Hex]);

    /// <summary>The folders of the repository's work trees.</summary>
    public GitRead<ImmutableArray<string>> Worktrees()
    {
        var result = Git(Root, GitOperation.Metadata, ["worktree", "list", "--porcelain", "-z"]);
        return result.ExitCode == 0
            ? new GitRead<ImmutableArray<string>>.Read([.. NulFields(result).Where(field => field.StartsWith("worktree ", StringComparison.Ordinal)).Select(field => field[9..])])
            : Failure<ImmutableArray<string>>(result);
    }

    /// <summary>
    /// The tree of the checkout's files as they are, without the project's data folder and worktrees. It refuses an index
    /// that hides changes with assume-unchanged or skip-worktree, whose files the tree would miss.
    /// </summary>
    public GitRead<TreeId> Capture(string checkout)
    {
        var visible = VisibleIndex(checkout, _environment, _limits);
        return visible is GitRead<byte[]>.Failed hidden
            ? new GitRead<TreeId>.Failed(hidden.Detail)
            : WriteTree(checkout, [Prefix + DataFolder.Name, Prefix + ".worktrees"], _environment, _limits);
    }

    private static string[] Pathspec(IEnumerable<string> excludedPaths) =>
        [".", .. excludedPaths.Distinct(StringComparer.Ordinal).Select(exclusion => ":(exclude)" + exclusion)];

    private static GitRead<TreeId> WriteTree(string folder, IEnumerable<string> excludedPaths,
        IReadOnlyDictionary<string, string> environment, GitLimits limits)
    {
        var index = ReadText(Run(["rev-parse", "--path-format=absolute", "--git-path", "index"], folder, GitOperation.Metadata, limits, environment), trim: true);
        if (index is not GitRead<string>.Read path) return new GitRead<TreeId>.Failed(((GitRead<string>.Failed)index).Detail);
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
            return new GitRead<TreeId>.Failed(error.Message);
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

    public GitRead<CommitId> CreateCommit(CommitRecipe recipe)
    {
        var author = Identity(recipe.Author);
        var committer = Identity(recipe.Committer);
        if (author is null || committer is null)
        {
            return new GitRead<CommitId>.Failed("Commit identities must have the form Name <email>.");
        }
        var date = $"@{recipe.Timestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)} +0000";
        var environment = new Dictionary<string, string>
        {
            ["GIT_AUTHOR_NAME"] = author.Value.Name, ["GIT_AUTHOR_EMAIL"] = author.Value.Email, ["GIT_AUTHOR_DATE"] = date,
            ["GIT_COMMITTER_NAME"] = committer.Value.Name, ["GIT_COMMITTER_EMAIL"] = committer.Value.Email, ["GIT_COMMITTER_DATE"] = date,
        };
        var result = Git(Root, GitOperation.Metadata, ["-c", "commit.gpgSign=false", "commit-tree", recipe.Tree.Hex,
            .. recipe.Parents.SelectMany(parent => new[] { "-p", parent.Hex }), "-F", "-"], environment, Encoding.UTF8.GetBytes(recipe.Message));
        return result.ExitCode == 0 ? new GitRead<CommitId>.Read(new(result.Text.Trim())) : Failure<CommitId>(result);
    }

    /// <summary>Where <paramref name="relativePath"/>, from the project folder, is.</summary>
    private string InProject(string relativePath) => Path.GetFullPath(Path.Combine(ProjectFolder, relativePath));

    public GitResult AddWorktree(string relativePath, string branchShortName, CommitId start) =>
        Git(Root, GitOperation.Worktree, ["worktree", "add", "-b", branchShortName, InProject(relativePath), start.Hex]);

    /// <summary>Removes untracked files and folders that Git does not ignore, which keeps ignored ones such as delivered inputs.</summary>
    public GitResult CleanUntracked(string checkout) => Git(checkout, GitOperation.Worktree, ["clean", "-fdq"]);

    public GitResult AddWorktreeForExistingBranch(string path, string branch) => Git(Root, GitOperation.Worktree, ["worktree", "add", InProject(path), branch]);

    public void EnsureExcluded()
    {
        var folder = Directory.CreateDirectory(Path.Combine(CommonDirectory, "info")).FullName;
        var path = Path.Combine(folder, "exclude");
        var contents = File.Exists(path) ? File.ReadAllBytes(path) : [];
        var lines = Encoding.UTF8.GetString(contents).Split('\n').Select(line => line.TrimEnd('\r')).ToHashSet(StringComparer.Ordinal);
        var project = "/" + Regex.Replace(Prefix, @"[\*?\[\]]", @"\$0");
        var line = project + ".worktrees/";
        if (lines.Contains(line))
        {
            return;
        }
        var separator = contents.Length != 0 && contents[^1] != (byte)'\n' ? "\n" : "";
        AtomicFile.Replace(path, [.. contents, .. Encoding.UTF8.GetBytes(separator + line + "\n")]);
    }

    /// <summary>Resets the checkout's branch, index, and files to <paramref name="commit"/>.</summary>
    public GitResult ResetCheckout(string checkout, CommitId commit) => Git(checkout, GitOperation.Worktree, ["reset", "--hard", "--no-recurse-submodules", commit.Hex]);

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
            var searchPath = environment is not null && environment.TryGetValue("PATH", out var given) ? given : Environment.GetEnvironmentVariable("PATH");
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
            var stdout = CopyAsync(stdoutPipe, output, stop.Token);
            var stderr = ReadAsync(stderrPipe, stop.Token);
            var input = WriteAsync(stdinPipe, stdin, stop.Token);
            var pipes = Task.WhenAll(stdout, stderr, input);
            try
            {
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
            finally
            {
                // A canceled read or write can still run. Wait for it to end before its pipe and buffer are disposed.
                stop.Cancel();
                Task.WaitAny([pipes], Settle);
            }
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new(-1, [], error.Message);
        }
    }

    // Each pipe runs on the thread pool, as a read or write on a Windows pipe can block its thread.
    private static async Task CopyAsync(Stream from, Stream to, CancellationToken stop)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        await from.CopyToAsync(to, stop).ConfigureAwait(false);
    }

    private static async Task<string> ReadAsync(StreamReader from, CancellationToken stop)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        return await from.ReadToEndAsync(stop).ConfigureAwait(false);
    }

    private static async Task WriteAsync(Stream to, byte[]? bytes, CancellationToken stop)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        if (bytes is not null) await to.WriteAsync(bytes, stop).ConfigureAwait(false);
        to.Close();
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
            return new GitRead<byte[]>.Failed($"The index hides changes to {path} with assume-unchanged or skip-worktree.");
        }
        return new GitRead<byte[]>.Read(result.Stdout);
    }

    private static string[] NulFields(GitResult result) => result.Text.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    private static GitRead<string> ReadText(GitResult result, bool trim = false) => result.ExitCode == 0
        ? new GitRead<string>.Read(trim ? result.Text.TrimEnd('\r', '\n') : result.Text) : Failure<string>(result);
    private static GitRead<T>.Failed Failure<T>(GitResult result) => new(result.Stderr);
}
