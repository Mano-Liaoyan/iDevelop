using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

internal static partial class RunReducer
{
    internal static bool Permitted(RunRecord record, RunEvent e)
    {
        if (e is RunEvent.OwnershipFenced) return true;
        if (record.Phase == RunPhase.Approved)
        {
            return true;
        }
        if (record.Phase == RunPhase.StopRequested)
        {
            return e is not (RunEvent.Reserved or RunEvent.Prepared or RunEvent.TurnClaimed or RunEvent.Requested or
                RunEvent.Planned { Plan: MaterializationPlan.Preparation or MaterializationPlan.Refresh });
        }
        return e switch
        {
            RunEvent.GitObserved or RunEvent.Blocked or RunEvent.BlockResolved => true,
            RunEvent.AttemptClosed or RunEvent.TurnClosed or RunEvent.RootExitObserved or RunEvent.TurnCaptured => record.Phase == RunPhase.Abandoned,
            _ => false,
        };
    }

    private static (RunRecord Record, RunProblem? Problem) ApplyMaterialization(RunRecord record, RunEntry entry)
    {
        (RunRecord, RunProblem?) Reject(RunProblem problem) => (record, problem);
        switch (entry.Event)
        {
            case RunEvent.LayoutAllocated { Key: LayoutKey.Run key }:
                if (!ValidKey(record.Id.Value, key.Key) || !System.IO.Path.IsPathFullyQualified(key.Repository))
                {
                    return Reject(RunProblem.InvalidData);
                }
                if (record.RunKey is not null)
                {
                    return Reject(RunProblem.StartConflict);
                }
                return (record with { RunKey = key.Key, Repository = key.Repository }, null);
            case RunEvent.LayoutAllocated { Key: LayoutKey.Task key }:
                if (!ValidKey(key.TaskId.Value, key.Key) || !KnownTask(record, key.TaskId))
                {
                    return Reject(RunProblem.InvalidData);
                }
                if (record.TaskKeys.ContainsKey(key.TaskId) || record.TaskKeys.ContainsValue(key.Key))
                {
                    return Reject(RunProblem.StartConflict);
                }
                return (record with { TaskKeys = record.TaskKeys.Add(key.TaskId, key.Key) }, null);
            case RunEvent.Planned { Plan: MaterializationPlan.Preparation preparation } when
                record.Plans.Values.OfType<MaterializationPlan.Preparation>().Any(plan => plan.Attempt == preparation.Attempt || plan.Inputs == preparation.Inputs):
                return Reject(RunProblem.InputConflict);
            case RunEvent.Planned planned:
                if (PlanProblem(record, planned.Plan) is { } planProblem)
                {
                    return Reject(planProblem);
                }
                var updated = record with { Plans = record.Plans.Add(entry.Operation, planned.Plan) };
                if (planned.Plan is MaterializationPlan.Refresh refresh)
                {
                    var reviewer = record.Attempts[refresh.Launch.Attempt];
                    updated = updated with { Inputs = updated.Inputs.Add(refresh.Inputs, InputMaterial.Build(record, refresh.Inputs,
                        reviewer.Task, reviewer.Revision, refresh.Bindings, refresh.Sources, refresh.Review, refresh.Composed)) };
                }
                return (updated, null);
            case RunEvent.GitIntended intended:
                if (intended.Plan == entry.Operation ||
                    !record.Plans.ContainsKey(intended.Plan) && !(record.Receipts.GetValueOrDefault(intended.Plan)?.Event is
                        RunEvent.LayoutAllocated { Key: LayoutKey.Run layout } && intended.Mutation is GitMutation.MoveRef baseMove &&
                        baseMove.Change == new RefChange(RunLayout.ApprovedBase(layout.Key), null, record.Base.Commit)))
                {
                    return Reject(RunProblem.InvalidData);
                }
                return (record with { GitIntents = record.GitIntents.Add(entry.Operation, intended) }, null);
            case RunEvent.GitObserved observed:
                if (observed.Mutation == entry.Operation || !record.GitIntents.ContainsKey(observed.Mutation) ||
                    record.GitObservations.ContainsKey(observed.Mutation))
                {
                    return Reject(RunProblem.InvalidData);
                }
                return (record with { GitObservations = record.GitObservations.Add(observed.Mutation, observed.Observation) }, null);
            case RunEvent.Prepared { Execution: var prepared }:
                if (!record.Attempts.TryGetValue(prepared.Launch.Attempt, out var attempt))
                {
                    return Reject(RunProblem.UnknownAttempt);
                }
                if (record.Closures.ContainsKey(attempt.Id) || record.Preparations.ContainsKey(prepared.Launch) ||
                    prepared.Launch.Turn > 1 && !record.TurnClosures.ContainsKey(new(attempt.Id, prepared.Launch.Turn - 1)))
                {
                    return Reject(RunProblem.InvalidClaim);
                }
                if (!record.Inputs.TryGetValue(prepared.Inputs, out var inputs) || inputs.Task != attempt.Task ||
                    inputs.Revision != attempt.Revision || prepared.Launch.Turn == 1 && prepared.Inputs != attempt.InitialInputs)
                {
                    return Reject(RunProblem.InputConflict);
                }
                var refreshPlan = record.Plans.FirstOrDefault(pair => pair.Value is MaterializationPlan.Refresh refresh &&
                    refresh.Launch == prepared.Launch && refresh.Inputs == inputs.Id);
                if (prepared.Launch.Turn > 1 && prepared.Inputs != record.Preparations.GetValueOrDefault(new(attempt.Id, prepared.Launch.Turn - 1))?.Inputs &&
                    (refreshPlan.Value is not MaterializationPlan.Refresh || !record.GitIntents.Any(pair => pair.Value.Plan == refreshPlan.Key &&
                        pair.Value.Mutation is GitMutation.ResetCheckout reset && reset.Task == attempt.Task && reset.Target == inputs.CodeBase &&
                        record.GitObservations.GetValueOrDefault(pair.Key)?.Value == inputs.CodeBase.Hex)))
                    return Reject(RunProblem.InputConflict);
                if ((prepared.Launch.Turn == 1 || refreshPlan.Value is MaterializationPlan.Refresh) && InputProblem(record, inputs, true) is { } stale)
                {
                    return Reject(stale.Problem);
                }
                if (!MatchesOwner(record, attempt, prepared.Location.Owner) ||
                    prepared.Location.AttemptBase != AttemptBase(record, attempt, inputs) || prepared.PromptHash != Revision.Hash(prepared.Prompt) ||
                    prepared.OutboxPath != (Editable(record, attempt) ? RunLayout.Outbox(attempt.Id) : ""))
                {
                    return Reject(RunProblem.InputConflict);
                }
                return (record with { Preparations = record.Preparations.Add(prepared.Launch, prepared) }, null);
            case RunEvent.Blocked { Block: var block }:
                if (!KnownTask(record, block.Task) || block.Attempt is { } blockedAttempt &&
                    !record.Attempts.ContainsKey(blockedAttempt) ||
                    block.Inputs is { } blockedInput && !record.Inputs.ContainsKey(blockedInput) &&
                    !record.Plans.Values.OfType<MaterializationPlan.Preparation>().Any(plan => plan.Inputs == blockedInput) &&
                    !(block.Attempt is { } refreshing && CanRefresh(record, refreshing) &&
                        blockedInput.Value == OperationIds.Derive(block.Operation, "refresh-input").Value))
                {
                    return Reject(RunProblem.InvalidData);
                }
                // A turn's capture that could not be taken blocks under its turn's capture operation, once, after its root exit.
                if (block.Attempt is { } captured && record.Claims.Keys.Where(key => key.Attempt == captured && CaptureBlock(key) == block.Operation)
                    .Select(key => (LaunchKey?)key).FirstOrDefault() is { } launch &&
                    (!record.RootExits.ContainsKey(launch) || record.TurnClosures.ContainsKey(launch) || CaptureSettled(record, launch)))
                {
                    return Reject(RunProblem.InvalidClaim);
                }
                return (record with { Blocks = record.Blocks.Add(entry.Operation, new(block, false)) }, null);
            case RunEvent.BlockResolved resolved:
                if (!record.Blocks.TryGetValue(resolved.Block, out var state) || state.Resolved)
                {
                    return Reject(RunProblem.InvalidData);
                }
                return (record with { Blocks = record.Blocks.SetItem(resolved.Block, state with { Resolved = true }) }, null);
            case RunEvent.ConflictResolved resolved:
                return ConflictResolutionProblem(record, resolved.Conflict) is { } resolutionProblem ? Reject(resolutionProblem) : (record, null);
            default:
                return Reject(RunProblem.UnsupportedEvent);
        }
    }

    private static bool CanRefresh(RunRecord record, AttemptId attempt) =>
        record.Attempts.TryGetValue(attempt, out var reviewer) && !record.Closures.ContainsKey(attempt) &&
        record.Revisions[reviewer.Revision].Snapshot.Tasks[reviewer.Task].Blueprint.Work is WorkSpec.Review &&
        record.Preparations.Keys.Any(key => key.Attempt == attempt && record.TurnClosures.ContainsKey(key) &&
            !record.Preparations.ContainsKey(new(attempt, key.Turn + 1)));

    private static bool ValidKey(Guid id, string key) => key.Length is >= 8 and <= 64 &&
        Revision.Hash(id.ToString("D")).Sha256.StartsWith(key, StringComparison.Ordinal);

    private static bool KnownTask(RunRecord record, TaskId task) => record.Revisions.Values.Any(revision => revision.Snapshot.Tasks.ContainsKey(task));

    /// <summary>An editing task's worktree is on the task's branch, and a read-only task's worktree has no branch (ADR 0032).</summary>
    private static bool MatchesOwner(RunRecord record, RunAttempt attempt, WorktreeOwner owner) =>
        record.RunKey is { } run && record.TaskKeys.TryGetValue(attempt.Task, out var key) && owner.Task == attempt.Task &&
        owner.RelativePath == RunLayout.TaskCheckout(run, key) &&
        owner.Branch == (Editable(record, attempt) ? RunLayout.TaskBranch(run, key) : null);

    internal static CommitId? AttemptBase(RunRecord record, RunAttempt attempt, InputRecord inputs)
    {
        // Each attempt of a Merge node starts at the conflict it resolves, its markers included.
        if (MergeOf(record, attempt.Cause) is { } conflict) return conflict.Conflicted;
        if (attempt.Cause is AttemptCause.Initial || record.Revisions[attempt.Revision].Snapshot.Tasks[attempt.Task].Blueprint.Work is WorkSpec.Review)
        {
            return inputs.CodeBase;
        }
        // A Retry whose inputs' work changed starts at the merge of that work into its earlier change, or without an earlier
        // change, at that work itself; its worktree's files go along (#125).
        if (attempt.Cause is AttemptCause.Retry && Editable(record, attempt) && Moves(record, attempt, inputs)) return inputs.CodeBase;
        return Earlier(record, attempt.Task);
    }

    /// <summary>
    /// Where a later attempt of an editing task starts while its inputs' work is unchanged: its newest result's code, or
    /// without a result, where its first attempt started. Its worktree's branch is there.
    /// </summary>
    internal static CommitId? Earlier(RunRecord record, TaskId task) =>
        record.Results.LastOrDefault(result => result.Task == task && (result.Code is CodeOutput.Produced || result.Origin is ResultOrigin.Merged)) switch
        {
            { Code: CodeOutput.Produced output } => output.Code.Commit,
            // A Merge node's fix starts at the merge its own step handed on.
            { Code: CodeOutput.Forwarded forwarded } => record.Inputs[forwarded.Inputs].CodeBase,
            _ => First(record, task),
        };

    /// <summary>
    /// Whether an editing task's Retry starts somewhere other than where its worktree's branch is: its inputs' code merges
    /// with its current result's code, or it has no result and its inputs' code is not where its first attempt started.
    /// </summary>
    private static bool Moves(RunRecord record, RunAttempt attempt, InputRecord inputs)
    {
        if (inputs.Code is CodeSelection.Joined joined && joined.Join.Sources.Any(source => source.Task == attempt.Task)) return true;
        if (record.Results.Any(result => result.Task == attempt.Task && result.Code is CodeOutput.Produced)) return false;
        return First(record, attempt.Task) != inputs.CodeBase;
    }

    /// <summary>Where the task's first prepared attempt in this run started.</summary>
    private static CommitId? First(RunRecord record, TaskId task) =>
        record.Preparations.Values.Where(prepared => record.Attempts[prepared.Launch.Attempt].Task == task)
            .OrderBy(prepared => record.Receipts.Values.Single(entry => entry.Event is RunEvent.Prepared e &&
                e.Execution.Launch == prepared.Launch).Sequence).FirstOrDefault()?.Location.AttemptBase;

    /// <summary>
    /// Whether the attempt edits the project on its task's branch: an editing agent's, or a Merge node's that fixes a review
    /// round of its merge rather than resolving a conflict, as a journal an earlier build wrote holds (ADR 0041).
    /// </summary>
    internal static bool Editable(RunRecord record, RunAttempt attempt) =>
        record.Revisions[attempt.Revision].Snapshot.Tasks[attempt.Task].Blueprint.Work switch
        {
            WorkSpec.Agent { Access: AgentAccess.Edit } => true,
            WorkSpec.Merge => MergeOf(record, attempt.Cause) is null,
            _ => false,
        };

    internal static RunProblem? PlanProblem(RunRecord record, MaterializationPlan plan, bool recorded = false)
    {
        switch (plan)
        {
            case MaterializationPlan.Preparation preparation:
                if (ReservationTaskProblem(record, preparation.Task, preparation.Revision, recorded) is { } taskProblem)
                {
                    return taskProblem.Problem;
                }
                if (ReservationProblem(record, preparation.Task, preparation.Cause) is { } reservation)
                {
                    return reservation;
                }
                // A Merge node's attempt takes no results: only the conflict it resolves.
                if (MergeOf(record, preparation.Cause) is not null)
                {
                    return preparation.Bindings.IsEmpty && preparation.Sources.IsEmpty && preparation.Review is null ? null : RunProblem.InputConflict;
                }
                var input = new InputRecord(preparation.Inputs, preparation.Task, preparation.Revision, preparation.Bindings,
                    new CodeSelection.Root(record.Base.Commit), "", [], preparation.Review);
                if (InputProblem(record, input, true) is { } inputProblem)
                {
                    return inputProblem.Problem;
                }
                if (!Same(preparation.Sources, InputMaterial.Sources(record, preparation.Task, preparation.Revision, preparation.Cause, preparation.Bindings)) ||
                    !Same(preparation.Review, InputMaterial.Review(record.Revisions[preparation.Revision].Snapshot, preparation.Task, preparation.Bindings)))
                {
                    return RunProblem.InputConflict;
                }
                // A Retry is the person's deliberate choice, or a rerun of a result that went out of date, so it takes the
                // inputs as they are now (#125).
                if (preparation.Cause is AttemptCause.Retry) return null;
                var prior = Previous(preparation.Cause) ?? (preparation.Cause is AttemptCause.ReviewFix ?
                    record.Receipts.Values.OrderByDescending(entry => entry.Sequence).Select(entry => entry.Event).OfType<RunEvent.Reserved>()
                        // A Merge node's attempt that resolved a conflict took no inputs to compare with.
                        .FirstOrDefault(reserved => reserved.Attempt.Task == preparation.Task && MergeOf(record, reserved.Attempt.Cause) is null)?.Attempt.Id : null);
                if (prior is { } previous && record.Plans.Values.OfType<MaterializationPlan.Preparation>().FirstOrDefault(p => p.Attempt == previous) is { } old &&
                    !Same(old.Sources, preparation.Sources))
                {
                    return RunProblem.StaleInput;
                }
                return null;
            case MaterializationPlan.Refresh refresh:
                if (!record.Attempts.TryGetValue(refresh.Launch.Attempt, out var reviewer)) return RunProblem.UnknownAttempt;
                if (record.Revisions[reviewer.Revision].Snapshot.Tasks[reviewer.Task].Blueprint.Work is not WorkSpec.Review ||
                    refresh.Launch.Turn < 2 || record.Closures.ContainsKey(reviewer.Id) ||
                    !record.TurnClosures.ContainsKey(new(reviewer.Id, refresh.Launch.Turn - 1)) || record.Preparations.ContainsKey(refresh.Launch))
                    return RunProblem.InvalidClaim;
                if (record.Inputs.ContainsKey(refresh.Inputs) || record.Plans.Values.OfType<MaterializationPlan.Refresh>().Any(p => p.Launch == refresh.Launch))
                    return RunProblem.InputConflict;
                var refreshedInput = new InputRecord(refresh.Inputs, reviewer.Task, reviewer.Revision, refresh.Bindings,
                    new CodeSelection.Root(record.Base.Commit), "", [], refresh.Review);
                if (InputProblem(record, refreshedInput, true) is { } freshProblem) return freshProblem.Problem;
                if (!Same(refresh.Sources, InputMaterial.Sources(record, refresh.Bindings)) ||
                    !Same(refresh.Review, InputMaterial.Review(record.Revisions[reviewer.Revision].Snapshot, reviewer.Task, refresh.Bindings)) || refresh.Review is null)
                    return RunProblem.InputConflict;
                try
                {
                    InputMaterial.Build(record, refresh.Inputs, reviewer.Task, reviewer.Revision, refresh.Bindings,
                        refresh.Sources, refresh.Review, refresh.Composed);
                }
                catch (ArgumentException) { return RunProblem.InputConflict; }
                if (refresh.Composed is { } refreshedJoin &&
                    (record.Plans.GetValueOrDefault(refreshedJoin.Operation) is not MaterializationPlan.Join refreshedPlan ||
                        refreshedPlan.Task != reviewer.Task || refreshedPlan.Inputs != refresh.Inputs ||
                        !Same(refreshedPlan.Sources, refresh.Sources) || refreshedPlan.Commit != refreshedJoin.Commit ||
                        refreshedPlan.Recipe.Tree != refreshedJoin.Tree || refreshedPlan.Ref != refreshedJoin.Ref ||
                        !ObservedMove(record, refreshedJoin.Operation, refreshedPlan.Ref, refreshedPlan.Previous, refreshedPlan.Commit)))
                    return RunProblem.InputConflict;
                return null;
            case MaterializationPlan.Join join:
                return record.RunKey is { } run && record.TaskKeys.TryGetValue(join.Task, out var key) &&
                    join.Ref == RunLayout.JoinBranch(run, key) &&
                    (record.Plans.Values.OfType<MaterializationPlan.Preparation>().Any(preparation =>
                        preparation.Task == join.Task && preparation.Inputs == join.Inputs && Same(preparation.Sources, join.Sources)) ||
                        GateJoin(record, join) ||
                        record.Attempts.Values.Any(attempt => attempt.Task == join.Task && CanRefresh(record, attempt.Id) &&
                            RunStore.Inputs(record, attempt.Task, attempt.Revision, join.Inputs) is { Rejection: null, Inputs: { } capture } &&
                            Same(InputMaterial.Sources(record, capture.Bindings), join.Sources))) &&
                    join.Sources.Select(source => source.Commit).Distinct().Count() >= 2 &&
                    join.Recipe.Parents.SequenceEqual(join.Sources.Select(source => source.Commit).Distinct())
                    ? null : RunProblem.InputConflict;
            default:
                return RunProblem.InvalidData;
        }
    }

    private static RunProblem? ReservedProblem(RunRecord record, RunEvent.Reserved reserved)
    {
        var plan = record.Plans.Values.OfType<MaterializationPlan.Preparation>().FirstOrDefault(p => p.Attempt == reserved.Attempt.Id);
        if (plan is null || plan.Inputs != reserved.Inputs.Id || plan.Task != reserved.Attempt.Task || plan.Revision != reserved.Attempt.Revision ||
            !Same(plan.Cause, reserved.Attempt.Cause))
        {
            return RunProblem.InputConflict;
        }
        try
        {
            var expected = InputMaterial.Build(record, plan, (reserved.Inputs.Code as CodeSelection.Joined)?.Join);
            return SameInput(expected, reserved.Inputs) ? null : RunProblem.InputConflict;
        }
        catch (ArgumentException)
        {
            return RunProblem.InputConflict;
        }
    }

    /// <summary>
    /// The capture a closed turn <paramref name="launch"/> took, when it matched its turn's root observation. Null otherwise.
    /// </summary>
    internal static CaptureObservation? MatchedCapture(RunRecord record, LaunchKey launch) =>
        record.TurnClosures.ContainsKey(launch) && record.RootExits.ContainsKey(launch) &&
        record.Captures.TryGetValue(launch, out var capture) && Divergence(record, capture) is null ? capture : null;

    /// <summary>
    /// The capture an editing attempt's <paramref name="launch"/> ended with, when it matched its turn and holds the commit
    /// the turn ended with. Null otherwise.
    /// </summary>
    internal static CaptureObservation? ResultCapture(RunRecord record, LaunchKey launch) =>
        MatchedCapture(record, launch) is { Candidate: not null } capture ? capture : null;

    /// <summary>The plan an attempt's Git steps record under: the plan that prepared it.</summary>
    internal static OperationId? AttemptPlan(RunRecord record, AttemptId attempt) =>
        record.Plans.Where(pair => pair.Value is MaterializationPlan.Preparation plan && plan.Attempt == attempt)
            .Select(pair => (OperationId?)pair.Key).FirstOrDefault();

    /// <summary>The move that makes the commit <paramref name="launch"/> ended with its editing task's branch tip (ADR 0032).</summary>
    internal static RefChange ResultMove(RunRecord record, LaunchKey launch, CaptureObservation capture) =>
        new(record.Preparations[launch].Location.Owner.Branch!, record.RootExits[launch].Tip, capture.Candidate!.Value);

    /// <summary>The id an editing attempt's accepted result takes, derived from the acceptance, under which its artifacts are stored.</summary>
    internal static ResultId EditedResultId(OperationId accept) => new(OperationIds.Derive(accept, "result").Value);

    /// <summary>
    /// An editing attempt's result: its report, the artifacts its outbox held at the end of its last turn, and the commit that
    /// turn ended with as its code. A review fix keeps its prior result's artifacts that it does not declare again.
    /// </summary>
    internal static ResultRecord EditedResult(RunRecord record, LaunchKey launch, CaptureObservation capture, ResultId id, InputId inputs,
        string report, ResultId? supersedes)
    {
        var attempt = record.Attempts[launch.Attempt];
        var location = record.Preparations[new(attempt.Id, 1)].Location;
        return new(id, attempt.Task, attempt.Revision, inputs, new ResultOrigin.Executed(attempt.Id), report, supersedes)
        {
            Code = new CodeOutput.Produced(new(attempt.Task, attempt.Id, location.AttemptBase, capture.Candidate!.Value, capture.Recipe.Tree,
                location.Owner.Branch!)),
            Artifacts = [.. capture.Artifacts.Select(artifact => artifact with { StoredPath = RunStorage.ArtifactPath(id, artifact.Name) }),
                .. CarriedArtifacts(record, attempt.Id, capture.Artifacts, id)],
        };
    }

    private static bool ObservedMove(RunRecord record, OperationId plan, string reference, CommitId? expected, CommitId target) =>
        record.GitIntents.Any(pair => pair.Value.Plan == plan && pair.Value.Mutation is GitMutation.MoveRef move &&
            move.Change == new RefChange(reference, expected, target) && record.GitObservations.ContainsKey(pair.Key) &&
            record.GitObservations[pair.Key].Value == target.Hex);

    private static RunProblem? ResultCodeProblem(RunRecord record, RunEvent.ResultAccepted accepted, TaskDefinition task)
    {
        var result = accepted.Result;
        if (task.Blueprint.Work is WorkSpec.Agent { Access: AgentAccess.Edit } || task.Blueprint.Work is WorkSpec.Merge && result.Origin is ResultOrigin.Executed)
        {
            if (result.Origin is not ResultOrigin.Executed executed || !record.Attempts.TryGetValue(executed.Attempt, out var attempt) ||
                !Editable(record, attempt))
                return RunProblem.UnsupportedResult;
            // The commit the attempt's last turn ended with is its code, once it is the task branch's tip.
            var final = record.Claims.Keys.Where(key => key.Attempt == attempt.Id).OrderBy(key => key.Turn).LastOrDefault();
            if (final.Attempt != attempt.Id || ResultCapture(record, final) is not { } capture || AttemptPlan(record, attempt.Id) is not { } plan)
                return RunProblem.UnsupportedResult;
            var move = ResultMove(record, final, capture);
            return Same(result, EditedResult(record, final, capture, result.Id, result.Inputs, result.Report, result.Supersedes)) &&
                ObservedMove(record, plan, move.Ref, move.Expected, move.Target) ? null : RunProblem.InputConflict;
        }
        else
        {
            CodeOutput? code = accepted.Inputs.Code is CodeSelection.Single or CodeSelection.Joined ? new CodeOutput.Forwarded(accepted.Inputs.Id) : null;
            // An approval and a review forward artifacts: exactly their inputs' dependency artifacts, stored under their own result.
            var reviews = task.Blueprint.Work is WorkSpec.Review && result.Origin is ResultOrigin.Executed;
            var artifacts = result.Origin is ResultOrigin.Human or ResultOrigin.Merged || reviews
                ? GateForwarding.Artifacts(record, accepted.Inputs, result.Id) : ([], null);
            if (!Same(result.Code, code) || reviews && artifacts.Collision is not null || !Same(result.Artifacts, artifacts.Artifacts) ||
                task.Blueprint.Work is WorkSpec.Review && accepted.Inputs.Review is null)
            {
                return RunProblem.InputConflict;
            }
        }
        return null;
    }
}
