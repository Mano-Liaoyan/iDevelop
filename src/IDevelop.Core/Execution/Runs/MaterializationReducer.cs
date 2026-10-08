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
            return e is not (RunEvent.Reserved or RunEvent.Prepared or RunEvent.TurnClaimed or
                RunEvent.Planned { Plan: MaterializationPlan.Preparation or MaterializationPlan.RetryReset or MaterializationPlan.Refresh });
        }
        return e switch
        {
            RunEvent.Planned { Plan: MaterializationPlan.Salvage } => true,
            RunEvent.GitIntended { Mutation: GitMutation.MoveRef move } intent =>
                record.Plans.GetValueOrDefault(intent.Plan) is MaterializationPlan.Salvage salvage &&
                move.Change.Expected is null && move.Change.Ref == salvage.Ref && move.Change.Target == salvage.Commit,
            RunEvent.GitObserved or RunEvent.Blocked or RunEvent.BlockResolved or RunEvent.SalvageRetained => true,
            RunEvent.AttemptClosed or RunEvent.TurnClosed or RunEvent.RootExitObserved or RunEvent.TurnCaptured or RunEvent.CaptureDisposed => record.Phase == RunPhase.Abandoned,
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
                if (!MatchesOwner(record, attempt.Task, prepared.Location.Owner) ||
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
                return (record with { Blocks = record.Blocks.Add(entry.Operation, new(block, false)) }, null);
            case RunEvent.SalvageRetained retained:
                if (record.Plans.GetValueOrDefault(retained.Plan) is not MaterializationPlan.Salvage salvage ||
                    retained.Ref != salvage.Ref || retained.Commit != salvage.Commit || record.Salvages.ContainsKey(retained.Plan) ||
                    !ObservedMove(record, retained.Plan, retained.Ref, null, retained.Commit))
                {
                    return Reject(RunProblem.InvalidData);
                }
                return (record with { Salvages = record.Salvages.Add(retained.Plan, retained) }, null);
            case RunEvent.BlockResolved resolved:
                if (!record.Blocks.TryGetValue(resolved.Block, out var state) || state.Resolved)
                {
                    return Reject(RunProblem.InvalidData);
                }
                return (record with { Blocks = record.Blocks.SetItem(resolved.Block, state with { Resolved = true }) }, null);
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

    private static bool MatchesOwner(RunRecord record, TaskId task, WorktreeOwner owner) =>
        record.RunKey is { } run && record.TaskKeys.TryGetValue(task, out var key) && owner.Task == task &&
        owner.RelativePath == RunLayout.TaskCheckout(run, key) && owner.Branch == RunLayout.TaskBranch(run, key);

    internal static CommitId? AttemptBase(RunRecord record, RunAttempt attempt, InputRecord inputs)
    {
        if (attempt.Cause is AttemptCause.Initial || record.Revisions[attempt.Revision].Snapshot.Tasks[attempt.Task].Blueprint.Work is WorkSpec.Review)
        {
            return inputs.CodeBase;
        }
        var produced = record.Results.LastOrDefault(result => result.Task == attempt.Task && result.Code is CodeOutput.Produced);
        return produced?.Code is CodeOutput.Produced output ? output.Code.Commit :
            record.Preparations.Values.Where(prepared => record.Attempts[prepared.Launch.Attempt].Task == attempt.Task)
                .OrderBy(prepared => record.Receipts.Values.Single(entry => entry.Event is RunEvent.Prepared e &&
                    e.Execution.Launch == prepared.Launch).Sequence).FirstOrDefault()?.Location.AttemptBase;
    }

    internal static bool Editable(RunRecord record, RunAttempt attempt) =>
        record.Revisions[attempt.Revision].Snapshot.Tasks[attempt.Task].Blueprint.Work is WorkSpec.Agent { Access: AgentAccess.Edit };

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
                var input = new InputRecord(preparation.Inputs, preparation.Task, preparation.Revision, preparation.Bindings,
                    new CodeSelection.Root(record.Base.Commit), "", [], preparation.Review);
                if (InputProblem(record, input, true) is { } inputProblem)
                {
                    return inputProblem.Problem;
                }
                if (!Same(preparation.Sources, InputMaterial.Sources(record, preparation.Bindings)) ||
                    !Same(preparation.Review, InputMaterial.Review(record.Revisions[preparation.Revision].Snapshot, preparation.Task, preparation.Bindings)))
                {
                    return RunProblem.InputConflict;
                }
                var prior = Previous(preparation.Cause) ?? (preparation.Cause is AttemptCause.ReviewFix ?
                    record.Receipts.Values.OrderByDescending(entry => entry.Sequence).Select(entry => entry.Event).OfType<RunEvent.Reserved>()
                        .FirstOrDefault(reserved => reserved.Attempt.Task == preparation.Task)?.Attempt.Id : null);
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
                        record.Attempts.Values.Any(attempt => attempt.Task == join.Task && CanRefresh(record, attempt.Id) &&
                            RunStore.Inputs(record, attempt.Task, attempt.Revision, join.Inputs) is { Rejection: null, Inputs: { } capture } &&
                            Same(InputMaterial.Sources(record, capture.Bindings), join.Sources))) &&
                    join.Sources.Select(source => source.Commit).Distinct().Count() >= 2 &&
                    join.Recipe.Parents.SequenceEqual(join.Sources.Select(source => source.Commit).Distinct())
                    ? null : RunProblem.InputConflict;
            case MaterializationPlan.Publication publication:
                if (!record.Attempts.TryGetValue(publication.Attempt, out var publisher))
                {
                    return RunProblem.UnknownAttempt;
                }
                if (!Editable(record, publisher))
                {
                    return RunProblem.UnsupportedResult;
                }
                if (record.Closures.GetValueOrDefault(publisher.Id) is not AttemptEnd.Logged { Outcome: TerminalAttemptOutcome.Succeeded })
                {
                    return RunProblem.OutcomeMismatch;
                }
                if (!record.Preparations.ContainsKey(new(publisher.Id, 1)))
                {
                    return RunProblem.InvalidClaim;
                }
                if (record.Plans.Values.OfType<MaterializationPlan.Publication>().Any(p => p.Attempt == publisher.Id))
                {
                    return RunProblem.StartConflict;
                }
                if (record.Schema == 3)
                {
                    var closure = (AttemptEnd.Logged)record.Closures[publisher.Id];
                    var launch = new LaunchKey(publisher.Id, record.TurnClosures.Keys.Where(key => key.Attempt == publisher.Id).Select(key => key.Turn).DefaultIfEmpty(0).Max());
                    if (publication.Capture is not { } capture || record.Settlements.GetValueOrDefault(launch) != capture ||
                        record.Dispositions.GetValueOrDefault(capture)?.Disposition is not CaptureDisposition.Matched ||
                        !record.RootExits.TryGetValue(launch, out var root) || !record.Captures.TryGetValue(capture, out var observations) ||
                        observations.Count != 2 || observations.Any(o => o.Log != closure.Evidence || o.Report != publication.Report))
                        return RunProblem.OutcomeMismatch;
                    var first = observations[0];
                    if (publication.VerifiedTip != root.Tip || publication.IndexBefore != first.Index?.Content ||
                        !Same(publication.Recipe, first.Recipe) || publication.Commit != first.Candidate || publication.Report != first.Report ||
                        !Same(publication.Artifacts, first.Artifacts.Select(artifact => artifact with
                        { StoredPath = RunStorage.ArtifactPath(publication.Result, artifact.Name) }).ToImmutableArray()))
                        return RunProblem.EvidenceMismatch;
                }
                else if (publication.Capture is not null) return RunProblem.InvalidData;
                return publication.Recipe.Parents.Length == 1 && publication.Recipe.Parents[0] == publication.VerifiedTip ? null : RunProblem.InvalidData;
            case MaterializationPlan.Salvage salvage:
                return record.Attempts.TryGetValue(salvage.Attempt, out var salvaged) && salvaged.Task == salvage.Task &&
                    salvage.Recipe.Parents.Length is 1 or 2 && salvage.Recipe.Parents[0] == salvage.ObservedTip &&
                    (salvage.Recipe.Parents.Length == 1 || salvage.Recipe.Parents[1] == salvage.BranchTip) ? null : RunProblem.InvalidData;
            case MaterializationPlan.RetryReset reset:
                return record.Salvages.ContainsKey(reset.SalvagePlan) &&
                    record.Plans.GetValueOrDefault(reset.SalvagePlan) is MaterializationPlan.Salvage retained &&
                    retained.Task == reset.Task && retained.Attempt == reset.Salvaged && reset.From == retained.BranchTip &&
                    reset.Remove.All(file => retained.Untracked.Any(captured => Same(captured, file))) ? null : RunProblem.InvalidData;
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

    internal static ResultRecord PublicationResult(RunRecord record, MaterializationPlan.Publication plan)
    {
        var attempt = record.Attempts[plan.Attempt];
        var inputs = record.Claims.Values.Where(claim => claim.Key.Attempt == attempt.Id).OrderBy(claim => claim.Key.Turn).Last().Inputs;
        return new(plan.Result, attempt.Task, attempt.Revision, inputs.Id, new ResultOrigin.Executed(attempt.Id), plan.Report, plan.Supersedes)
        {
            Code = new CodeOutput.Produced(new(attempt.Task, attempt.Id, record.Preparations[new(attempt.Id, 1)].Location.AttemptBase,
                plan.Commit, plan.Recipe.Tree, RunLayout.ResultRef(record.RunKey!, record.TaskKeys[attempt.Task], attempt.Id))),
            Artifacts = plan.Artifacts,
        };
    }

    private static bool ObservedMove(RunRecord record, OperationId plan, string reference, CommitId? expected, CommitId target) =>
        record.GitIntents.Any(pair => pair.Value.Plan == plan && pair.Value.Mutation is GitMutation.MoveRef move &&
            move.Change == new RefChange(reference, expected, target) && record.GitObservations.ContainsKey(pair.Key) &&
            record.GitObservations[pair.Key].Value == target.Hex);

    private static RunProblem? ResultCodeProblem(RunRecord record, RunEvent.ResultAccepted accepted, TaskDefinition task)
    {
        var result = accepted.Result;
        if (task.Blueprint.Work is WorkSpec.Agent { Access: AgentAccess.Edit })
        {
            if (result.Origin is not ResultOrigin.Executed executed ||
                record.Plans.FirstOrDefault(pair => pair.Value is MaterializationPlan.Publication p && p.Attempt == executed.Attempt) is not
                    { Value: MaterializationPlan.Publication publication } pair)
            {
                return RunProblem.UnsupportedResult;
            }
            var attempt = record.Attempts[executed.Attempt];
            if (!Same(result, PublicationResult(record, publication)) ||
                !ObservedMove(record, pair.Key, RunLayout.TaskBranch(record.RunKey!, record.TaskKeys[attempt.Task]), publication.VerifiedTip, publication.Commit) ||
                !ObservedMove(record, pair.Key, RunLayout.ResultRef(record.RunKey!, record.TaskKeys[attempt.Task], attempt.Id), null, publication.Commit))
            {
                return RunProblem.InputConflict;
            }
        }
        else
        {
            CodeOutput? code = accepted.Inputs.Code is CodeSelection.Single or CodeSelection.Joined ? new CodeOutput.Forwarded(accepted.Inputs.Id) : null;
            if (!Same(result.Code, code) || result.Artifacts.Length != 0 || task.Blueprint.Work is WorkSpec.Review && accepted.Inputs.Review is null)
            {
                return RunProblem.InputConflict;
            }
        }
        return null;
    }
}
