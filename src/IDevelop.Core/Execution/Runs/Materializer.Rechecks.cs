using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

internal sealed partial class Materializer
{
    private const string OwnBaselineChanged = "The checkout changed after its last recorded state. Preserve and restore it, then start again.";
    private const string ProducerChanged = "The checkout changed after its result was accepted. Preserve and restore it before the tasks after it start.";

    /// <summary>
    /// A claim that keeps changes starts from the checkout its last durable receipt recorded: the recovery baseline of a
    /// Continue, otherwise the folded baseline. An earlier drift block on the attempt still holds the claim.
    /// </summary>
    private ClaimCheck? OwnBaselineHold(CoordinatorPermit permit, OperationId operation, GitRepository repository, RunRecord record,
        PreparedExecution prepared, RunAttempt attempt)
    {
        if (RunReducer.AttemptDrift(record, id => id == attempt.Id) is { } existing) return new ClaimCheck.Blocked(existing.Value.Block);
        try
        {
            if (attempt.Cause is AttemptCause.Continue continued && record.Baselines.ContainsKey((continued.Previous, continued.Confirmation)))
                VerifyRecoveryBaseline(repository, record, prepared.Location, attempt.Cause);
            else if (VerifyOwnBaseline(repository, record, prepared.Location, attempt.Id, refusePending: true) is { } problem)
                return new ClaimCheck.Rejected(problem);
            return null;
        }
        catch (MaterializationFailure failed) when (IsCheckoutDrift(failed))
        {
            return RecheckBlock(permit, operation, "claim-recheck",
                new(Recheck(operation), attempt.Task, attempt.Id, failed.Problem, prepared.Inputs, [], failed.Message) { Scope = failed.Scope });
        }
    }

    /// <summary>
    /// Before a consumer's first claim, every producer it depends on must be settled and every code owner's checkout must
    /// still match its folded baseline. Precedence: an existing drift block, then an unresolved claim or Git step, then
    /// one live observation per code owner. Drift is recorded on the owner's publishing attempt.
    /// </summary>
    private ClaimCheck? ProducerRecheck(CoordinatorPermit permit, OperationId operation, GitRepository repository, RunRecord record,
        InputRecord inputs)
    {
        var producers = RunReducer.Producers(inputs);
        if (producers.IsEmpty) return null;
        if (RunReducer.ProducerDrift(record, producers) is { } existing) return new ClaimCheck.Blocked(existing.Value.Block);
        if (RunReducer.ProducerUnsettled(record, producers)) return new ClaimCheck.Rejected(new(RunProblem.UnresolvedOwnership));
        var owners = new List<(TaskId Task, AttemptId Attempt, PreparedExecution Prepared, ImmutableDictionary<string, ComponentBaseline> Baseline)>();
        foreach (var owner in RunReducer.CodeOwners(inputs))
        {
            if (RunReducer.PublishingAttempt(record, owner) is not { } published || !record.Preparations.TryGetValue(new(published, 1), out var prepared))
                return new ClaimCheck.Rejected(new(RunProblem.UnresolvedOwnership));
            var fold = CheckoutBaseline.Fold(record, prepared.Location.Owner, commit => Value(repository.ReadCommit(commit)).Tree);
            if (fold.Components.Values.Any(component => component is not ComponentBaseline.Fixed))
                return new ClaimCheck.Rejected(new(RunProblem.UnresolvedOwnership));
            owners.Add((owner, published, prepared, fold.Components));
        }
        foreach (var owner in owners)
        {
            MaterializationBlock? block = null;
            try
            {
                var current = ReadRecoveryCheckout(repository, record, owner.Prepared.Location, owner.Attempt);
                if (BaselineDifference(repository, owner.Baseline, current) is { } difference)
                    block = new(Recheck(operation), owner.Task, owner.Attempt, DriftProblem(difference), owner.Prepared.Inputs, [], ProducerChanged)
                    { Scope = difference };
            }
            catch (MaterializationFailure failed) when (IsCheckoutDrift(failed))
            {
                block = new(Recheck(operation), owner.Task, owner.Attempt, failed.Problem, owner.Prepared.Inputs, [], failed.Message) { Scope = failed.Scope };
            }
            if (block is not null) return RecheckBlock(permit, operation, "claim-producer", block);
        }
        return null;
    }

    /// <summary>
    /// Compares the live checkout with the folded baseline's fixed components. Null when they match. A pending or
    /// unfinished component refuses when <paramref name="refusePending"/> is set, and is skipped otherwise.
    /// </summary>
    private static RunRejection? VerifyOwnBaseline(GitRepository repository, RunRecord record, ExecutionLocation location, AttemptId attempt,
        bool refusePending)
    {
        var fold = CheckoutBaseline.Fold(record, location.Owner, commit => Value(repository.ReadCommit(commit)).Tree);
        if (refusePending && fold.Components.Values.Any(component => component is not ComponentBaseline.Fixed))
            return new(RunProblem.UnresolvedOwnership);
        var current = ReadRecoveryCheckout(repository, record, location, attempt);
        if (BaselineDifference(repository, fold.Components, current) is { } difference)
            throw Fault(DriftProblem(difference), OwnBaselineChanged, difference);
        return null;
    }

    private static BlockScope.Checkout? BaselineDifference(GitRepository repository, ImmutableDictionary<string, ComponentBaseline> baseline,
        CheckoutState current)
    {
        string? Recorded(string name) => (baseline.GetValueOrDefault(name) as ComponentBaseline.Fixed)?.Value;
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        var branch = Recorded("branch") is { } tip && tip != current.Branch?.Hex;
        var head = Recorded("head") is { } symbolic && symbolic != current.SymbolicHead;
        var files = Recorded("files");
        var filesChanged = files is not null && files != current.Files.Hex;
        if (filesChanged) paths.UnionWith(Value(repository.DiffTreePaths(new(files!), current.Files)));
        var index = Recorded("index");
        var indexChanged = index is not null && index != current.IndexTree?.Hex;
        if (indexChanged && current.IndexTree is { } live) paths.UnionWith(Value(repository.DiffTreePaths(new(index!), live)));
        return branch || head || filesChanged || indexChanged ? new([.. paths], branch, head) : null;
    }

    private static MaterializationProblem DriftProblem(BlockScope.Checkout difference) =>
        difference is { Branch: false, Head: false } ? MaterializationProblem.DirtyWorktree : MaterializationProblem.UncertainOwnership;

    private static bool IsCheckoutDrift(MaterializationFailure failed) =>
        failed.Problem is MaterializationProblem.DirtyWorktree or MaterializationProblem.UncertainOwnership && RunReducer.CheckedWithCheckout(failed.Scope);

    // A recheck's block names an operation no command runs as its own, so no replay of that command resolves it. Restore and a
    // recovery baseline, which verify the checkout, do.
    private static OperationId Recheck(OperationId operation) => OperationIds.Derive(operation, "recheck");

    private ClaimCheck RecheckBlock(CoordinatorPermit permit, OperationId operation, string step, MaterializationBlock block) =>
        Block(permit, operation, step, block) switch
        {
            Preparation.Blocked blocked => new ClaimCheck.Blocked(blocked.Block),
            Preparation.Rejected rejected => new ClaimCheck.Rejected(rejected.Reason),
            _ => throw new InvalidOperationException(),
        };
}
