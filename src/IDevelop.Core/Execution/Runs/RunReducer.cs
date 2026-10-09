using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>Folds run events into a record and validates their transitions.</summary>
internal static partial class RunReducer
{
    public static RunRead Replay(WorkflowId workflow, RunId run, IEnumerable<RunEntry> entries)
    {
        RunRecord? record = null;
        foreach (var entry in entries)
        {
            var applied = Apply(workflow, run, record, entry);
            if (applied is RunRead.Rejected)
            {
                return applied;
            }

            record = ((RunRead.Loaded)applied).Record;
        }
        return record is null ? new RunRead.Rejected(new(RunProblem.NotApproved)) : new RunRead.Loaded(record);
    }

    public static RunRead Apply(WorkflowId workflow, RunId run, RunRecord? record, RunEntry entry)
    {
        RunRead Reject(RunProblem problem, TaskId? task = null) => new RunRead.Rejected(new(problem, entry.Sequence, task), record);
        if (entry.Schema is not (1 or 2 or 3) || record is not null && entry.Schema != record.Schema)
        {
            return Reject(RunProblem.UnsupportedSchema);
        }

        if (entry.Sequence != (record?.Sequence ?? 0) + 1)
        {
            return Reject(RunProblem.SequenceGap);
        }

        if (RunValidation.Entry(entry) is { } invalid)
        {
            return Reject(invalid);
        }

        if (record is null)
        {
            if (entry.Event is not RunEvent.Approved approved)
            {
                return Reject(RunProblem.NotApproved);
            }

            if (approved.Run != run || approved.Revision.Snapshot.Id != workflow)
            {
                return Reject(RunProblem.IdentityMismatch);
            }

            record = new(run, workflow, approved.Base, approved.Revision) { Schema = entry.Schema };
            foreach (var included in approved.Included ?? [])
            {
                if (IncludedProblem(record, included) is { } problem) return Reject(problem, included.Result.Task);
                record = record with
                {
                    Results = record.Results.Add(included.Result),
                    Inputs = record.Inputs.Add(included.Inputs.Id, included.Inputs)
                };
            }

            if (approved.Node is { } requested)
            {
                if (!approved.Revision.Snapshot.Tasks.ContainsKey(requested)) return Reject(RunProblem.IdentityMismatch, requested);
                record = record with { Requested = [requested] };
            }

            // Run Workflow runs every task, so it carries nothing from earlier runs (#90).
            if (approved.Carried is not null && approved.Node is null) return Reject(RunProblem.UnsupportedResult);
            var carried = Carry(record, approved.Carried, entry.Operation);
            if (carried.Problem is { } carriedProblem) return Reject(carriedProblem, carried.Task);
            record = carried.Record;

            // A node runs only after every task before it has a result, which at approval only an included report or a
            // result carried from an earlier run can be.
            if (approved.Node is { } node && RunScope.Missing(record, node) is { } missing) return Reject(RunProblem.MissingDependencyResult, missing);
        }
        else
        {
            if (record.Id != run || record.Workflow != workflow)
            {
                return Reject(RunProblem.IdentityMismatch);
            }

            if (record.Receipts.ContainsKey(entry.Operation))
            {
                return Reject(RunProblem.OperationConflict);
            }

            if (entry.Event is RunEvent.Approved)
            {
                return Reject(RunProblem.StartConflict);
            }

            if (!Permitted(record, entry.Event))
            {
                return Reject(RunProblem.RunStopped);
            }

            switch (entry.Event)
            {
                case RunEvent.Amended amended:
                    if (amended.Previous != record.Revision.Id)
                    {
                        return Reject(RunProblem.RevisionConflict);
                    }

                    if (amended.Revision.Snapshot.Id != workflow)
                    {
                        return Reject(RunProblem.IdentityMismatch);
                    }

                    if (AmendmentProblem(record, amended.Revision.Snapshot) is { } amendment)
                    {
                        return Reject(amendment);
                    }

                    var revision = record.Revisions.GetValueOrDefault(amended.Revision.Id) ?? amended.Revision;
                    record = record with
                    {
                        Revision = revision,
                        Revisions = record.Revisions.SetItem(revision.Id, revision)
                    };
                    break;
                case RunEvent.Reserved reserved:
                    var attempt = reserved.Attempt;
                    if (ReservationTaskProblem(record, attempt.Task, attempt.Revision,
                        recorded: record.Schema >= 2 && record.Plans.Values.OfType<MaterializationPlan.Preparation>()
                            .Any(plan => plan.Attempt == attempt.Id)) is { } taskProblem)
                    {
                        return Reject(taskProblem.Problem, taskProblem.Task);
                    }

                    if (record.Attempts.ContainsKey(attempt.Id))
                    {
                        return Reject(RunProblem.StartConflict);
                    }

                    if (ReservationProblem(record, attempt.Task, attempt.Cause) is { } reservation)
                    {
                        return Reject(reservation);
                    }

                    if (attempt.InitialInputs != reserved.Inputs.Id || attempt.Task != reserved.Inputs.Task ||
                        attempt.Revision != reserved.Inputs.Revision)
                    {
                        return Reject(RunProblem.InputConflict);
                    }

                    if (InputProblem(record, reserved.Inputs, fresh: true) is { } input)
                    {
                        return Reject(input.Problem, input.Task);
                    }

                    if (record.Schema >= 2 && ReservedProblem(record, reserved) is { } materialInput)
                    {
                        return Reject(materialInput);
                    }

                    record = record with
                    {
                        Attempts = record.Attempts.Add(attempt.Id, attempt),
                        Inputs = record.Inputs.SetItem(reserved.Inputs.Id, reserved.Inputs)
                    };
                    break;
                case RunEvent.TurnClaimed claim:
                    if (record.Phase != RunPhase.Approved)
                    {
                        return Reject(RunProblem.RunStopped);
                    }

                    if (!record.Attempts.TryGetValue(claim.Key.Attempt, out var owner))
                    {
                        return Reject(RunProblem.UnknownAttempt);
                    }

                    if (record.Closures.ContainsKey(owner.Id) || record.Claims.ContainsKey(claim.Key))
                    {
                        return Reject(RunProblem.InvalidClaim);
                    }

                    if (claim.Inputs.Task != owner.Task || claim.Inputs.Revision != owner.Revision)
                    {
                        return Reject(RunProblem.InputConflict);
                    }

                    if (claim.Key.Turn == 1 && (claim.Inputs.Id != owner.InitialInputs || !SameInput(claim.Inputs,
                        record.Inputs[owner.InitialInputs])))
                    {
                        return Reject(RunProblem.InputConflict);
                    }

                    if (claim.Key.Turn > 1 && !record.TurnClosures.ContainsKey(new(owner.Id, claim.Key.Turn - 1)))
                    {
                        return Reject(RunProblem.InvalidClaim);
                    }

                    if (InputProblem(record, claim.Inputs, fresh: !record.Inputs.ContainsKey(claim.Inputs.Id)) is { } turnInput)
                    {
                        return Reject(turnInput.Problem, turnInput.Task);
                    }

                    if (record.Schema == 3 && claim.Key.Turn == 1)
                    {
                        var currentResults = record.CurrentResults;
                        var stale = record.StaleResults;
                        foreach (var binding in claim.Inputs.Bindings.OfType<InputBinding.Provided>().Where(binding => binding.Kind == ConnectionKind.Dependency))
                        {
                            if (stale.Contains(binding.Result) || !currentResults.TryGetValue(binding.Edge.From, out var dependency) || dependency.Id != binding.Result)
                            {
                                return Reject(RunProblem.StaleInput, binding.Edge.From);
                            }
                        }
                    }

                    if (record.Schema >= 2 && (!record.Preparations.TryGetValue(claim.Key, out var prepared) ||
                        prepared.Inputs != claim.Inputs.Id || prepared.PromptHash != claim.Prompt))
                    {
                        return Reject(RunProblem.InvalidClaim);
                    }

                    record = record with
                    {
                        Claims = record.Claims.Add(claim.Key, claim),
                        Inputs = record.Inputs.SetItem(claim.Inputs.Id, claim.Inputs)
                    };
                    break;
                case RunEvent.RootExitObserved observed:
                    if (!record.Claims.ContainsKey(observed.Launch) || record.Fenced.Contains(observed.Launch) ||
                        record.RootExits.ContainsKey(observed.Launch) || record.TurnClosures.ContainsKey(observed.Launch) ||
                        record.Closures.ContainsKey(observed.Launch.Attempt))
                    {
                        return Reject(RunProblem.InvalidClaim);
                    }
                    record = record with { RootExits = record.RootExits.Add(observed.Launch, observed) };
                    break;
                case RunEvent.TurnCaptured captured:
                    var observation = captured.Observation;
                    var observations = record.Captures.GetValueOrDefault(observation.Capture, []);
                    if (!record.RootExits.ContainsKey(observation.Launch) ||
                        record.Fenced.Contains(observation.Launch) && !(observation.Ordinal == 2 && observation.Recovery && observations.Count == 1) ||
                        record.TurnClosures.ContainsKey(observation.Launch) || record.Closures.ContainsKey(observation.Launch.Attempt) ||
                        record.Dispositions.ContainsKey(observation.Capture) || observation.Ordinal != observations.Count + 1 ||
                        observation.Ordinal is not (1 or 2) || CaptureForAnotherId(record, observation.Launch, observation.Capture))
                        return Reject(RunProblem.InvalidClaim);
                    if (observations.Any(prior => prior.Launch != observation.Launch || prior.Log != observation.Log))
                        return Reject(RunProblem.EvidenceMismatch);
                    record = record with { Captures = record.Captures.SetItem(observation.Capture, observations.Add(observation)) };
                    break;
                case RunEvent.CaptureDisposed disposed:
                    var pair = record.Captures.GetValueOrDefault(disposed.Capture, []);
                    if (!record.RootExits.ContainsKey(disposed.Launch) ||
                        record.TurnClosures.ContainsKey(disposed.Launch) || record.Closures.ContainsKey(disposed.Launch.Attempt) ||
                        record.Dispositions.ContainsKey(disposed.Capture) || CaptureForAnotherId(record, disposed.Launch, disposed.Capture) ||
                        disposed.Disposition is CaptureDisposition.Failed && pair.Count > 1 ||
                        disposed.Disposition is not CaptureDisposition.Failed && pair.Count != 2)
                        return Reject(RunProblem.InvalidClaim);
                    if (pair.Any(prior => prior.Launch != disposed.Launch) ||
                        disposed.Disposition is CaptureDisposition.Matched &&
                        !CaptureComparison.Matches(pair[0], pair[1], record.RootExits[disposed.Launch]))
                        return Reject(RunProblem.EvidenceMismatch);
                    record = record with
                    {
                        Captures = record.Captures.SetItem(disposed.Capture, pair),
                        Dispositions = record.Dispositions.Add(disposed.Capture, disposed)
                    };
                    break;
                case RunEvent.TurnClosed closed:
                    if (record.Schema == 3 && record.Fenced.Contains(closed.Key) && closed.Capture is null || !record.Claims.ContainsKey(closed.Key) || record.TurnClosures.ContainsKey(closed.Key) ||
                        record.Closures.ContainsKey(closed.Key.Attempt))
                    {
                        return Reject(RunProblem.InvalidClaim);
                    }

                    if (closed.Capture is { } capture && (!record.RootExits.ContainsKey(closed.Key) ||
                        !record.Dispositions.TryGetValue(capture, out var disposition) || disposition.Launch != closed.Key ||
                        record.Captures[capture].Any(observed => observed.Log != closed.Evidence)))
                        return Reject(RunProblem.EvidenceMismatch);
                    record = record with
                    {
                        TurnClosures = record.TurnClosures.Add(closed.Key, closed.Evidence),
                        Settlements = closed.Capture is { } settlementCapture ? record.Settlements.Add(closed.Key, settlementCapture) : record.Settlements
                    };
                    break;
                case RunEvent.AttemptClosed closed:
                    if (!record.Attempts.ContainsKey(closed.Attempt))
                    {
                        return Reject(RunProblem.UnknownAttempt);
                    }

                    if (record.Closures.ContainsKey(closed.Attempt))
                    {
                        return Reject(RunProblem.OutcomeMismatch);
                    }

                    record = record with
                    {
                        Closures = record.Closures.Add(closed.Attempt, closed.End)
                    };
                    break;
                case RunEvent.ResultAccepted accepted:
                    var result = accepted.Result;
                    if (record.Results.Any(item => item.Id == result.Id))
                    {
                        return Reject(RunProblem.StartConflict);
                    }

                    if (!record.Revisions.TryGetValue(result.Revision, out var resultRevision) ||
                        !resultRevision.Snapshot.Tasks.TryGetValue(result.Task, out var resultTask))
                    {
                        return Reject(RunProblem.IdentityMismatch);
                    }

                    if (resultTask.Blueprint.Work is not WorkSpec.Agent { Access: AgentAccess.ReadOnly } &&
                        (record.Schema == 1 || resultTask.Blueprint.Work is WorkSpec.Person && result.Origin is not ResultOrigin.Human))
                    {
                        return Reject(RunProblem.UnsupportedResult);
                    }

                    if (accepted.Inputs.Id != result.Inputs || accepted.Inputs.Task != result.Task || accepted.Inputs.Revision != result.Revision)
                    {
                        return Reject(RunProblem.InputConflict);
                    }

                    if (InputProblem(record, accepted.Inputs, fresh: false) is { } resultInput)
                    {
                        return Reject(resultInput.Problem, resultInput.Task);
                    }

                    if (record.CurrentResults.TryGetValue(result.Task, out var current))
                    {
                        if (result.Supersedes != current.Id)
                        {
                            return Reject(RunProblem.StartConflict);
                        }
                    }
                    else if (result.Supersedes is not null)
                    {
                        return Reject(RunProblem.UnknownResult);
                    }

                    switch (result.Origin)
                    {
                        case ResultOrigin.Executed executed:
                            if (!record.Attempts.TryGetValue(executed.Attempt, out var producer))
                            {
                                return Reject(RunProblem.UnknownAttempt);
                            }

                            if (producer.Task != result.Task || producer.Revision != result.Revision)
                            {
                                return Reject(RunProblem.IdentityMismatch);
                            }

                            if (record.Closures.GetValueOrDefault(producer.Id) is not AttemptEnd.Logged { Outcome: TerminalAttemptOutcome.Succeeded })
                            {
                                return Reject(RunProblem.OutcomeMismatch);
                            }

                            if (!record.Claims.Values.Any(claim => claim.Key.Attempt == producer.Id && claim.Inputs.Id == result.Inputs))
                            {
                                return Reject(RunProblem.UnknownInput);
                            }

                            break;
                        case ResultOrigin.Reused { Source: AttemptSource.Standalone source, Evidence: var evidence }:
                            if (resultTask.Blueprint.Work is not WorkSpec.Agent { Proposes: false } || source.Task != result.Task ||
                                evidence.Definition != Revision.Hash(Revision.CanonicalTask(resultTask)) || evidence.Inputs != Revision.Hash(""))
                            {
                                return Reject(RunProblem.ReuseUnverifiable);
                            }

                            if (result.Revision != record.Revision.Id || accepted.Inputs.Bindings.Length != 0 || accepted.Inputs.Text != "" ||
                                resultRevision.Snapshot.Connections.Keys.Any(edge => edge.To == result.Task) ||
                                    record.Attempts.Values.Any(item => item.Task == result.Task))
                            {
                                return Reject(RunProblem.ReuseUnverifiable);
                            }

                            break;
                        case ResultOrigin.Rebased rebased:
                            if (record.Schema != 3 || record.Plans.GetValueOrDefault(rebased.Plan) is not MaterializationPlan.Rebase rebase ||
                                rebase.Task != result.Task || rebase.Source != rebased.Source || rebase.Result != result.Id)
                            {
                                return Reject(RunProblem.UnknownResult);
                            }

                            break;
                        case ResultOrigin.Human human:
                            if (HumanResultProblem(record, accepted, resultTask, human) is { } humanProblem)
                            {
                                return Reject(humanProblem);
                            }

                            break;
                        default:
                            return Reject(RunProblem.UnsupportedResult);
                    }
                    if (record.Schema >= 2 && ResultCodeProblem(record, accepted, resultTask) is { } codeProblem)
                    {
                        return Reject(codeProblem);
                    }
                    record = record with
                    {
                        Results = record.Results.Add(result),
                        Inputs = record.Inputs.SetItem(accepted.Inputs.Id, accepted.Inputs)
                    };
                    if (result.Origin is ResultOrigin.Human approval)
                    {
                        record = Decide(record, approval.Request, new GateDecision.Approved(result.Id, entry.Operation));
                    }
                    break;
                case RunEvent.GateRequested requested:
                    if (GateRequestProblem(record, requested) is { } gateProblem)
                    {
                        return Reject(gateProblem, requested.Request.Task);
                    }

                    record = record with
                    {
                        Gates = record.Gates.Add(requested.Request.Id, new(requested.Request, null)),
                        Inputs = record.Inputs.Add(requested.Inputs.Id, requested.Inputs)
                    };
                    break;
                case RunEvent.Requested requested:
                    if (record.Requested is not { } nodes) return Reject(RunProblem.StartConflict, requested.Task);
                    if (!record.Revision.Snapshot.Tasks.ContainsKey(requested.Task)) return Reject(RunProblem.IdentityMismatch, requested.Task);
                    // A task the run already has a result of, such as one it carried, is not run again in it.
                    if (RunScope.InFlow(record).Contains(requested.Task) || record.Results.Any(result => result.Task == requested.Task))
                        return Reject(RunProblem.StartConflict, requested.Task);
                    var widened = Carry(record with { Requested = nodes.Add(requested.Task) }, requested.Carried, entry.Operation);
                    if (widened.Problem is { } widenedProblem) return Reject(widenedProblem, widened.Task);
                    if (RunScope.Missing(widened.Record, requested.Task) is { } missing) return Reject(RunProblem.MissingDependencyResult, missing);
                    record = widened.Record;
                    break;
                case RunEvent.GateSentBack sent:
                    var sentBack = SendBack(record, entry, sent);
                    if (sentBack.Problem is { } sendBackProblem)
                    {
                        return Reject(sendBackProblem);
                    }

                    record = sentBack.Record;
                    break;
                case RunEvent.OwnershipFenced fenced:
                    if (fenced.Claims.IsEmpty || fenced.Claims.Distinct().Count() != fenced.Claims.Length ||
                        fenced.Claims.Any(key => !record.UnresolvedClaims.Contains(key) || record.Fenced.Contains(key)))
                    {
                        return Reject(RunProblem.InvalidClaim);
                    }
                    record = record with { Fenced = record.Fenced.Union(fenced.Claims) };
                    break;
                case RunEvent.StopRequested:
                    if (record.Phase != RunPhase.Approved)
                    {
                        return Reject(RunProblem.RunStopped);
                    }

                    record = record with
                    {
                        Phase = RunPhase.StopRequested
                    };
                    break;
                case RunEvent.Settled settled:
                    if (record.Plans.Any(pair => pair.Value is MaterializationPlan.Publication publication &&
                        !record.Results.Any(result => result.Id == publication.Result) &&
                        !record.Blocks.Values.Any(block => block.Block.Operation == pair.Key)))
                    {
                        return Reject(RunProblem.UnfinishedPublication);
                    }
                    if (record.Plans.Any(pair => pair.Value is MaterializationPlan.Rebase rebase &&
                        !record.Results.Any(result => result.Id == rebase.Result) &&
                        !record.Blocks.Values.Any(block => block.Block.Operation == pair.Key)))
                    {
                        return Reject(RunProblem.UnfinishedPublication);
                    }
                    if (record.Attempts.Keys.Any(id => !record.Closures.ContainsKey(id)) || record.UnresolvedClaims.Length != 0)
                    {
                        return Reject(RunProblem.UnclosedAttempts);
                    }

                    // A run completes once every task it can still start has a current result. Run Workflow can start each one.
                    if (settled.Outcome == RunOutcome.Completed && RunScope.Dormant(record) is var dormant && record.Revision.Snapshot.Tasks.Keys.Any(task =>
                        !dormant.Contains(task) && (!record.CurrentResults.TryGetValue(task, out var value) || record.StaleResults.Contains(value.Id))))
                    {
                        return Reject(RunProblem.IncompleteResults);
                    }

                    record = record with
                    {
                        Phase = settled.Outcome switch
                        {
                            RunOutcome.Completed => RunPhase.Completed,
                            RunOutcome.Stopped => RunPhase.Stopped,
                            RunOutcome.Failed => RunPhase.Failed
                        }
                    };
                    break;
                case RunEvent.Abandoned:
                    record = record with
                    {
                        Phase = RunPhase.Abandoned
                    };
                    break;
                default:
                    var material = ApplyMaterialization(record, entry);
                    if (material.Problem is { } materialProblem)
                    {
                        return Reject(materialProblem);
                    }
                    record = material.Record;
                    break;
            }
        }
        return new RunRead.Loaded(record with
        {
            Sequence = entry.Sequence,
            Receipts = record.Receipts.Add(entry.Operation, entry)
        });
    }

    /// <summary>
    /// Why a report the approval includes does not fit the new run: it must be a read-only root's, from a standalone attempt
    /// of that task, of the approved definition, with empty inputs on the run's base and no code or artifacts. A planner's
    /// is <see cref="ResultOrigin.Included"/> and any other's <see cref="ResultOrigin.Reused"/>.
    /// </summary>
    private static RunProblem? IncludedProblem(RunRecord record, IncludedResult included)
    {
        var (result, inputs) = (included.Result, included.Inputs);
        var snapshot = record.Revision.Snapshot;
        if (!snapshot.Tasks.TryGetValue(result.Task, out var task) || snapshot.Connections.Keys.Any(edge => edge.To == result.Task))
            return RunProblem.ReuseUnverifiable;
        if (record.Results.Any(other => other.Id == result.Id || other.Task == result.Task) || record.Inputs.ContainsKey(inputs.Id))
            return RunProblem.StartConflict;
        if (result.Revision != record.Revision.Id || result.Inputs != inputs.Id || inputs.Task != result.Task || inputs.Revision != result.Revision ||
            !inputs.Bindings.IsEmpty || inputs.Text != "" || !inputs.Files.IsEmpty || inputs.Review is not null ||
            inputs.Code != new CodeSelection.Root(record.Base.Commit) || result.Supersedes is not null || result.Code is not null ||
            !result.Artifacts.IsEmpty)
            return RunProblem.InputConflict;
        var definition = Revision.Hash(Revision.CanonicalTask(task));
        return (task.Blueprint.Work, result.Origin) switch
        {
            (WorkSpec.Agent { Access: AgentAccess.ReadOnly, Proposes: true }, ResultOrigin.Included planner) when
                planner.Source.Task == result.Task && planner.Evidence.Definition == definition => null,
            (WorkSpec.Agent { Access: AgentAccess.ReadOnly, Proposes: false }, ResultOrigin.Reused { Source: AttemptSource.Standalone source } reused) when
                source.Task == result.Task && reused.Evidence.Definition == definition && reused.Evidence.Inputs == Revision.Hash("") => null,
            _ => RunProblem.ReuseUnverifiable,
        };
    }

    internal static RunProblem? AmendmentProblem(RunRecord record, Workflow candidate)
    {
        foreach (var task in record.Revision.Snapshot.Tasks.Values)
        {
            if (!candidate.Tasks.TryGetValue(task.Id, out var replacement) || task.Blueprint.Key != replacement.Blueprint.Key ||
                task.Blueprint.Work != replacement.Blueprint.Work ||
                !task.Blueprint.Fields.Select(field => (field.Key, field.Required)).OrderBy(field => field.Key, StringComparer.Ordinal)
                    .SequenceEqual(replacement.Blueprint.Fields.Select(field => (field.Key, field.Required)).OrderBy(field => field.Key,
                        StringComparer.Ordinal)))
            {
                return RunProblem.StartedTaskChanged;
            }

            if (record.Attempts.Values.Any(attempt => attempt.Task == task.Id) || record.Results.Any(result => result.Task == task.Id) ||
                record.Gates.Values.Any(gate => gate.Request.Task == task.Id))
            {
                if (!SameTask(record.Revision.Snapshot, candidate, task.Id))
                {
                    return RunProblem.StartedTaskChanged;
                }
            }
        }
        return null;
    }

    internal static bool SameTask(Workflow original, Workflow current, TaskId task) =>
        original.Tasks.TryGetValue(task, out var definition) && current.Tasks.TryGetValue(task, out var replacement) &&
        Revision.CanonicalTask(definition) == Revision.CanonicalTask(replacement) &&
        original.Connections.Where(edge => edge.Key.To == task).SequenceEqual(current.Connections.Where(edge => edge.Key.To == task));

    internal static RunAttempt? Slot(RunRecord record, TaskId task, AttemptCause cause) => record.Attempts.Values.FirstOrDefault(attempt => MatchesSlot(attempt, task, cause));

    internal static bool MatchesSlot(RunAttempt attempt, TaskId task, AttemptCause cause) =>
        attempt.Task == task && (cause switch
        {
            AttemptCause.Initial => attempt.Cause is AttemptCause.Initial,
            AttemptCause.ReviewFix fix => attempt.Cause is AttemptCause.ReviewFix other && other.Link.Review == fix.Link.Review &&
                other.Link.Attempt == fix.Link.Attempt && other.Link.Round == fix.Link.Round,
            AttemptCause.Retry retry => Previous(attempt.Cause) == retry.Previous,
            AttemptCause.Continue continued => Previous(attempt.Cause) == continued.Previous,
            _ => false,
        });

    internal static AttemptId? Previous(AttemptCause cause) => cause switch
    {
        AttemptCause.Retry retry => retry.Previous,
        AttemptCause.Continue continued => continued.Previous,
        _ => null,
    };

    internal static RunRejection? ReservationTaskProblem(RunRecord record, TaskId task, RevisionId revision, bool recorded = false)
    {
        if (record.Phase != RunPhase.Approved)
        {
            return new(RunProblem.RunStopped);
        }

        if (revision != record.Revision.Id && (!recorded || !record.Revisions.TryGetValue(revision, out var original) ||
            !SameTask(original.Snapshot, record.Revision.Snapshot, task)))
        {
            return new(RunProblem.RevisionConflict);
        }

        if (!record.Revisions[revision].Snapshot.Tasks.TryGetValue(task, out var definition))
        {
            return new(RunProblem.IdentityMismatch);
        }

        if (definition.Blueprint.Work is WorkSpec.Person)
        {
            return new(RunProblem.UnsupportedWork);
        }

        if (definition.Execution is not { Model: not null } ||
            definition.Blueprint.Fields.Any(field => field.Required && string.IsNullOrWhiteSpace(definition.Field(field.Key))))
        {
            return new(RunProblem.TaskUnconfigured, Task: task);
        }

        return null;
    }

    internal static RunProblem? ReservationProblem(RunRecord record, TaskId task, AttemptCause cause)
    {
        // Only a task the person ran, or one after it, starts in a run that a node's Run started.
        if (cause is AttemptCause.Initial && !RunScope.InFlow(record).Contains(task))
        {
            return RunProblem.NotRequested;
        }

        if (cause is AttemptCause.Initial && record.Results.Any(result => result.Task == task && result.Origin is ResultOrigin.Reused or ResultOrigin.Included))
        {
            return RunProblem.StartConflict;
        }

        if (Slot(record, task, cause) is not null)
        {
            return Previous(cause) is null ? RunProblem.StartConflict : RunProblem.ReplacementConflict;
        }

        if (Previous(cause) is { } previous)
        {
            if (!record.Attempts.TryGetValue(previous, out var owner) || owner.Task != task)
            {
                return RunProblem.UnknownAttempt;
            }

            if (!record.Closures.TryGetValue(previous, out var end))
            {
                return RunProblem.UnclosedAttempts;
            }

            if (cause is AttemptCause.Continue continued && end is AttemptEnd.Recovered &&
                (end is not AttemptEnd.Recovered { Outcome: RecoveryOutcome.Stopped } ||
                 !record.Baselines.ContainsKey((previous, continued.Confirmation))))
            {
                return RunProblem.OutcomeMismatch;
            }
        }
        if (cause is AttemptCause.ReviewFix fix && (!record.Attempts.TryGetValue(fix.Link.Attempt, out var review) ||
            review.Task != fix.Link.Review || record.Revisions[review.Revision].Snapshot.SubjectOf(review.Task) != task ||
            !record.TurnClosures.ContainsKey(new(review.Id, fix.Link.Round)) || record.Closures.ContainsKey(review.Id)))
        {
            return RunProblem.InvalidClaim;
        }

        return null;
    }

    internal static bool Same<T>(T left, T right) => RunJournal.Canonical(left) == RunJournal.Canonical(right);

    internal static bool SameInput(InputRecord left, InputRecord right) => Same(left, right);

    internal static RunRejection? InputProblem(RunRecord record, InputRecord input, bool fresh)
    {
        if (record.Inputs.TryGetValue(input.Id, out var stored))
        {
            if (!SameInput(stored, input))
            {
                return new(RunProblem.InputConflict);
            }

            if (!fresh)
            {
                return null;
            }
        }
        if (!record.Revisions.TryGetValue(input.Revision, out var revision) || !revision.Snapshot.Tasks.ContainsKey(input.Task))
        {
            return new(RunProblem.RevisionConflict);
        }

        var incoming = revision.Snapshot.Connections.Where(edge => edge.Key.To == input.Task).ToArray();
        if (input.Bindings.Length != incoming.Length)
        {
            return new(RunProblem.InputConflict);
        }

        for (var index = 0; index < incoming.Length; index++)
        {
            var edge = incoming[index];
            switch (input.Bindings[index])
            {
                case InputBinding.MissingContext missing when missing.Edge == edge.Key && edge.Value == ConnectionKind.Context:
                    if (fresh && record.CurrentResults.TryGetValue(edge.Key.From, out var context) &&
                        !record.StaleResults.Contains(context.Id))
                    {
                        return new(RunProblem.InputConflict);
                    }

                    break;
                case InputBinding.Provided provided when provided.Edge == edge.Key && provided.Kind == edge.Value:
                    var result = record.Results.FirstOrDefault(result => result.Id == provided.Result);
                    if (result is null || result.Task != edge.Key.From)
                    {
                        return new(RunProblem.UnknownResult);
                    }

                    if (fresh && (record.StaleResults.Contains(result.Id) || record.CurrentResults[result.Task].Id != result.Id))
                    {
                        return new(RunProblem.StaleInput, Task: result.Task);
                    }

                    break;
                default:
                    return new(edge.Value == ConnectionKind.Dependency ? RunProblem.MissingDependencyResult : RunProblem.InputConflict,
                        Task: edge.Key.From);
            }
        }
        return null;
    }
}

internal static partial class RunValidation
{
    public static RunProblem? Entry(RunEntry entry)
    {
        if (entry.Operation.Value == Guid.Empty || !Revision.IsHash(entry.Command.Sha256))
        {
            return RunProblem.InvalidData;
        }

        if (SchemaProblem(entry) is { } schema)
        {
            return schema;
        }
        return entry.Event switch
        {
            RunEvent.Approved approved => approved.Run.Value == Guid.Empty || !Base(approved.Base) || approved.Node?.Value == Guid.Empty ||
                approved.Included is { } included && !Results(included) || approved.Carried is { } carried && !Results(carried)
                ? RunProblem.InvalidData : Snapshot(approved.Revision),
            RunEvent.Amended amended => !Revision.IsHash(amended.Previous.Sha256) || amended.Confirmation.Value == Guid.Empty ||
                amended.Origin is AmendmentOrigin.Planner planner && (planner.Attempt.Value == Guid.Empty || planner.Turn < 1)
                ? RunProblem.ConfirmationRequired : Snapshot(amended.Revision),
            RunEvent.Reserved reserved => !Attempt(reserved.Attempt) || !Input(reserved.Inputs) ? RunProblem.InvalidData : null,
            RunEvent.TurnClaimed claim => !Key(claim.Key) || !Input(claim.Inputs) || !Revision.IsHash(claim.Prompt.Sha256) ?
                RunProblem.InvalidData : null,
            RunEvent.OwnershipFenced fenced => fenced.Claims.IsDefault || !fenced.Claims.All(Key) ? RunProblem.InvalidData : null,
            RunEvent.RootExitObserved observed => !Key(observed.Launch) || !Revision.IsCommit(observed.Tip.Hex) ||
                observed.Head is not null && string.IsNullOrWhiteSpace(observed.Head) || !Enum.IsDefined(observed.Ownership) ||
                observed.Exit is not RootExit.Exited && observed.Exit is not RootExit.NotStarted ||
                observed.Exit is RootExit.NotStarted notStarted && string.IsNullOrWhiteSpace(notStarted.Detail)
                ? RunProblem.InvalidData : null,
            RunEvent.TurnCaptured captured => !Capture(captured.Observation) ? RunProblem.InvalidData : null,
            RunEvent.CaptureDisposed disposed => disposed.Capture.Value == Guid.Empty || !Key(disposed.Launch) ||
                !Disposition(disposed.Disposition) ? RunProblem.InvalidData : null,
            RunEvent.TurnClosed closed => closed.Capture?.Value == Guid.Empty || !Key(closed.Key) || !Checkpoint(closed.Evidence) ? RunProblem.InvalidData : null,
            RunEvent.AttemptClosed closed => closed.Attempt.Value == Guid.Empty ? RunProblem.InvalidData : End(closed.End),
            RunEvent.ResultAccepted accepted => !Result(accepted.Result) || !Input(accepted.Inputs) ? RunProblem.InvalidData : null,
            RunEvent.GateRequested { Request: var request } requested => request.Id.Value == Guid.Empty || request.Task.Value == Guid.Empty ||
                request.Inputs.Value == Guid.Empty || request.Result.Value == Guid.Empty || request.Sequence < 1 ||
                !Revision.IsHash(request.Revision.Sha256) || !Input(requested.Inputs) ? RunProblem.InvalidData : null,
            RunEvent.GateSentBack sent => sent.Request.Value == Guid.Empty || sent.Inputs.Value == Guid.Empty ? RunProblem.InvalidData :
                string.IsNullOrWhiteSpace(sent.Reason) ? RunProblem.ConfirmationRequired : null,
            RunEvent.Abandoned abandoned => abandoned.Confirmation.Value == Guid.Empty || string.IsNullOrWhiteSpace(abandoned.Reason) ?
                RunProblem.ConfirmationRequired : null,
            RunEvent.Settled settled => !Enum.IsDefined(settled.Outcome) ? RunProblem.InvalidData : null,
            RunEvent.StopRequested => null,
            RunEvent.Requested requested => requested.Task.Value == Guid.Empty || requested.Carried is { } carried && !Results(carried)
                ? RunProblem.InvalidData : null,
            _ => Materialization(entry.Event),
        };
    }

    private static bool Results(ImmutableArray<IncludedResult> results) =>
        !results.IsDefaultOrEmpty && results.All(item => Result(item.Result) && Input(item.Inputs));

    private static RunProblem? Snapshot(ApprovedRevision revision)
    {
        var workflow = revision.Snapshot;
        if (!Revision.IsHash(revision.Id.Sha256) || workflow.Id.Value == Guid.Empty || workflow.Tasks.Values.Any(task =>
            task.Id.Value == Guid.Empty || !Enum.IsDefined(task.Conversation) || task.Execution is { } settings && !Enum.IsDefined(settings.Client)))
        {
            return RunProblem.InvalidData;
        }

        return Revision.Check(revision)?.Problem;
    }

    private static bool Base(RunBase value) => Revision.IsCommit(value.Commit.Hex) && Enum.IsDefined(value.Choice);

    internal static bool Checkpoint(LogCheckpoint checkpoint) => checkpoint.ByteLength > 0 && Revision.IsHash(checkpoint.Content.Sha256);

    private static bool Key(LaunchKey key) => key.Attempt.Value != Guid.Empty && key.Turn > 0;

    private static bool Input(InputRecord input) => input.Id.Value != Guid.Empty && input.Task.Value != Guid.Empty &&
        Revision.IsHash(input.Revision.Sha256) &&
        Code(input.Code) && input.Text is not null && !input.Files.IsDefault && input.Files.All(file =>
            file.Source.Value != Guid.Empty && Path(file.RelativePath) && Revision.IsHash(file.Content.Sha256) && file.ByteLength >= 0) &&
        Review(input.Review) && Bindings(input.Bindings);

    private static bool Bindings(ImmutableArray<InputBinding> bindings) => !bindings.IsDefault && bindings.All(binding => binding switch
    {
        InputBinding.Provided provided => provided.Edge.From.Value != Guid.Empty && provided.Edge.To.Value != Guid.Empty &&
            Enum.IsDefined(provided.Kind) && provided.Result.Value != Guid.Empty,
        InputBinding.MissingContext missing => missing.Edge.From.Value != Guid.Empty && missing.Edge.To.Value != Guid.Empty,
        _ => false,
    });

    private static bool Attempt(RunAttempt attempt) => attempt.Id.Value != Guid.Empty && attempt.Task.Value != Guid.Empty &&
        Revision.IsHash(attempt.Revision.Sha256) && attempt.InitialInputs.Value != Guid.Empty && (attempt.Cause switch
        {
            AttemptCause.Initial => true,
            AttemptCause.Retry retry => retry.Previous.Value != Guid.Empty && retry.Confirmation.Value != Guid.Empty,
            AttemptCause.Continue continued => continued.Previous.Value != Guid.Empty && continued.Confirmation.Value != Guid.Empty,
            AttemptCause.ReviewFix fix => fix.Link.Attempt.Value != Guid.Empty && fix.Link.Review.Value != Guid.Empty &&
                fix.Link.Round > 0 && fix.Link.Guidance >= 0,
            _ => false,
        });

    private static RunProblem? End(AttemptEnd end) => end switch
    {
        AttemptEnd.Logged logged => Enum.IsDefined(logged.Outcome) && Checkpoint(logged.Evidence) ? null : RunProblem.InvalidData,
        AttemptEnd.Recovered recovered => !Enum.IsDefined(recovered.Outcome) ? RunProblem.InvalidData :
            recovered.Confirmation.Value == Guid.Empty || string.IsNullOrWhiteSpace(recovered.Reason) ? RunProblem.ConfirmationRequired : null,
        _ => RunProblem.InvalidData,
    };

    private static bool Result(ResultRecord result) => result.Id.Value != Guid.Empty && result.Task.Value != Guid.Empty &&
        Revision.IsHash(result.Revision.Sha256) && result.Inputs.Value != Guid.Empty && result.Report is not null &&
        result.Supersedes?.Value != Guid.Empty && !result.Artifacts.IsDefault && result.Artifacts.All(Artifact) &&
        (result.Code is null || result.Code is CodeOutput.Forwarded forwarded && forwarded.Inputs.Value != Guid.Empty ||
            result.Code is CodeOutput.Produced produced && Owned(produced.Code)) && (result.Origin switch
        {
            ResultOrigin.Executed executed => executed.Attempt.Value != Guid.Empty,
            ResultOrigin.Rebased rebased => rebased.Source.Value != Guid.Empty && rebased.Plan.Value != Guid.Empty &&
                rebased.Source == result.Supersedes,
            ResultOrigin.Human human => human.Request.Value != Guid.Empty,
            ResultOrigin.Carried carried => carried.Run.Value != Guid.Empty && carried.Result.Value != Guid.Empty,
            ResultOrigin.Reused reused => Checkpoint(reused.Evidence.SourceLog) && Revision.IsHash(reused.Evidence.Definition.Sha256) &&
                Revision.IsHash(reused.Evidence.Inputs.Sha256) && Revision.IsHash(reused.Evidence.CodeTree.Sha256) &&
                reused.Evidence.Confirmation.Value != Guid.Empty &&
                (reused.Source switch
                {
                    AttemptSource.Standalone source => source.Task.Value != Guid.Empty && source.Attempt.Value != Guid.Empty,
                    AttemptSource.Owned source => source.Workflow.Value != Guid.Empty && source.Run.Value != Guid.Empty &&
                        source.Task.Value != Guid.Empty && source.Attempt.Value != Guid.Empty,
                    _ => false,
                }),
            ResultOrigin.Included included => Checkpoint(included.Evidence.SourceLog) && included.Evidence.Turn > 0 &&
                Revision.IsHash(included.Evidence.Definition.Sha256) && Revision.IsHash(included.Evidence.Selection.Sha256) &&
                Revision.IsHash(included.Evidence.CodeTree.Sha256) && included.Evidence.Confirmation.Value != Guid.Empty &&
                included.Source.Task.Value != Guid.Empty && included.Source.Attempt.Value != Guid.Empty,
            _ => false,
        });
}
