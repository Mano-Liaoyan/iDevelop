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
        record.CurrentResults.GetValueOrDefault(task)?.Origin is ResultOrigin.Executed executed ? executed.Attempt : null;

    internal static bool ProducerUnsettled(RunRecord record, ImmutableArray<TaskId> producers) =>
        record.UnresolvedClaims.Any(key => producers.Contains(record.Attempts[key.Attempt].Task));

    internal static bool IsDrift(MaterializationBlock block) =>
        block.Problem is MaterializationProblem.DirtyWorktree or MaterializationProblem.UncertainOwnership;

    /// <summary>The oldest unresolved drift block on a producer's publishing attempt.</summary>
    internal static KeyValuePair<OperationId, MaterializationBlockState>? ProducerDrift(RunRecord record, ImmutableArray<TaskId> producers)
    {
        var attempts = producers.Select(task => PublishingAttempt(record, task)).OfType<AttemptId>().ToHashSet();
        return AttemptDrift(record, attempts.Contains);
    }

    /// <summary>The oldest unresolved drift block on an attempt that <paramref name="attempt"/> selects.</summary>
    internal static KeyValuePair<OperationId, MaterializationBlockState>? AttemptDrift(RunRecord record, Func<AttemptId, bool> attempt) =>
        record.Blocks.Where(pair => !pair.Value.Resolved && pair.Value.Block.Attempt is { } owner && attempt(owner) && IsDrift(pair.Value.Block))
            .OrderBy(pair => record.Receipts[pair.Key].Sequence)
            .Select(pair => (KeyValuePair<OperationId, MaterializationBlockState>?)pair).FirstOrDefault();

    /// <summary>A first claim waits while a producer it depends on is unsettled or its checkout drifted.</summary>
    internal static bool ProducerHold(RunRecord record, InputRecord inputs) =>
        Producers(inputs) is { IsEmpty: false } producers && (ProducerUnsettled(record, producers) || ProducerDrift(record, producers) is not null);
}
