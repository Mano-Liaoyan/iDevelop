using System.Collections.Immutable;
using IDevelop.Nodes;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>
/// What a run-owned planner may fill and place, derived from its attempt alone, so every preparation, claim, and recovery
/// renders the same prompt. Its slots are the empty tasks its dependency connections reach in the attempt's revision. Its
/// types are the built-in blueprints, then each other blueprint that revision uses, by key: the blueprints an amendment
/// can resolve.
/// </summary>
internal static class RunPlanning
{
    public static ImmutableArray<Blueprint> Types(Workflow snapshot) =>
    [
        .. BuiltInBlueprints.All.Select(builtIn => snapshot.Blueprints.GetValueOrDefault(builtIn.Key) ?? builtIn),
        .. snapshot.Blueprints.Values.Where(blueprint => BuiltInBlueprints.Find(blueprint.Key) is null),
    ];

    /// <summary>The blueprint a run's proposal names by <paramref name="key"/>: the revision's own copy, else a built-in.</summary>
    public static Blueprint? Find(Workflow snapshot, BlueprintKey key) => snapshot.Blueprints.GetValueOrDefault(key) ?? BuiltInBlueprints.Find(key);

    /// <summary>The planning context of <paramref name="attempt"/>'s prompt, or null for a task that proposes nothing.</summary>
    public static PlanningContext? Context(RunRecord record, RunAttempt attempt)
    {
        var snapshot = record.Revisions[attempt.Revision].Snapshot;
        // What started is left out: a task the planner's dependencies reach starts only after its result, and an
        // amendment never fills a task that started.
        return snapshot.Tasks[attempt.Task].Blueprint.Work is WorkSpec.Agent { Proposes: true }
            ? PlanningContext.For(snapshot, attempt.Task, Types(snapshot), _ => false) : null;
    }

    /// <summary>
    /// The handles <paramref name="attempt"/>'s log records. Each attempt plans under its own id; a continuation keeps the
    /// handles of the attempt it continues, as a standalone continuation does, so a node proposed again keeps its id.
    /// </summary>
    public static PlanningHandles? Handles(RunRecord record, RunAttempt attempt) => attempt.Cause is AttemptCause.Continue continued &&
        record.Attempts.TryGetValue(continued.Previous, out var previous)
        ? Handles(record, previous)
        : Context(record, attempt)?.Handles(attempt.Id.Value);
}
