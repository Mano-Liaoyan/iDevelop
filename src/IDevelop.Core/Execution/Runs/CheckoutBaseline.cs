using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace IDevelop.Execution;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Result), "result")]
[JsonDerivedType(typeof(Capture), "capture")]
[JsonDerivedType(typeof(Preparation), "preparation")]
[JsonDerivedType(typeof(Recovery), "recovery")]
[JsonDerivedType(typeof(Reset), "reset")]
internal abstract record RestoreTarget
{
    private RestoreTarget() { }
    internal sealed record Result(ResultId Id) : RestoreTarget;
    internal sealed record Capture(CaptureId Id) : RestoreTarget;
    internal sealed record Preparation(LaunchKey Launch) : RestoreTarget;
    internal sealed record Recovery(AttemptId Previous, OperationId Confirmation) : RestoreTarget;
    internal sealed record Reset(OperationId Operation) : RestoreTarget;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Fixed), "fixed")]
[JsonDerivedType(typeof(Pending), "pending")]
[JsonDerivedType(typeof(Unfinished), "unfinished")]
internal abstract record ComponentBaseline
{
    private ComponentBaseline() { }
    internal sealed record Fixed(string? Value, RestoreTarget Source) : ComponentBaseline;
    internal sealed record Pending(string Before, string After, OperationId Owner) : ComponentBaseline;
    internal sealed record Unfinished(OperationId Owner) : ComponentBaseline;
}

internal static class CheckoutBaseline
{
    public static (ImmutableDictionary<string, ComponentBaseline> Components, EvidenceFile? Index) Fold(
        RunRecord record, WorktreeOwner owner, Func<CommitId, TreeId> tree)
    {
        var components = ImmutableDictionary.CreateBuilder<string, ComponentBaseline>(StringComparer.Ordinal);
        EvidenceFile? index = null;
        foreach (var entry in record.Receipts.Values.OrderBy(entry => entry.Sequence))
        {
            switch (entry.Event)
            {
                case RunEvent.Prepared { Execution: var prepared } when prepared.Location.Owner == owner && prepared.Launch.Turn == 1 &&
                    record.Attempts[prepared.Launch.Attempt].Cause is not AttemptCause.Continue:
                    At(prepared.Location.AttemptBase, new RestoreTarget.Preparation(prepared.Launch));
                    break;
                case RunEvent.TurnClosed { Capture: { } capture, Key: var launch } when
                    record.Preparations[launch].Location.Owner == owner &&
                    record.Dispositions[capture].Disposition is CaptureDisposition.Matched:
                    var first = record.Captures[capture].Single(observation => observation.Ordinal == 1);
                    var source = new RestoreTarget.Capture(capture);
                    Set("branch", first.Tip!.Value.Hex, source);
                    Set("head", first.Head, source);
                    Set("files", first.Recipe.Tree.Hex, source);
                    Set("index", first.IndexTree?.Hex, source);
                    index = first.Index;
                    break;
                case RunEvent.RecoveryBaselined { Baseline: var baseline } when
                    record.Preparations[new(baseline.Previous, 1)].Location.Owner == owner:
                    var preserved = ((MaterializationPlan.Preservation)record.Plans[
                        OperationIds.Derive(baseline.Preservation, "preserve-plan")]).Preserved;
                    var recovery = new RestoreTarget.Recovery(baseline.Previous, baseline.Confirmation);
                    Set("branch", preserved.Branch?.Hex, recovery);
                    Set("head", preserved.SymbolicHead, recovery);
                    Set("files", preserved.Files.Hex, recovery);
                    Set("index", preserved.IndexTree?.Hex, recovery);
                    index = preserved.Index;
                    break;
                case RunEvent.GitIntended intended:
                    Apply(entry.Operation, intended, false);
                    break;
                case RunEvent.GitObserved observed:
                    Apply(observed.Mutation, record.GitIntents[observed.Mutation], true);
                    break;
            }
        }
        return (components.ToImmutable(), index);

        void Set(string name, string? value, RestoreTarget source) => components[name] = new ComponentBaseline.Fixed(value, source);
        void At(CommitId commit, RestoreTarget source)
        {
            Set("branch", commit.Hex, source);
            Set("head", owner.Branch, source);
            Set("files", tree(commit).Hex, source);
            Set("index", tree(commit).Hex, source);
            index = null;
        }
        void Unfinished(OperationId plan)
        {
            foreach (var name in new[] { "branch", "head", "files", "index" }) components[name] = new ComponentBaseline.Unfinished(plan);
            index = null;
        }
        void Apply(OperationId intentId, RunEvent.GitIntended intent, bool observed)
        {
            var plan = record.Plans.GetValueOrDefault(intent.Plan);
            switch (intent.Mutation)
            {
                case GitMutation.CreateWorktree create when create.Owner == owner && plan is MaterializationPlan.Preparation preparation:
                    if (observed) At(create.Start, new RestoreTarget.Preparation(new(preparation.Attempt, 1)));
                    else Unfinished(intent.Plan);
                    break;
                case GitMutation.MoveRef move when move.Change.Ref == owner.Branch &&
                    (plan is MaterializationPlan.Refresh refresh && record.Attempts[refresh.Launch.Attempt].Task == owner.Task ||
                     plan is MaterializationPlan.Publication publication && record.Attempts[publication.Attempt].Task == owner.Task ||
                     plan is MaterializationPlan.Rebase rebasing && rebasing.Task == owner.Task):
                    if (observed) Set("branch", move.Change.Target.Hex, plan switch
                    {
                        MaterializationPlan.Publication published => new RestoreTarget.Result(published.Result),
                        MaterializationPlan.Rebase rebased => new RestoreTarget.Result(rebased.Result),
                        _ => new RestoreTarget.Reset(intent.Plan),
                    });
                    else if (move.Change.Expected is { } expected)
                        components["branch"] = new ComponentBaseline.Pending(expected.Hex, move.Change.Target.Hex, intent.Plan);
                    else components["branch"] = new ComponentBaseline.Unfinished(intent.Plan);
                    break;
                case GitMutation.ResetCheckout reset when reset.Task == owner.Task && plan is MaterializationPlan.Refresh refreshing:
                    var old = record.Preparations[new(refreshing.Launch.Attempt, refreshing.Launch.Turn - 1)].Location.AttemptBase;
                    foreach (var name in new[] { "files", "index" })
                        components[name] = observed ? new ComponentBaseline.Fixed(tree(reset.Target).Hex, new RestoreTarget.Reset(intent.Plan)) :
                            new ComponentBaseline.Pending(tree(old).Hex, tree(reset.Target).Hex, intent.Plan);
                    if (observed) Set("head", owner.Branch, new RestoreTarget.Reset(intent.Plan));
                    index = null;
                    break;
                case GitMutation.ResetCheckout reset when reset.Task == owner.Task && plan is MaterializationPlan.Rebase rebase:
                    foreach (var name in new[] { "files", "index" })
                        components[name] = observed ? new ComponentBaseline.Fixed(tree(reset.Target).Hex, new RestoreTarget.Result(rebase.Result)) :
                            new ComponentBaseline.Pending(tree(rebase.From).Hex, tree(reset.Target).Hex, intent.Plan);
                    if (observed) Set("head", owner.Branch, new RestoreTarget.Result(rebase.Result));
                    index = null;
                    break;
                case GitMutation.AlignIndex align when align.Task == owner.Task && plan is MaterializationPlan.Publication publishing:
                    if (observed)
                    {
                        Set("index", align.Target.Hex, new RestoreTarget.Result(publishing.Result));
                        index = null;
                    }
                    else
                    {
                        var before = components.GetValueOrDefault("index") switch
                        {
                            ComponentBaseline.Fixed fixedValue => fixedValue.Value,
                            ComponentBaseline.Pending pending => pending.Before,
                            _ => null,
                        };
                        components["index"] = before is null ? new ComponentBaseline.Unfinished(intent.Plan) :
                            new ComponentBaseline.Pending(before, align.Target.Hex, intent.Plan);
                    }
                    break;
            }
            if (plan is MaterializationPlan.RetryReset retry && retry.Task == owner.Task)
            {
                if (intent.Mutation is GitMutation.RemovePaths && observed) At(retry.To, new RestoreTarget.Reset(intent.Plan));
                else if (!record.Receipts.Values.Any(e => e.Sequence < record.Receipts[intentId].Sequence &&
                    e.Event is RunEvent.GitObserved o && record.GitIntents[o.Mutation].Plan == intent.Plan &&
                    record.GitIntents[o.Mutation].Mutation is GitMutation.RemovePaths)) Unfinished(intent.Plan);
            }
        }
    }
}

internal sealed record PathRestore(string Path, string? From, string? To);

internal sealed record RestorePreview(OperationId Preservation, CheckoutState Current,
    ImmutableDictionary<string, ComponentBaseline> Baseline, CheckoutState To, ImmutableArray<PathRestore> Paths,
    ImmutableArray<string> Refs, LockEvidence? IndexLock, ImmutableArray<OperationId> Repairs, ImmutableArray<OperationId> Rechecks,
    SortedDictionary<string, CommitId> SharedRefs, OperationId? Supersedes, Digest Identity);
