using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

internal static partial class RunReducer
{
    /// <summary>
    /// The tasks whose state a consumer's first claim depends on: the producer of every dependency binding, whether or not
    /// it carries code, and the original owners of the code it delivers, never a reviewer that only forwards it.
    /// </summary>
    internal static ImmutableArray<TaskId> Producers(InputRecord inputs) =>
        [.. inputs.Bindings.OfType<InputBinding.Provided>().Where(binding => binding.Kind == ConnectionKind.Dependency)
            .Select(binding => binding.Edge.From).Concat(CodeOwners(inputs)).Where(task => task != inputs.Task).Distinct()
            .OrderBy(task => task.ToString(), StringComparer.Ordinal)];

    internal static ImmutableArray<TaskId> CodeOwners(InputRecord inputs) => [.. (inputs.Code switch
    {
        CodeSelection.Single single => single.Source.Owners,
        CodeSelection.Joined joined => [.. joined.Join.Sources.SelectMany(source => source.Owners)],
        _ => ImmutableArray<TaskId>.Empty,
    }).Where(task => task != inputs.Task).Distinct().OrderBy(task => task.ToString(), StringComparer.Ordinal)];

    /// <summary>The attempt whose result is the task's current one. A drift block on its checkout names this attempt.</summary>
    internal static AttemptId? PublishingAttempt(RunRecord record, TaskId task) =>
        record.CurrentResults.GetValueOrDefault(task) is { Origin: ResultOrigin.Executed executed } ? executed.Attempt : null;

    internal static bool ProducerUnsettled(RunRecord record, ImmutableArray<TaskId> producers) =>
        record.UnresolvedClaims.Any(key => producers.Contains(record.Attempts[key.Attempt].Task));

    /// <summary>
    /// A drift block: a dirty or unowned checkout, except an operation's own operation- or repository-scoped fault, which
    /// that operation reruns.
    /// </summary>
    internal static bool IsDrift(MaterializationBlock block, OperationId? operation = null) =>
        block.Problem is MaterializationProblem.DirtyWorktree or MaterializationProblem.UncertainOwnership &&
        (block.Scope is not (BlockScope.Operation or BlockScope.Repository) || block.Operation != operation);

    /// <summary>
    /// The ids a step of <paramref name="operation"/> records its blocks under, in the order it takes them: the first, then
    /// one numbered from 1 for each block it records after it.
    /// </summary>
    internal static IEnumerable<OperationId> BlockIds(OperationId operation, string step)
    {
        yield return OperationIds.Derive(operation, step + "-blocked");
        for (var number = 1; ; number++) yield return OperationIds.Derive(operation, step + "-blocked-" + number);
    }

    /// <summary>
    /// The oldest unresolved drift block, as <paramref name="operation"/> sees it, on an attempt that <paramref name="attempt"/>
    /// selects. A turn's capture that could not be taken blocks only that attempt's result, not its next turn.
    /// </summary>
    internal static KeyValuePair<OperationId, MaterializationBlockState>? AttemptDrift(RunRecord record, Func<AttemptId, bool> attempt,
        OperationId? operation = null) =>
        record.Blocks.Where(pair => !pair.Value.Resolved && pair.Value.Block.Attempt is { } owner && attempt(owner) &&
                IsDrift(pair.Value.Block, operation) && !record.Claims.Keys.Any(key => key.Attempt == owner && CaptureBlock(key) == pair.Value.Block.Operation))
            .OrderBy(pair => record.Receipts[pair.Key].Sequence)
            .Select(pair => (KeyValuePair<OperationId, MaterializationBlockState>?)pair).FirstOrDefault();

    /// <summary>A first claim waits while a producer it depends on is unsettled.</summary>
    internal static bool ProducerHold(RunRecord record, InputRecord inputs) =>
        Producers(inputs) is { IsEmpty: false } producers && ProducerUnsettled(record, producers);
}
