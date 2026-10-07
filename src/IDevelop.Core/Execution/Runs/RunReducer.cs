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
                    if (ReservationTaskProblem(record, attempt.Task, attempt.Revision) is { } taskProblem)
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
                case RunEvent.TurnClosed closed:
                    if (!record.Claims.ContainsKey(closed.Key) || record.TurnClosures.ContainsKey(closed.Key) ||
                        record.Closures.ContainsKey(closed.Key.Attempt))
                    {
                        return Reject(RunProblem.InvalidClaim);
                    }

                    record = record with
                    {
                        TurnClosures = record.TurnClosures.Add(closed.Key, closed.Evidence)
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
                        (record.Schema == 1 || resultTask.Blueprint.Work is WorkSpec.Person))
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
                    if (record.Attempts.Keys.Any(id => !record.Closures.ContainsKey(id)) || record.UnresolvedClaims.Length != 0)
                    {
                        return Reject(RunProblem.UnclosedAttempts);
                    }

                    if (settled.Outcome == RunOutcome.Completed && record.Revision.Snapshot.Tasks.Keys.Any(task =>
                        !record.CurrentResults.TryGetValue(task, out var value) || record.StaleResults.Contains(value.Id)))
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

            if (record.Attempts.Values.Any(attempt => attempt.Task == task.Id) || record.Results.Any(result => result.Task == task.Id))
            {
                if (Revision.CanonicalTask(task) != Revision.CanonicalTask(replacement) ||
                    !record.Revision.Snapshot.Connections.Where(edge =>
                        edge.Key.To == task.Id).SequenceEqual(candidate.Connections.Where(edge => edge.Key.To == task.Id)))
                {
                    return RunProblem.StartedTaskChanged;
                }
            }
        }
        return null;
    }

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

    internal static RunRejection? ReservationTaskProblem(RunRecord record, TaskId task, RevisionId revision)
    {
        if (record.Phase != RunPhase.Approved)
        {
            return new(RunProblem.RunStopped);
        }

        if (revision != record.Revision.Id)
        {
            return new(RunProblem.RevisionConflict);
        }

        if (!record.Revision.Snapshot.Tasks.TryGetValue(task, out var definition))
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
        if (cause is AttemptCause.Initial && record.Results.Any(result => result.Task == task && result.Origin is ResultOrigin.Reused))
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

            if (cause is AttemptCause.Continue && end is AttemptEnd.Recovered)
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
            RunEvent.Approved approved => approved.Run.Value == Guid.Empty || !Base(approved.Base) ? RunProblem.InvalidData :
                Snapshot(approved.Revision),
            RunEvent.Amended amended => !Revision.IsHash(amended.Previous.Sha256) || amended.Confirmation.Value == Guid.Empty ||
                amended.Origin is AmendmentOrigin.Planner planner && (planner.Attempt.Value == Guid.Empty || planner.Turn < 1)
                ? RunProblem.ConfirmationRequired : Snapshot(amended.Revision),
            RunEvent.Reserved reserved => !Attempt(reserved.Attempt) || !Input(reserved.Inputs) ? RunProblem.InvalidData : null,
            RunEvent.TurnClaimed claim => !Key(claim.Key) || !Input(claim.Inputs) || !Revision.IsHash(claim.Prompt.Sha256) ?
                RunProblem.InvalidData : null,
            RunEvent.OwnershipFenced fenced => fenced.Claims.IsDefault || !fenced.Claims.All(Key) ? RunProblem.InvalidData : null,
            RunEvent.TurnClosed closed => !Key(closed.Key) || !Checkpoint(closed.Evidence) ? RunProblem.InvalidData : null,
            RunEvent.AttemptClosed closed => closed.Attempt.Value == Guid.Empty ? RunProblem.InvalidData : End(closed.End),
            RunEvent.ResultAccepted accepted => !Result(accepted.Result) || !Input(accepted.Inputs) ? RunProblem.InvalidData : null,
            RunEvent.Abandoned abandoned => abandoned.Confirmation.Value == Guid.Empty || string.IsNullOrWhiteSpace(abandoned.Reason) ?
                RunProblem.ConfirmationRequired : null,
            RunEvent.Settled settled => !Enum.IsDefined(settled.Outcome) ? RunProblem.InvalidData : null,
            RunEvent.StopRequested => null,
            _ => Materialization(entry.Event),
        };
    }

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
            _ => false,
        });
}
