using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>
/// The run's merges (#148). A conflict between a task's predecessors goes to a Merge node's agent, and the task starts
/// once the merge is committed. The run's finished work merges into the branch it started from before the run completes,
/// with a conflict going to a Merge node's agent too. A conflict that no Merge node takes waits for the person, and so does
/// the final merge while the person's folder has uncommitted changes.
/// </summary>
internal sealed partial class WorkflowRunCoordinator
{
    // Owned by the loop.
    private bool _merging;
    private bool _mergeLater;
    private BranchMergeWait? _branchMerge;
    private readonly HashSet<OperationId> _resolving = [];

    /// <summary>
    /// Starts a Merge node's attempt for each conflict between predecessors that waits for one, and lets each task whose
    /// conflict a merge resolved start again.
    /// </summary>
    private void ResolveConflicts(RunRecord record, RunView view)
    {
        foreach (var (operation, state) in record.Blocks.Where(pair => !pair.Value.Resolved && pair.Value.Block.Conflict?.Merge is not null)
            .OrderBy(pair => record.Receipts.GetValueOrDefault(pair.Key)?.Sequence ?? 0))
        {
            var block = state.Block;
            var conflict = block.Conflict!.Merge!;
            // Only the block that holds its task now: one on a replaced attempt or input holds nothing.
            if (view.Tasks.GetValueOrDefault(block.Task) is not { State: TaskState.Blocked, Block: { } shown } || !RunReducer.Same(shown, block)) continue;
            if (RunReducer.Resolution(record, conflict.Ours, conflict.Theirs) is not null)
            {
                ResolveBlock(operation);
                continue;
            }
            StartMerge(record, conflict, MergeRouting.Node(record.Revision.Snapshot, conflict, MergeRouting.Branches(block.Conflict)));
        }
    }

    /// <summary>Records that a merge resolved the conflict of <paramref name="operation"/>'s block, so its task starts again.</summary>
    private void ResolveBlock(OperationId operation)
    {
        if (!_resolving.Add(operation)) return;
        Offload(() => _store.Record(_permit!, OperationIds.Derive(operation, "merged"), new RunEvent.BlockResolved(operation, MergedReason)),
            decision =>
            {
                _resolving.Remove(operation);
                if (decision is RunDecision.Rejected rejected) _problem = $"A resolved conflict could not be recorded: {rejected.Reason.Problem}.";
            }, error =>
            {
                _resolving.Remove(operation);
                _problem = error.Message;
            });
    }

    internal const string MergedReason = "The conflict is resolved, and its merge is committed.";

    /// <summary>
    /// Starts <paramref name="node"/>'s attempt that resolves <paramref name="conflict"/>, while the node is free and a client
    /// slot is. A conflict takes one attempt: one that goes on is waited for, and one that ended without resolving it
    /// waits for the person's Retry.
    /// </summary>
    private void StartMerge(RunRecord record, MergeConflict conflict, TaskId? node)
    {
        if (node is not { } merge || _live.ContainsKey(merge) || _holds.ContainsKey(merge) || _runs.ClientRoots - Slotted.Length <= 0) return;
        if (record.Attempts.Values.Any(attempt => RunReducer.MergeOf(record, attempt.Id) is { } other && other.Merges(conflict.Ours, conflict.Theirs)))
            return;
        var cause = new AttemptCause.Merge(conflict);
        var revision = record.Revision.Id;
        _live[merge] = new(LiveStage.Starting);
        _runs.Probe?.Invoke("coordinator.merge");
        Background(() => StartTurn(new TurnIntent.First(RunOperations.First(Address.Run, merge, cause), merge, cause, null)),
            start => Started(merge, start, revision), error =>
            {
                _live.Remove(merge);
                _problem = error.Message;
                HoldStart(merge, new TaskHold.Refused(new(RunProblem.StorageUnavailable), null, Transient: true));
            });
    }

    /// <summary>
    /// Merges the run's finished work into the branch it started from, once every task it can start is done and nothing
    /// is in flight, and settles the run as completed once the branch holds it. A run that started on a detached HEAD has no
    /// branch to merge into. A conflict goes to the Merge node with no successors; a folder with uncommitted changes, or a
    /// conflict no Merge node takes, waits for the person and is looked at again after the retry delay.
    /// </summary>
    private RunRecord MergeAndComplete(RunRecord record)
    {
        if (record.Base.Branch is not { } branch) return Settle(record, RunOperations.Completed(Address.Run), RunOutcome.Completed);
        if (_merging || _mergeLater) return record;
        _merging = true;
        Offload(() => Scheduled().MergeIntoBranch(_permit!), merged =>
        {
            _merging = false;
            switch (merged)
            {
                case BranchMerge.Merged:
                    _branchMerge = null;
                    if (Read() is { Phase: RunPhase.Approved } now) Settle(now, RunOperations.Completed(Address.Run), RunOutcome.Completed);
                    return;
                case BranchMerge.Uncommitted uncommitted:
                    _branchMerge = new BranchMergeWait.Uncommitted(branch, uncommitted.Folder, uncommitted.Paths);
                    break;
                case BranchMerge.Conflict conflict:
                    var node = MergeRouting.Node(record.Revision.Snapshot, conflict.Merge, []);
                    _branchMerge = new BranchMergeWait.Conflict(branch, conflict.Merge.Paths, node) { Merge = conflict.Merge };
                    if (Read() is { } current) StartMerge(current, conflict.Merge, node);
                    break;
                case BranchMerge.Problem problem:
                    _branchMerge = new BranchMergeWait.Problem(branch, problem.Detail);
                    break;
                case BranchMerge.Rejected { Reason.Problem: var refused } when !Transient(refused):
                    _branchMerge = new BranchMergeWait.Problem(branch, refused.ToString());
                    break;
            }
            MergeLater();
        }, error =>
        {
            _merging = false;
            _branchMerge = new BranchMergeWait.Problem(branch, error.Message);
            MergeLater();
        });
        return record;
    }

    /// <summary>Looks at the final merge again after the retry delay, so a commit, a stash, or the person's own merge lets it go on.</summary>
    private void MergeLater()
    {
        if (_mergeLater) return;
        _mergeLater = true;
        Background(async () =>
        {
            await Task.Delay(_runs.CoordinatorRetry, _runs.TimeProvider).ConfigureAwait(false);
            return true;
        }, _ => _mergeLater = false, _ => _mergeLater = false);
    }

    /// <summary>
    /// Adds the document's Merge node <paramref name="node"/> to the run, with the connections into it from the run's own
    /// tasks, so that a conflict that waits for the person can go to it: the person added a Merge node and ran it (#148).
    /// The journal records it once under <paramref name="confirmation"/> as the person's amendment, which only adds.
    /// </summary>
    public Task<RunCommand> AddMerge(RunAddress address, Workflow document, TaskId node, OperationId confirmation, CancellationToken wait = default) =>
        Command(address, record =>
        {
            var operation = RunOperations.AddMerge(confirmation, node);
            if (record.Receipts.TryGetValue(operation, out var receipt))
                return receipt.Event is RunEvent.Amended ? Accepted : new RunCommand.Refused(new(RunProblem.OperationConflict));
            if (record.Phase != RunPhase.Approved) return new RunCommand.Refused(new(RunProblem.RunStopped));
            if (document.Tasks.GetValueOrDefault(node) is not { Blueprint.Work: WorkSpec.Merge } definition)
                return new RunCommand.Refused(new(RunProblem.UnsupportedWork, Task: node));
            var snapshot = record.Revision.Snapshot;
            if (snapshot.Tasks.ContainsKey(node)) return new RunCommand.Refused(new(RunProblem.StartConflict, Task: node));
            ImmutableArray<WorkflowEdit> edits =
            [
                new WorkflowEdit.PlaceNode(node, definition.Blueprint, document.Positions.GetValueOrDefault(node))
                {
                    Title = definition.Title, Fields = definition.Fields.ToImmutableDictionary(), Settings = new(definition.Execution, definition.Conversation),
                },
                .. document.Connections.Where(connection => connection.Key.To == node && snapshot.Tasks.ContainsKey(connection.Key.From))
                    .OrderBy(connection => connection.Key).Select(connection => (WorkflowEdit)new WorkflowEdit.Connect(connection.Key, connection.Value)),
            ];
            if (snapshot.Apply(new WorkflowEdit.Batch(edits)) is not EditResult.Applied applied)
                return new RunCommand.Refused(new(RunProblem.InvalidData, Task: node));
            return _store.Amend(_permit!, operation, record.Revision.Id, Revision.Capture(applied.Workflow), new AmendmentOrigin.Person(), confirmation)
                is RunDecision.Rejected rejected ? new RunCommand.Refused(rejected.Reason) : Accepted;
        }, wait);

    /// <summary>
    /// Whether the run waits for the person on a merge conflict that no Merge node takes: between a task's predecessors, or
    /// in its final merge into its branch. Adding a Merge node and running it lets the conflict go to that node.
    /// </summary>
    internal static bool WaitsForMergeNode(RunView view) => view.Phase == RunPhase.Approved &&
        (view.Merge is BranchMergeWait.Conflict { Node: null } ||
         view.Tasks.Values.Any(task => task is { State: TaskState.Blocked, MergedBy: null, Block: { Problem: MaterializationProblem.FanInConflict, Conflict.Merge: not null } }));

    /// <summary>Whether a Merge node's view shows only an attempt of the final merge that ended without resolving it.</summary>
    private static bool FinalMergeFailed(TaskView view) => view is { State: TaskState.Failed, Merge.Target: MergeTarget.Branch };

    /// <summary>The run's status while its final merge waits: for the person, or for a Merge node's agent.</summary>
    private static RunStatus MergeStatus(RunStatus status, BranchMergeWait? wait) => (status, wait) switch
    {
        (RunStatus.Running, BranchMergeWait.Uncommitted or BranchMergeWait.Conflict { Node: null }) => RunStatus.Waiting,
        (RunStatus.Running, BranchMergeWait.Problem) => RunStatus.NeedsAttention,
        _ => status,
    };
}
