namespace IDevelop.Execution;

internal static class RefOwnership
{
    public static bool Accepts(RunRecord record, GitRepository repository, string name, CommitId? live, RefChange? change = null)
    {
        CommitId? expected = null;
        CommitId? target = null;
        var lease = false;
        foreach (var entry in record.Receipts.Values.OrderBy(entry => entry.Sequence))
        {
            switch (entry.Event)
            {
                case RunEvent.GitIntended { Mutation: GitMutation.MoveRef move } when move.Change.Ref == name:
                    expected = move.Change.Expected;
                    target = move.Change.Target;
                    lease = false;
                    break;
                case RunEvent.GitIntended { Mutation: GitMutation.CreateWorktree create } when create.Owner.Branch == name:
                    target = create.Start;
                    lease = false;
                    break;
                case RunEvent.GitObserved observed:
                    var mutation = record.GitIntents[observed.Mutation].Mutation;
                    if (mutation is GitMutation.MoveRef moved && moved.Change.Ref == name ||
                        mutation is GitMutation.CreateWorktree created && created.Owner.Branch == name)
                    {
                        expected = observed.Observation.Value is { } value ? new CommitId(value) : null;
                        target = null;
                        lease = false;
                    }
                    break;
                case RunEvent.Prepared prepared when record.Schema < 3 && prepared.Execution.Location.Owner.Branch == name &&
                    RunReducer.Editable(record, record.Attempts[prepared.Execution.Launch.Attempt]):
                    lease = true;
                    break;
                case RunEvent.TurnClaimed claim when record.Schema == 3 &&
                    record.Preparations[claim.Key].Location.Owner.Branch == name &&
                    RunReducer.Editable(record, record.Attempts[claim.Key.Attempt]):
                    lease = true;
                    break;
                case RunEvent.RootExitObserved observed when record.Preparations[observed.Launch].Location.Owner.Branch == name:
                    lease = false;
                    if (observed.Ownership == TipOwnership.Explained)
                    {
                        expected = observed.Tip;
                        target = null;
                    }
                    break;
                case RunEvent.TurnClosed closed when record.Schema == 3 &&
                    record.Preparations[closed.Key].Location.Owner.Branch == name:
                    lease = false;
                    break;
                case RunEvent.AttemptClosed closed when record.Schema == 3 && record.Preparations.Values.Any(prepared =>
                    prepared.Launch.Attempt == closed.Attempt && prepared.Location.Owner.Branch == name):
                    lease = false;
                    break;
                case RunEvent.OwnershipFenced fenced when fenced.Claims.Any(key =>
                    record.Preparations[key].Location.Owner.Branch == name):
                    lease = false;
                    break;
                case RunEvent.SalvageRetained retained:
                    var salvage = (MaterializationPlan.Salvage)record.Plans[retained.Plan];
                    if (record.Preparations[new(salvage.Attempt, 1)].Location.Owner.Branch == name &&
                        (salvage.BranchTip == expected || target is not null && salvage.BranchTip == target || lease))
                    {
                        expected = salvage.BranchTip;
                        target = null;
                        lease = false;
                    }
                    break;
            }
        }
        var allowed = live == expected || target is not null && live == target || lease && expected is { } basis && live is { } tip &&
            repository.IsAncestor(basis, tip) is GitAncestry.Yes;
        return allowed && (change is null || live == change.Expected ||
            target is not null && change.Expected == expected && change.Target == target && live == target);
    }
}
