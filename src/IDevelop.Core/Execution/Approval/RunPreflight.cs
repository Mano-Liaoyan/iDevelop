using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>
/// What Run Workflow shows before anything runs: the execution snapshot it would approve, each task's effective agent
/// settings and inputs, the configuration gaps, the run base it can start from, and how tasks are isolated. Building it
/// writes no file, ref, or journal. Computing the work tree's snapshot writes only Git objects that nothing references.
/// </summary>
internal sealed record RunPreflight(string Project, ApprovedRevision Revision, PreflightGit Git, PreflightBase? Base,
    ImmutableArray<PreflightTask> Tasks, RunId? Active)
{
    public WorkflowId Workflow => Revision.Snapshot.Id;

    public WorktreePolicy Worktrees { get; } = new(".worktrees", "idp/");

    /// <summary>What keeps the workflow from being approved now. Empty when it can be.</summary>
    public ImmutableArray<PreflightGap> Gaps { get; init; } = [];

    /// <summary>
    /// Reports of earlier standalone attempts that E1's narrow validator would let the run reuse, with the bases they match.
    /// Listing one includes nothing: a run reuses a report only when the person includes it.
    /// </summary>
    public ImmutableArray<PreflightReport> Reusable { get; init; } = [];

    /// <summary>
    /// Each root planner that ran on its own, with the bases where the run could include its report instead of running it
    /// again. Listing one includes nothing: the person includes it, and the confirmation finishes it if it still waits.
    /// </summary>
    public ImmutableArray<PreflightPlanner> Planners { get; init; } = [];

    /// <summary>
    /// The bases the person can choose: HEAD when the project has a commit, and a snapshot of the uncommitted work when the
    /// work tree differs from HEAD outside iDevelop's data and nothing is unmerged.
    /// </summary>
    public ImmutableArray<BaseChoice> Choices => Base is not { } found || !Gaps.IsEmpty ? [] : Offered(found);

    internal static ImmutableArray<BaseChoice> Offered(PreflightBase found) =>
        found.Changed.IsEmpty || !found.Unmerged.IsEmpty ? [BaseChoice.Head] : [BaseChoice.Head, BaseChoice.Snapshot];
}

internal abstract record PreflightGit
{
    private PreflightGit() { }

    internal sealed record Ready(string Version) : PreflightGit;

    /// <summary>The folder is not a Git repository's root, Git is older than 2.39, or Git failed.</summary>
    internal sealed record Refused(MaterializationProblem Problem, string Detail) : PreflightGit;
}

/// <param name="Head">HEAD's commit.</param>
/// <param name="Branch">The branch HEAD names, or null when HEAD is detached.</param>
/// <param name="WorkTree">The tree a snapshot of the work tree would hold, built through a temporary index.</param>
/// <param name="Changed">The paths where the work tree differs from HEAD, outside <c>.idp</c>. Empty for a clean project.</param>
/// <param name="Ignored">The ignored entries that never reach a task, a folder as one entry ending in a slash.</param>
/// <param name="Unmerged">Paths with merge conflicts in the index, which keep a snapshot from being offered.</param>
internal sealed record PreflightBase(CommitId Head, string? Branch, TreeId WorkTree, ImmutableArray<string> Changed,
    ImmutableArray<string> Ignored, ImmutableArray<string> Unmerged, ImmutableArray<PreflightSubmodule> Submodules);

internal enum SubmoduleState { Current, Uninitialized, Changed, Conflicted }

/// <summary>A submodule as <c>git submodule status --recursive</c> reports it. Each task's checkout initializes its own.</summary>
internal sealed record PreflightSubmodule(string Path, CommitId Commit, SubmoduleState State);

/// <param name="Root">True when no dependency leads into the task, so it can start from the run base.</param>
internal sealed record PreflightTask(TaskId Task, string Title, WorkKind Kind, AgentAccess? Access, bool Proposes,
    ExecutionSettings? Settings, ConversationMode Conversation, ImmutableArray<PreflightInput> Inputs, bool Root);

internal sealed record PreflightInput(TaskId From, ConnectionKind Kind);

/// <summary>Each task runs in its own full checkout under <paramref name="Checkouts"/>, on a branch under <paramref name="Branches"/>.</summary>
internal sealed record WorktreePolicy(string Checkouts, string Branches);

/// <summary>A standalone attempt's report that the run could reuse for <paramref name="Task"/> on each of <paramref name="Bases"/>.</summary>
internal sealed record PreflightReport(TaskId Task, AttemptId Source, string Report, ImmutableArray<BaseChoice> Bases)
{
    public bool Equals(PreflightReport? other) => other is not null && Task == other.Task && Source == other.Source && Report == other.Report &&
        Bases.SequenceEqual(other.Bases);

    public override int GetHashCode() => HashCode.Combine(Task, Source, Report);
}

/// <summary>
/// A root planner's newest standalone attempt, as of its final turn <paramref name="Turn"/>. <paramref name="Status"/> is
/// <see cref="AttemptStatus.WaitingForInput"/> for a Chat planner that a confirmation including it marks done.
/// </summary>
/// <param name="Report">The report the run would include, the final turn's text.</param>
/// <param name="Bases">The bases on which the run can include it. Empty with <paramref name="Problem"/> when it cannot.</param>
internal sealed record PreflightPlanner(TaskId Task, AttemptId Source, int Turn, AttemptStatus Status, string? Report,
    ImmutableArray<BaseChoice> Bases, RunProblem? Problem)
{
    public bool Equals(PreflightPlanner? other) => other is not null && Task == other.Task && Source == other.Source && Turn == other.Turn &&
        Status == other.Status && Report == other.Report && Bases.SequenceEqual(other.Bases) && Problem == other.Problem;

    public override int GetHashCode() => HashCode.Combine(Task, Source, Turn);
}

internal abstract record PreflightGap
{
    private PreflightGap() { }

    /// <summary>The project is not a Git repository's root, its Git is older than 2.39, or Git failed.</summary>
    internal sealed record Git(MaterializationProblem Problem, string Detail) : PreflightGap;

    /// <summary>HEAD names no commit yet, so there is no base to start from.</summary>
    internal sealed record NoCommit : PreflightGap;

    /// <summary>The task could not start as configured, for the reason a single start would give.</summary>
    internal sealed record Task(TaskId Id, StartProblem Problem) : PreflightGap;

    /// <summary>The task joins several dependency results, which needs a newer Git.</summary>
    internal sealed record Join(TaskId Id, string Detail) : PreflightGap;

    /// <summary>Git could not read the project's submodules.</summary>
    internal sealed record Submodules(string Detail) : PreflightGap;

    /// <summary>A run record of the workflow could not be read, so no run can be approved until it is repaired.</summary>
    internal sealed record Records(string Detail) : PreflightGap;
}
