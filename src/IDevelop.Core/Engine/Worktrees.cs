using System.Collections.Immutable;
using IDevelop.Git;
using IDevelop.Workflows;

namespace IDevelop.Engine;

/// <summary>Where an editing node's worktree stands once its predecessors' commits are merged.</summary>
internal abstract record WorktreeStart
{
    private WorktreeStart() { }

    public sealed record Ready(CommitId Commit) : WorktreeStart;

    /// <summary>Merging the commits conflicts in <paramref name="Paths"/>, and the worktree holds Git's merge with its markers.</summary>
    public sealed record Conflict(ImmutableArray<string> Paths) : WorktreeStart
    {
        public string Description => "Merging the work handed on conflicts in these files:\n\n" + string.Concat(Paths.Select(path => $"- `{path}`\n"));
    }
}

/// <summary>A Git step that failed. Its message is for the person.</summary>
internal sealed class GitFailure(string message) : InvalidOperationException(message);

/// <summary>
/// The editing nodes' worktrees of one workflow. Each node has its worktree at <c>.worktrees/&lt;workflow&gt;/&lt;node&gt;</c>
/// in the project folder, on its branch <c>idp/&lt;workflow&gt;/&lt;node&gt;</c> (ADR 0032). Commits merge with
/// <c>git merge-tree</c>; a conflict leaves Git's merge with its markers as a commit on both parents, and the worktree's
/// next commit replaces it on the same parents (ADR 0033). The steps of every workflow's worktrees run one at a time, and
/// the project lock keeps every other window out of the project.
/// </summary>
internal sealed class Worktrees(string projectFolder, WorkflowId workflow, IReadOnlyDictionary<string, string> environment)
{
    private const string Identity = "iDevelop <idevelop@localhost>";
    private const int DiffLimit = 200_000;
    private static readonly Lock Gate = new();

    private GitRepository? _repository;

    /// <summary>Why no editing node can run, or null.</summary>
    public string? Problem
    {
        get
        {
            try
            {
                _ = Repository;
                return null;
            }
            catch (GitFailure e)
            {
                return e.Message;
            }
        }
    }

    private GitRepository Repository => _repository ??= GitRepository.Open(projectFolder, environment) switch
    {
        RepositoryOpen.Opened opened => opened.Repository,
        RepositoryOpen.Refused { InNoWorkTree: true } => throw new GitFailure("The project folder is in no Git work tree."),
        RepositoryOpen.Refused refused => throw new GitFailure(refused.Detail.Trim()),
        _ => throw new InvalidOperationException(),
    };

    /// <summary>The folder the node's agent works in: the project's folder in the node's worktree (ADR 0036).</summary>
    public string Folder(TaskId node) => Repository.ProjectIn(Checkout(node));

    /// <summary>
    /// Resets the node's worktree, with no file an earlier session left, to the merge of <paramref name="inputs"/>, or to the
    /// branch's HEAD when there are none.
    /// </summary>
    public WorktreeStart Reset(TaskId node, IReadOnlyList<CommitId> inputs) => Locked(repository =>
    {
        var start = Fold(repository, inputs.Count > 0 ? null : Value(repository.ResolveCommit("HEAD")), inputs);
        var checkout = Checkout(node);
        if (!Value(repository.Worktrees()).Any(worktree => Same(worktree, checkout)))
        {
            repository.EnsureExcluded();
            var branch = $"idp/{workflow}/{node}";
            var relative = Path.GetRelativePath(repository.ProjectFolder, checkout);
            Run(Value(repository.ResolveCommit("refs/heads/" + branch)) is null
                ? repository.AddWorktree(relative, branch, start.Commit)
                : repository.AddWorktreeForExistingBranch(relative, branch));
        }

        Run(repository.ResetCheckout(checkout, start.Commit));
        Run(repository.CleanUntracked(checkout));
        return start.Result;
    });

    /// <summary>
    /// Commits the node's files, when they changed or <paramref name="resolving"/> says they resolve the conflict the
    /// worktree's HEAD holds, and then merges each of <paramref name="inputs"/> its commit does not hold yet. The node's
    /// commit is the worktree's HEAD after a <see cref="WorktreeStart.Ready"/>.
    /// </summary>
    public WorktreeStart Commit(TaskId node, string message, IReadOnlyList<CommitId> inputs, bool resolving) => Locked(repository =>
    {
        var checkout = Checkout(node);
        var head = Value(repository.ResolveCheckoutHead(checkout)) ?? throw new GitFailure("The worktree has no commit.");
        var tree = Value(repository.Capture(checkout));
        var read = Value(repository.ReadCommit(head));
        // A resolution replaces the commit of Git's merge with its markers, on the same parents.
        ImmutableArray<CommitId> parents = resolving && read.Parents.Length == 2 ? read.Parents : [head];
        if (parents.Length > 1 || read.Tree != tree)
        {
            head = Value(repository.CreateCommit(Recipe(tree, parents, message)));
        }

        var start = Fold(repository, head, inputs);
        Run(repository.ResetCheckout(checkout, start.Commit));
        return start.Result;
    });

    /// <summary>
    /// The change <paramref name="commit"/> made, as a diff from the merge of <paramref name="inputs"/>, or for a root node from
    /// where it left the branch's HEAD. A reviewer reads at most <see cref="DiffLimit"/> characters of it.
    /// </summary>
    public string Change(IReadOnlyList<CommitId> inputs, CommitId commit) => Locked(repository =>
    {
        var start = inputs.Count > 0 ? Fold(repository, null, inputs).Commit.Hex : Git(repository, "merge-base", "HEAD", commit.Hex).Trim();
        var diff = Git(repository, "diff", "--no-color", "--no-ext-diff", "--no-textconv", start, commit.Hex);
        return diff.Length <= DiffLimit ? diff : $"{diff[..DiffLimit]}\n… The diff goes on. Run git diff {start} {commit.Hex} to read all of it.";
    });

    /// <summary>The conflicting files in the node's worktree, whose HEAD is a conflict's merge, that still hold Git's conflict markers.</summary>
    public ImmutableArray<string> MarkersRemain(TaskId node) => Locked(repository =>
    {
        var checkout = Checkout(node);
        var head = Value(repository.ResolveCheckoutHead(checkout));
        return head is { } commit && Value(repository.ReadCommit(commit)).Parents is [var ours, var theirs]
            && repository.MergeTrees(ours, theirs, ours) is TreeMerge.Conflicted conflicted
            ? MergeMarkers.Remaining(checkout, Paths(conflicted))
            : [];
    });

    /// <summary>Whether any of <paramref name="ends"/> holds work that the checked-out branch's HEAD does not.</summary>
    public bool HasWorkBeyondHead(IReadOnlyList<CommitId> ends)
    {
        var repository = Repository;
        if (Value(repository.ResolveCommit("HEAD")) is not { } head) return ends.Count > 0;
        return ends.Any(end => repository.IsAncestor(end, head) is not GitAncestry.Yes);
    }

    /// <summary>
    /// Merges <paramref name="ends"/> into the checked-out branch. Refused while HEAD is detached, when they conflict with each
    /// other, and when the branch moved and the merge conflicts.
    /// </summary>
    public void MergeIntoBranch(IReadOnlyList<CommitId> ends) => Locked<object?>(repository =>
    {
        if (ends.Count == 0) throw new InvalidOperationException("No node changed the project.");
        if (Value(repository.SymbolicHead(repository.Root)) is null)
        {
            throw new InvalidOperationException("HEAD is detached. Check out the branch to merge into, and try again.");
        }

        var work = Fold(repository, null, ends);
        if (work.Result is WorktreeStart.Conflict conflict)
        {
            throw new InvalidOperationException(
                $"The last nodes' work conflicts in {string.Join(", ", conflict.Paths)}. Add a Merge node after them, and run it.");
        }

        var head = Value(repository.ResolveCommit("HEAD")) ?? throw new GitFailure("The branch has no commit.");
        var target = Fold(repository, head, [work.Commit]);
        if (target.Result is WorktreeStart.Conflict moved)
        {
            throw new InvalidOperationException($"The branch changed, and merging into it conflicts in {string.Join(", ", moved.Paths)}.");
        }

        if (target.Commit != head) Run(repository.FastForward(repository.Root, target.Commit));
        return null;
    });

    /// <summary>
    /// Merges <paramref name="commits"/> one at a time into <paramref name="start"/>, skipping each one already held and
    /// fast-forwarding to each one that holds the merge so far. It stops at the first conflict, at a commit of Git's merge
    /// with its markers on both parents.
    /// </summary>
    private static (CommitId Commit, WorktreeStart Result) Fold(GitRepository repository, CommitId? start, IEnumerable<CommitId> commits)
    {
        var merged = start;
        foreach (var commit in commits.Distinct())
        {
            if (merged is not { } ours)
            {
                merged = commit;
            }
            else if (repository.IsAncestor(ours, commit) is GitAncestry.Yes)
            {
                merged = commit;
            }
            else if (repository.IsAncestor(commit, ours) is not GitAncestry.Yes)
            {
                switch (repository.MergeTrees(ours, commit, ours))
                {
                    case TreeMerge.Clean clean:
                        merged = Value(repository.CreateCommit(Recipe(clean.Tree, [ours, commit], "Merge the work handed on")));
                        break;
                    case TreeMerge.Conflicted conflicted:
                        var marked = Value(repository.CreateCommit(Recipe(conflicted.Tree, [ours, commit], "Merge the work handed on, with conflicts")));
                        return (marked, new WorktreeStart.Conflict(Paths(conflicted)));
                    case TreeMerge.Failed failed:
                        throw new GitFailure(failed.Detail);
                }
            }
        }

        var result = merged ?? throw new GitFailure("There is no commit to start from.");
        return (result, new WorktreeStart.Ready(result));
    }

    private static ImmutableArray<string> Paths(TreeMerge.Conflicted conflicted) =>
        [.. conflicted.Stages.Select(stage => stage.Path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    private static CommitRecipe Recipe(TreeId tree, ImmutableArray<CommitId> parents, string message) =>
        new(tree, parents, message + "\n", Identity, Identity, DateTimeOffset.UtcNow);

    private string Checkout(TaskId node) => Path.Combine(Repository.ProjectFolder, ".worktrees", workflow.ToString(), node.ToString());

    private T Locked<T>(Func<GitRepository, T> step)
    {
        var repository = Repository;
        lock (Gate) return step(repository);
    }

    private string Git(GitRepository repository, params string[] arguments) =>
        GitRepository.Run(arguments, repository.Root, GitOperation.Metadata, GitLimits.Default, environment) is { ExitCode: 0 } result
            ? result.Text
            : throw new GitFailure($"git {arguments[0]} failed.");

    private static void Run(GitResult result)
    {
        if (result.ExitCode != 0) throw new GitFailure(result.Stderr.Trim());
    }

    private static T Value<T>(GitRead<T> read) => read is GitRead<T>.Read value ? value.Value : throw new GitFailure(((GitRead<T>.Failed)read).Detail.Trim());

    private static bool Same(string first, string second) =>
        string.Equals(Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
