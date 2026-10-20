using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>
/// What Run Workflow shows before anything runs: the execution snapshot it would approve, each task's effective agent
/// settings and inputs, the configuration gaps, the current branch's HEAD that the run starts from, and how tasks are
/// isolated. Building it records nothing: no ref, no journal or intent, no file in the project, and the person's index stays
/// as it was. Its one write is Git's: finding what differs from HEAD runs <c>git add --all</c> and <c>git write-tree</c> on
/// a temporary copy of the index in the system's temporary folder, which it deletes, and that stores the blobs of
/// uncommitted files and the trees as loose objects that nothing references, which Git's garbage collection removes.
/// </summary>
internal sealed record RunPreflight(string Project, ApprovedRevision Revision, PreflightGit Git, PreflightBase? Base,
    ImmutableArray<PreflightTask> Tasks, RunId? Active)
{
    public WorkflowId Workflow => Revision.Snapshot.Id;

    /// <summary>
    /// The node whose Run asks for the run, or null for Run Workflow (#90). Its preview lists, checks, and offers only that
    /// node and the tasks after it, since only those can start.
    /// </summary>
    public TaskId? Node { get; init; }

    public WorktreePolicy Worktrees { get; } = new(".worktrees", "idp/");

    /// <summary>What keeps the workflow from being approved now. Empty when it can be.</summary>
    public ImmutableArray<PreflightGap> Gaps { get; init; } = [];

    /// <summary>
    /// Reports of earlier standalone attempts that E1's narrow validator would let the run reuse from HEAD. Listing one
    /// includes nothing: a run reuses a report only when the person includes it.
    /// </summary>
    public ImmutableArray<PreflightReport> Reusable { get; init; } = [];

    /// <summary>
    /// Each root planner that ran on its own, and whether the run could include its report instead of running it again.
    /// Listing one includes nothing: the person includes it, and the confirmation finishes it if it still waits.
    /// </summary>
    public ImmutableArray<PreflightPlanner> Planners { get; init; } = [];

    /// <summary>
    /// Whether the person can confirm the preview: the project has a commit for the run to start from, nothing keeps it
    /// from starting, and a node's run could start its node.
    /// </summary>
    public bool Confirmable => Base is not null && Gaps.IsEmpty && Starts;

    /// <summary>
    /// The results of earlier runs that a node's run would carry for the tasks it does not run, and the needed ones it could
    /// not carry (#90). Empty for Run Workflow, which carries nothing.
    /// </summary>
    public ImmutableArray<PreflightCarried> Carried { get; init; } = [];

    /// <summary>The results the run would carry.</summary>
    public IEnumerable<PreflightCarried> Used => Carried.Where(carried => carried.Refusal is null);

    /// <summary>
    /// Whether the run could start its node: each dependency predecessor of the node has a result the run carries. Run
    /// Workflow's roots need nothing.
    /// </summary>
    public bool Starts => Node is not { } node ||
        RunScope.Predecessors(Revision.Snapshot)[node].All(predecessor => Used.Any(carried => carried.Task == predecessor));
}

internal abstract record PreflightGit
{
    private PreflightGit() { }

    internal sealed record Ready(string Version) : PreflightGit;

    /// <summary>The folder is in no Git work tree, or Git failed.</summary>
    internal sealed record Refused(MaterializationProblem Problem, string Detail) : PreflightGit;
}

/// <param name="Head">HEAD's commit, which the run starts from.</param>
/// <param name="Branch">The branch HEAD names, or null when HEAD is detached.</param>
/// <param name="Changed">
/// The paths where the work tree differs from HEAD, outside <c>.idp</c>, which the run's agents do not see. Empty for a
/// clean project.
/// </param>
/// <param name="Ignored">The ignored entries that never reach a task, a folder as one entry ending in a slash.</param>
internal sealed record PreflightBase(CommitId Head, string? Branch, ImmutableArray<string> Changed, ImmutableArray<string> Ignored,
    ImmutableArray<PreflightSubmodule> Submodules);

internal enum SubmoduleState { Current, Uninitialized, Changed, Conflicted }

/// <summary>A submodule as <c>git submodule status --recursive</c> reports it. Each task's checkout initializes its own.</summary>
internal sealed record PreflightSubmodule(string Path, CommitId Commit, SubmoduleState State);

/// <param name="Root">True when no dependency leads into the task, so it can start from the run base.</param>
internal sealed record PreflightTask(TaskId Task, string Title, WorkKind Kind, AgentAccess? Access, bool Proposes,
    ExecutionSettings? Settings, ConversationMode Conversation, ImmutableArray<PreflightInput> Inputs, bool Root);

internal sealed record PreflightInput(TaskId From, ConnectionKind Kind);

/// <summary>Each task runs in its own full checkout under <paramref name="Checkouts"/>, on a branch under <paramref name="Branches"/>.</summary>
internal sealed record WorktreePolicy(string Checkouts, string Branches);

/// <summary>
/// A task's result from the earlier run <paramref name="Run"/> that a node's run would take instead of running the task
/// (#90), as its report and artifacts. <paramref name="Refusal"/> says why the run cannot take it, such as a result it took
/// from a task the run runs again, or is null when the run carries it.
/// </summary>
internal sealed record PreflightCarried(TaskId Task, RunId Run, ResultId Result, CarryRefusal? Refusal);

/// <summary>A standalone attempt's report that the run could reuse for <paramref name="Task"/>.</summary>
internal sealed record PreflightReport(TaskId Task, AttemptId Source, string Report);

/// <summary>
/// A root planner's newest standalone attempt, as of its final turn <paramref name="Turn"/>. <paramref name="Status"/> is
/// <see cref="AttemptStatus.WaitingForInput"/> for a Chat planner that a confirmation including it marks done.
/// </summary>
/// <param name="Report">The report the run would include, the final turn's text.</param>
/// <param name="Problem">Why the run cannot include it, or null when it can.</param>
internal sealed record PreflightPlanner(TaskId Task, AttemptId Source, int Turn, AttemptStatus Status, string? Report, RunProblem? Problem);

internal abstract record PreflightGap
{
    private PreflightGap() { }

    /// <summary>The project is in no Git work tree, or Git failed.</summary>
    internal sealed record Git(MaterializationProblem Problem, string Detail) : PreflightGap;

    /// <summary>HEAD names no commit yet, so there is no base to start from.</summary>
    internal sealed record NoCommit : PreflightGap;

    /// <summary>The task could not start as configured, for the reason a single start would give.</summary>
    internal sealed record Task(TaskId Id, StartProblem Problem) : PreflightGap;

    /// <summary>Git could not read the project's submodules.</summary>
    internal sealed record Submodules(string Detail) : PreflightGap;

    /// <summary>A run record of the workflow could not be read, so no run can be approved until it is repaired.</summary>
    internal sealed record Records(string Detail) : PreflightGap;

    /// <summary>
    /// The node runs only after these dependency predecessors have results, and none of them has a current result from an
    /// earlier run that the run could carry (#90). The person runs them first.
    /// </summary>
    internal sealed record After(TaskId Id, ImmutableArray<TaskId> Predecessors) : PreflightGap
    {
        public bool Equals(After? other) => other is not null && Id == other.Id && Predecessors.SequenceEqual(other.Predecessors);

        public override int GetHashCode() => HashCode.Combine(Id, Predecessors.Length);
    }
}
