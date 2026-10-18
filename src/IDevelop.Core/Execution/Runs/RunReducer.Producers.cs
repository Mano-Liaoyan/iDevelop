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

    /// <summary>
    /// The attempt whose result is the task's current one. A drift block on its checkout names this attempt. A rebased
    /// result keeps the checkout of the attempt that published the code it replays.
    /// </summary>
    internal static AttemptId? PublishingAttempt(RunRecord record, TaskId task) => record.CurrentResults.GetValueOrDefault(task) switch
    {
        { Origin: ResultOrigin.Executed executed } => executed.Attempt,
        { Origin: ResultOrigin.Rebased, Code: CodeOutput.Produced produced } => produced.Code.Attempt,
        _ => null,
    };

    internal static bool ProducerUnsettled(RunRecord record, ImmutableArray<TaskId> producers) =>
        record.UnresolvedClaims.Any(key => producers.Contains(record.Attempts[key.Attempt].Task));

    /// <summary>The step whose blocks a resting closure's baseline check recorded in earlier journals.</summary>
    internal const string ClosureCheck = "close-recheck";

    /// <summary>
    /// Publish's view of drift: a dirty or unowned checkout, except an operation's own operation- or repository-scoped
    /// fault, which that operation reruns. A failed closure check that an earlier journal holds is no drift either.
    /// </summary>
    internal static bool IsDrift(RunRecord record, OperationId id, MaterializationBlock block, OperationId? operation = null) =>
        block.Problem is MaterializationProblem.DirtyWorktree or MaterializationProblem.UncertainOwnership &&
        (block.Scope is not (BlockScope.Operation or BlockScope.Repository) || block.Operation != operation) && !ClosureFault(record, id, block);

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
    /// An operation-scoped fault that a resting closure's baseline check recorded under its own operation in an earlier
    /// journal. The check took its block ids in order, each once, so they are the ids the journal holds in an unbroken run
    /// from the first, however many times the check failed (#85).
    /// </summary>
    internal static bool ClosureFault(RunRecord record, OperationId id, MaterializationBlock block)
    {
        if (block.Scope is not BlockScope.Operation) return false;
        foreach (var taken in BlockIds(block.Operation, ClosureCheck))
        {
            if (taken == id) return true;
            if (!record.Receipts.ContainsKey(taken)) return false;
        }
        return false;
    }

    /// <summary>The oldest unresolved drift block, as <paramref name="operation"/> sees it, on an attempt that <paramref name="attempt"/> selects.</summary>
    internal static KeyValuePair<OperationId, MaterializationBlockState>? AttemptDrift(RunRecord record, Func<AttemptId, bool> attempt,
        OperationId? operation = null) =>
        record.Blocks.Where(pair => !pair.Value.Resolved && pair.Value.Block.Attempt is { } owner && attempt(owner) &&
                IsDrift(record, pair.Key, pair.Value.Block, operation))
            .OrderBy(pair => record.Receipts[pair.Key].Sequence)
            .Select(pair => (KeyValuePair<OperationId, MaterializationBlockState>?)pair).FirstOrDefault();

    /// <summary>A first claim waits while a producer it depends on is unsettled.</summary>
    internal static bool ProducerHold(RunRecord record, InputRecord inputs) =>
        Producers(inputs) is { IsEmpty: false } producers && ProducerUnsettled(record, producers);
}
