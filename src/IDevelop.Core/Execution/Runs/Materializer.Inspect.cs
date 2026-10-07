using IDevelop.Workflows;

namespace IDevelop.Execution;

internal sealed partial class Materializer
{
    public TaskWorkspace? Inspect(WorkflowId workflow, RunId run, TaskId task)
    {
        if (_store.Read(workflow, run) is not RunRead.Loaded loaded) return null;
        var record = loaded.Record;
        var owner = record.Receipts.Values.OrderBy(entry => entry.Sequence).Select(entry => entry.Event).OfType<RunEvent.Prepared>()
            .FirstOrDefault(prepared => prepared.Execution.Location.Owner.Task == task)?.Execution.Location.Owner;
        var runKey = record.RunKey ?? RunLayout.Key(run.Value, new HashSet<string>());
        var taskKey = record.TaskKeys.GetValueOrDefault(task) ?? RunLayout.Key(task.Value, record.TaskKeys.Values.ToHashSet(StringComparer.Ordinal));
        var checkout = Path.GetFullPath(Path.Combine(_project, owner?.RelativePath ?? RunLayout.TaskCheckout(runKey, taskKey)));
        var latest = record.Results.LastOrDefault(result => result.Task == task && result.Code is CodeOutput.Produced)?.Code as CodeOutput.Produced;
        var workspace = new TaskWorkspace(owner, checkout, false, null, null, latest?.Code.Commit,
            [.. record.Blocks.Where(pair => !pair.Value.Resolved && pair.Value.Block.Task == task)
                .OrderBy(pair => record.Receipts[pair.Key].Sequence).Select(pair => pair.Value.Block)],
            [.. record.Salvages.Values.Where(retained => record.Plans[retained.Plan] is MaterializationPlan.Salvage plan && plan.Task == task)
                .OrderBy(retained => record.Receipts.Values.Single(entry => entry.Event == retained).Sequence)]);
        if (GitRepository.Open(_project, _environment) is not RepositoryOpen.Opened opened) return workspace;
        var repository = opened.Repository;
        var registered = repository.Worktrees() is GitRead<System.Collections.Immutable.ImmutableArray<GitWorktree>>.Read worktrees &&
            worktrees.Value.Any(worktree => SamePath(worktree.Path, checkout));
        var tip = repository.ReadRef(owner?.Branch ?? RunLayout.TaskBranch(runKey, taskKey)) as GitRead<CommitId?>.Read;
        var status = registered ? repository.Status(checkout) as GitRead<byte[]>.Read : null;
        return workspace with { Registered = registered, BranchTip = tip?.Value, Clean = status is null ? null : status.Value.Length == 0 };
    }
}
