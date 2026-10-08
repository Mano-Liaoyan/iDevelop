using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using IDevelop.Nodes;
using IDevelop.Projects;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>The project-scoped run journals and their locked commands.</summary>
internal sealed class RunStore
{
    private readonly string _project;

    private readonly string _runs;

    private readonly TimeProvider _clock;

    private readonly Func<Guid> _ids;

    private RunStore(string project, TimeProvider clock, Func<Guid> ids)
    {
        _project = Path.GetFullPath(project);
        _runs = DataFolder.Runs(_project);
        _clock = clock;
        _ids = ids;
    }

    public static RunStore Open(string projectFolder) => new(projectFolder, TimeProvider.System, Guid.CreateVersion7);

    internal static RunStore Open(string projectFolder, TimeProvider clock, Func<Guid> ids) => new(projectFolder, clock, ids);

    public string AttemptFolder(WorkflowId workflow, RunId run, TaskId task, AttemptId attempt) =>
        AttemptLog.FolderOf(Path.Combine(Folder(workflow, run), "attempts"), task, attempt);

    public RunRead Read(WorkflowId workflow, RunId run)
    {
        try
        {
            return ReadJournal(workflow, run);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return new RunRead.Rejected(new(RunProblem.StorageUnavailable));
        }
    }

    public TaskRunOwnership TaskOwnership(StandaloneLease held)
    {
        var task = held.Task;
        string? owner = null;
        try
        {
            if (!Directory.Exists(_runs)) return new TaskRunOwnership.Free();
            foreach (var workflowFolder in Directory.EnumerateDirectories(_runs).Order(StringComparer.Ordinal))
            foreach (var runFolder in Directory.EnumerateDirectories(workflowFolder).Order(StringComparer.Ordinal))
            foreach (var journal in Directory.EnumerateFiles(runFolder, "events.jsonl"))
            {
                if (new FileInfo(journal).Length == 0) continue;
                var relative = Path.GetRelativePath(_project, journal);
                if (!Guid.TryParse(Path.GetFileName(workflowFolder), out var workflow) ||
                    !Guid.TryParse(Path.GetFileName(runFolder), out var run))
                    return new TaskRunOwnership.Unreadable($"{relative}: invalid run identity.");
                var read = ReadJournal(new(workflow), new(run), journal);
                if (read is RunRead.Rejected { Reason.Problem: RunProblem.IncompleteTail, Prefix: null }) continue;
                var record = read switch
                {
                    RunRead.Loaded loaded => loaded.Record,
                    RunRead.Rejected { Reason.Problem: RunProblem.IncompleteTail, Prefix: { } prefix } => prefix,
                    _ => null,
                };
                if (record is null)
                    return new TaskRunOwnership.Unreadable($"{relative}: {((RunRead.Rejected)read).Reason.Problem}.");
                if (owner is null &&
                    ((record.Phase is RunPhase.Approved or RunPhase.StopRequested && record.Revision.Snapshot.Tasks.ContainsKey(task)) ||
                     (record.Phase != RunPhase.Abandoned &&
                      record.Attempts.Values.Any(attempt => attempt.Task == task && !record.Closures.ContainsKey(attempt.Id))) ||
                     record.UnresolvedClaims.Any(claim => record.Attempts[claim.Attempt].Task == task)))
                    owner = record.Revision.Snapshot.DisplayName;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new TaskRunOwnership.Unreadable(error.Message);
        }
        return owner is { } name ? new TaskRunOwnership.Owned(name) : new TaskRunOwnership.Free();
    }

    internal string Project => _project;

    public ControlTake TakeControl(WorkflowId workflow, RunId run) => CoordinatorPermit.Acquire(this, workflow, run);

    internal ControlTake Fence(CoordinatorPermit permit)
    {
        ImmutableArray<LaunchKey> fenced = [];
        var decision = Transact(permit, new OperationId(_ids()),
            Fingerprint("takeControl", new { permit.Workflow, permit.Run }), (record, _) =>
            {
                if (record is null) return Missing();
                fenced = record.UnresolvedClaims;
                ImmutableArray<LaunchKey> claims = [.. fenced.Where(key => !record.Fenced.Contains(key))];
                return claims.IsEmpty
                    ? new Mutation.Existing(record.Receipts.Values.Single(entry => entry.Sequence == record.Sequence).Event)
                    : new Mutation.Append(new RunEvent.OwnershipFenced(claims));
            });
        return decision is RunDecision.Rejected refusal
            ? new ControlTake.Rejected(refusal.Reason)
            : new ControlTake.Owned(permit, fenced);
    }

    public RunDecision Approve(WorkflowId workflow, RunId run, OperationId operation, ApprovedRevision revision, RunBase codeBase) =>
        Transact(workflow, run, operation, Fingerprint("approve", new
        {
            revision = Revision.Canonical(revision.Snapshot),
            revision.Id,
            codeBase
        }), (record, all) =>
        {
            if (record is not null)
            {
                return new Mutation.Rejected(new(RunProblem.StartConflict));
            }

            if (all.Any(other => other.Workflow == workflow && other.Phase is RunPhase.Approved or RunPhase.StopRequested))
            {
                return new Mutation.Rejected(new(RunProblem.RunBusy));
            }

            return new Mutation.Append(new RunEvent.Approved(run, revision, codeBase));
        });

    public RunDecision Plan(RunLease lease, OperationId operation, RevisionId revision, AttemptCause cause) =>
        Transact(lease.Permit, operation, Fingerprint("plan", new { task = lease.Task, revision, cause }), (record, _) =>
        {
            var task = lease.Task;
            if (record is null)
            {
                return Missing();
            }
            if (record.Phase != RunPhase.Approved)
            {
                return Refuse(RunProblem.RunStopped);
            }
            if (cause is AttemptCause.Retry { Confirmation.Value: var retry } && retry == Guid.Empty ||
                cause is AttemptCause.Continue { Confirmation.Value: var continued } && continued == Guid.Empty)
            {
                return Refuse(RunProblem.ConfirmationRequired);
            }
            if (RunReducer.Slot(record, task, cause) is { } existing)
            {
                var sameCause = existing.Cause.GetType() == cause.GetType();
                return existing.Revision == revision && sameCause
                    ? new Mutation.Existing(new RunEvent.Planned(record.Plans.Values.OfType<MaterializationPlan.Preparation>()
                        .Single(plan => plan.Attempt == existing.Id)))
                    : Refuse(RunReducer.Previous(cause) is null ? RunProblem.StartConflict : RunProblem.ReplacementConflict);
            }
            if (RunReducer.ReservationTaskProblem(record, task, revision) is { } taskProblem)
            {
                return new Mutation.Rejected(taskProblem);
            }
            if (RunReducer.ReservationProblem(record, task, cause) is { } reservation)
            {
                return Refuse(reservation);
            }
            var capture = Inputs(record, task, revision, default);
            if (capture.Rejection is { } rejection)
            {
                return new Mutation.Rejected(rejection);
            }
            var bindings = capture.Inputs!.Bindings;
            var sources = InputMaterial.Sources(record, bindings);
            var review = InputMaterial.Review(record.Revisions[revision].Snapshot, task, bindings);
            var candidate = new MaterializationPlan.Preparation(default, default, task, revision, cause, bindings, sources, review);
            if (RunReducer.PlanProblem(record, candidate) is { } problem)
            {
                return Refuse(problem);
            }
            var original = record.Plans.Where(pair => pair.Value is MaterializationPlan.Preparation)
                .OrderByDescending(pair => record.Receipts[pair.Key].Sequence).Select(pair => (MaterializationPlan.Preparation)pair.Value)
                .FirstOrDefault(plan => RunReducer.MatchesSlot(new(plan.Attempt, plan.Task, plan.Revision, plan.Inputs, plan.Cause), task, cause) &&
                    plan.Revision == revision && plan.Cause.GetType() == cause.GetType() && RunReducer.Same(plan.Bindings, bindings) &&
                    RunReducer.Same(plan.Sources, sources) && RunReducer.Same(plan.Review, review));
            if (original is not null)
            {
                return new Mutation.Existing(new RunEvent.Planned(original));
            }
            var inputs = new InputId(_ids());
            return new Mutation.Append(new RunEvent.Planned(candidate with { Inputs = inputs, Attempt = new(_ids()) }));
        }, validate: record => LeaseProblem(record, lease));

    public RunDecision Refresh(RunLease lease, OperationId operation, LaunchKey launch, JoinRecord? join = null) =>
        Transact(lease.Permit, operation, Fingerprint("refresh", new { launch, join }), (record, _) =>
        {
            if (record is null) return Missing();
            if (record.Phase != RunPhase.Approved) return Refuse(RunProblem.RunStopped);
            if (!record.Attempts.TryGetValue(launch.Attempt, out var attempt)) return Refuse(RunProblem.UnknownAttempt);
            var existing = record.Plans.Values.OfType<MaterializationPlan.Refresh>().SingleOrDefault(p => p.Launch == launch);
            if (existing is not null) return new Mutation.Existing(new RunEvent.Planned(existing));
            var capture = Inputs(record, attempt.Task, attempt.Revision, default);
            if (capture.Rejection is { } rejection) return new Mutation.Rejected(rejection);
            var bindings = capture.Inputs!.Bindings;
            return new Mutation.Append(new RunEvent.Planned(new MaterializationPlan.Refresh(launch,
                join is not null && record.Plans.GetValueOrDefault(join.Operation) is MaterializationPlan.Join joined ? joined.Inputs : new(_ids()), bindings,
                InputMaterial.Sources(record, bindings), InputMaterial.Review(record.Revisions[attempt.Revision].Snapshot, attempt.Task, bindings), join)));
        }, validate: record => AttemptLeaseProblem(record, lease, launch.Attempt));

    public RunDecision Reserve(RunLease lease, OperationId operation, OperationId plan, JoinRecord? join = null) =>
        Transact(lease.Permit, operation, Fingerprint("reserve", new { plan, join }), (record, _) =>
        {
            if (record is null)
            {
                return Missing();
            }
            if (record.Phase != RunPhase.Approved)
            {
                return Refuse(RunProblem.RunStopped);
            }
            if (record.Plans.GetValueOrDefault(plan) is not MaterializationPlan.Preparation preparation)
            {
                return Refuse(RunProblem.InvalidData);
            }
            InputRecord inputs;
            try
            {
                inputs = InputMaterial.Build(record, preparation, join);
            }
            catch (ArgumentException)
            {
                return Refuse(RunProblem.InputConflict);
            }
            if (record.Attempts.TryGetValue(preparation.Attempt, out var existing))
            {
                var reserved = Reservation(record, existing.Id);
                return RunReducer.SameInput(reserved.Inputs, inputs) ? new Mutation.Existing(reserved) : Refuse(RunProblem.InputConflict);
            }
            if (RunReducer.ReservationTaskProblem(record, preparation.Task, preparation.Revision, recorded: true) is { } taskProblem)
            {
                return new Mutation.Rejected(taskProblem);
            }
            if (RunReducer.InputProblem(record, inputs, true) is { } inputProblem)
            {
                return new Mutation.Rejected(inputProblem);
            }
            if (RunReducer.PlanProblem(record, preparation, recorded: true) is { } planProblem)
            {
                return Refuse(planProblem);
            }
            return new Mutation.Append(new RunEvent.Reserved(new(preparation.Attempt, preparation.Task, preparation.Revision,
                preparation.Inputs, preparation.Cause), inputs));
        }, validate: record => LeaseProblem(record, lease) ??
            (record.Plans.GetValueOrDefault(plan) is MaterializationPlan.Preparation preparation && preparation.Task != lease.Task
                ? RunProblem.IdentityMismatch : null));

    public RunDecision Record(CoordinatorPermit permit, OperationId operation, RunEvent e) =>
        Transact(permit, operation, Fingerprint("record", e), (record, _) =>
        {
            if (record is null)
            {
                return Missing();
            }
            if (e is not (RunEvent.LayoutAllocated or RunEvent.Planned { Plan: MaterializationPlan.Publication or MaterializationPlan.Join or MaterializationPlan.Salvage or
                MaterializationPlan.RetryReset or MaterializationPlan.Refresh } or RunEvent.GitIntended or RunEvent.GitObserved or RunEvent.Prepared or RunEvent.Blocked or
                RunEvent.SalvageRetained or RunEvent.BlockResolved or RunEvent.RootExitObserved or RunEvent.OwnershipFenced or RunEvent.TurnCaptured or RunEvent.CaptureDisposed))
            {
                return Refuse(RunProblem.InvalidData);
            }
            if (!RunReducer.Permitted(record, e))
            {
                return Refuse(RunProblem.RunStopped);
            }
            if (e is RunEvent.Prepared { Execution: { Launch.Turn: 1 } prepared } &&
                record.Inputs.TryGetValue(prepared.Inputs, out var inputs) && RunReducer.InputProblem(record, inputs, true) is { } stale)
            {
                return new Mutation.Rejected(stale);
            }
            return new Mutation.Append(e);
        });

    public RunDecision AcceptPublication(CoordinatorPermit permit, OperationId operation, OperationId publication) =>
        Transact(permit, operation, Fingerprint("acceptPublication", new { publication }), (record, _) =>
        {
            if (record is null)
            {
                return Missing();
            }
            if (record.Plans.GetValueOrDefault(publication) is not MaterializationPlan.Publication plan)
            {
                return Refuse(RunProblem.InvalidData);
            }
            if (record.Results.FirstOrDefault(result => result.Id == plan.Result) is { } existing)
            {
                return new Mutation.Existing(new RunEvent.ResultAccepted(existing, record.Inputs[existing.Inputs]));
            }
            var attempt = record.Attempts[plan.Attempt];
            var closure = (AttemptEnd.Logged)record.Closures[attempt.Id];
            var read = OwnedEvidence(record, attempt, closure.Evidence);
            if (read.Rejection is { } rejection)
            {
                return new Mutation.Rejected(rejection);
            }
            if (!AttemptEvidence.Matches(read, TerminalAttemptOutcome.Succeeded) || read.Record!.Result != plan.Report ||
                !record.Claims.ContainsKey(new(attempt.Id, read.Record.Turns.Count)))
            {
                return Refuse(RunProblem.OutcomeMismatch);
            }
            var result = RunReducer.PublicationResult(record, plan);
            return new Mutation.Append(new RunEvent.ResultAccepted(result, record.Inputs[result.Inputs]));
        });

    public RunDecision Claim(RunLease lease, OperationId operation, LaunchKey key, InputRecord inputs, Digest prompt)
    {
        var workflow = lease.Permit.Workflow;
        var run = lease.Permit.Run;
        return Transact(lease.Permit, operation, Fingerprint("claim", new
        {
            key,
            inputs,
            prompt
        }), (record, all) =>
        {
            if (record is null)
            {
                return Missing();
            }

            if (record.Claims.TryGetValue(key, out var existing))
            {
                return RunReducer.SameInput(existing.Inputs, inputs) && existing.Prompt == prompt
                    ? new Mutation.Existing(existing) : Refuse(RunProblem.InputConflict);
            }
            if (record.Phase != RunPhase.Approved)
            {
                return Refuse(RunProblem.RunStopped);
            }

            if (!record.Attempts.TryGetValue(key.Attempt, out var attempt))
            {
                return Refuse(RunProblem.UnknownAttempt);
            }

            if (all.Any(other => other.UnresolvedClaims.Any(claim => other.Attempts[claim.Attempt].Task == attempt.Task &&
                (other.Id != run || other.Workflow != workflow || claim.Attempt != key.Attempt))))
            {
                return Refuse(RunProblem.UnresolvedOwnership);
            }

            return new Mutation.Append(new RunEvent.TurnClaimed(key, inputs, prompt), Grant: true);
        }, validate: record => AttemptLeaseProblem(record, lease, key.Attempt));
    }

    public RunDecision CloseTurn(CoordinatorPermit permit, OperationId operation, LaunchKey key, LogCheckpoint evidence, CaptureId? capture = null) =>
        Transact(permit, operation, Fingerprint("closeTurn", new
        {
            key,
            capture,
            evidence
        }), (record, _) =>
        {
            if (record is null)
            {
                return Missing();
            }

            if (record.Fenced.Contains(key)) return Refuse(RunProblem.UnresolvedOwnership);

            if (record.TurnClosures.TryGetValue(key, out var existing))
            {
                return RunReducer.Same(existing, evidence) && (record.Settlements.TryGetValue(key, out var settled) ? settled : (CaptureId?)null) == capture
                            ? new Mutation.Existing(new RunEvent.TurnClosed(key, existing) { Capture = capture }) : Refuse(RunProblem.EvidenceMismatch);
            }

            if (TurnEvidenceProblem(record, key, evidence) is { } rejection)
                return new Mutation.Rejected(rejection);

            return new Mutation.Append(new RunEvent.TurnClosed(key, evidence) { Capture = capture });
        });

    internal RunRejection? TurnEvidenceProblem(RunRecord record, LaunchKey key, LogCheckpoint evidence)
    {
        if (!record.Attempts.TryGetValue(key.Attempt, out var attempt)) return new(RunProblem.UnknownAttempt);
        var read = OwnedEvidence(record, attempt, evidence);
        if (read.Rejection is { } rejection) return rejection;
        if (read.Events.Any(e => e is AttemptEvent.Reconciled)) return new(RunProblem.RecoveryEvidenceInsufficient);
        return key.Turn < 1 || read.Record!.Turns.Count < key.Turn || read.Record.Turns[key.Turn - 1].Outcome == TurnOutcome.Running
            ? new(RunProblem.OutcomeMismatch) : null;
    }

    public RunDecision CloseAttempt(CoordinatorPermit permit, OperationId operation, AttemptId attempt,
        TerminalAttemptOutcome outcome, LogCheckpoint evidence) =>
        Transact(permit, operation, Fingerprint("closeAttempt", new
        {
            attempt,
            outcome,
            evidence
        }), (record, _) =>
        {
            if (record is null)
            {
                return Missing();
            }

            if (record.UnresolvedClaims.Any(key => key.Attempt == attempt && record.Fenced.Contains(key)))
                return Refuse(RunProblem.UnresolvedOwnership);

            var end = new AttemptEnd.Logged(outcome, evidence);
            if (record.Closures.TryGetValue(attempt, out var existing))
            {
                return RunReducer.Same<AttemptEnd>(existing, end)
                ? new Mutation.Existing(new RunEvent.AttemptClosed(attempt, end)) : Refuse(RunProblem.OutcomeMismatch);
            }

            if (!record.Attempts.TryGetValue(attempt, out var owner))
            {
                return Refuse(RunProblem.UnknownAttempt);
            }

            var read = OwnedEvidence(record, owner, evidence);
            if (read.Rejection is { } rejection)
            {
                return new Mutation.Rejected(rejection);
            }

            if (!AttemptEvidence.Matches(read, outcome))
            {
                return Refuse(RunProblem.OutcomeMismatch);
            }

            return new Mutation.Append(new RunEvent.AttemptClosed(attempt, end));
        });

    public RecoveryRead InspectRecovery(WorkflowId workflow, RunId run)
    {
        var read = Read(workflow, run);
        if (read is RunRead.Rejected rejected)
        {
            return new RecoveryRead.Rejected(rejected.Reason);
        }

        var record = ((RunRead.Loaded)read).Record;
        return new RecoveryRead.Loaded([.. record.Attempts.Values.OrderBy(attempt => attempt.Id.Value).Select(attempt =>
        {
            var claims = record.UnresolvedClaims.Where(key => key.Attempt == attempt.Id).ToImmutableArray();
            var state = record.Closures.ContainsKey(attempt.Id) ? RecoveryState.Closed : claims.Length != 0 ? RecoveryState.Uncertain :
                File.Exists(Path.Combine(AttemptFolder(workflow, run, attempt.Task, attempt.Id), "events.jsonl")) ? RecoveryState.Reserved :
                    RecoveryState.RequestMissing;
            return new AttemptRecovery(attempt.Id, state, claims);
        })]);
    }

    public RunDecision Recover(RunLease lease, OperationId operation, AttemptId attempt,
        RecoveryOutcome? outcome = null, OperationId? confirmation = null, string? reason = null) =>
        RecoverCore(lease.Permit.Workflow, lease.Permit.Run, operation, attempt, outcome, confirmation, reason,
            record => AttemptLeaseProblem(record, lease, attempt));

    public RunDecision Recover(LegacyRun run, StandaloneLease lease, OperationId operation, AttemptId attempt,
        RecoveryOutcome? outcome = null, OperationId? confirmation = null, string? reason = null) =>
        RecoverCore(run.Workflow, run.Run, operation, attempt, outcome, confirmation, reason,
            record => LegacyProblem(record) ?? StandaloneProblem(record, lease, attempt));

    private RunDecision RecoverCore(WorkflowId workflow, RunId run, OperationId operation, AttemptId attempt,
        RecoveryOutcome? outcome, OperationId? confirmation, string? reason, Func<RunRecord, RunProblem?> validate)
    {
        if (workflow.Value == Guid.Empty || run.Value == Guid.Empty || operation.Value == Guid.Empty)
        {
            return new RunDecision.Rejected(new(RunProblem.InvalidData));
        }
        try
        {
            var read = Read(workflow, run);
            if (read is RunRead.Rejected rejected) return new RunDecision.Rejected(rejected.Reason);
            var current = ((RunRead.Loaded)read).Record;
            if (validate(current) is { } problem)
                return new RunDecision.Rejected(new(problem));
            return Transact(workflow, run, operation, Fingerprint("recover", new
            {
                attempt,
                outcome,
                confirmation,
                reason
            }), (record, _) =>
            {
                if (record is null)
                {
                    return Missing();
                }

                if (!record.Attempts.TryGetValue(attempt, out var owner))
                {
                    return Refuse(RunProblem.UnknownAttempt);
                }

                if (record.Closures.TryGetValue(attempt, out var existing))
                {
                    return new Mutation.Existing(new RunEvent.AttemptClosed(attempt, existing));
                }

                var read = AttemptEvidence.Read(AttemptFolder(workflow, run, owner.Task, owner.Id));
                if (read.Rejection is null && read.Checkpoint is { } checkpoint && OwnedEvidence(record, owner, checkpoint).Rejection is null &&
                    AttemptEvidence.Terminal(read) is { } terminal && AttemptEvidence.Matches(read, terminal))
                {
                    return new Mutation.Append(new RunEvent.AttemptClosed(attempt, new AttemptEnd.Logged(terminal, checkpoint)));
                }

                if (outcome is null)
                {
                    return Refuse(RunProblem.RecoveryEvidenceInsufficient);
                }

                if (confirmation is not { Value: var id } || id == Guid.Empty || string.IsNullOrWhiteSpace(reason))
                {
                    return Refuse(RunProblem.ConfirmationRequired);
                }

                return new Mutation.Append(new RunEvent.AttemptClosed(attempt, new AttemptEnd.Recovered(outcome.Value, confirmation.Value, reason)));
            }, validate: validate);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new RunDecision.Rejected(new(RunProblem.StorageUnavailable));
        }
    }

    public RunDecision AcceptReport(CoordinatorPermit permit, OperationId operation, AttemptId attempt, InputId inputs,
        string report, ResultId? supersedes = null) =>
        Transact(permit, operation, Fingerprint("acceptReport", new
        {
            attempt,
            inputs,
            report,
            supersedes
        }), (record, _) =>
        {
            if (record is null)
            {
                return Missing();
            }

            var previous = record.Results.FirstOrDefault(result => result.Origin is ResultOrigin.Executed executed && executed.Attempt == attempt);
            if (previous is not null)
            {
                return previous.Inputs == inputs && previous.Report == report && previous.Supersedes == supersedes
                ? new Mutation.Existing(new RunEvent.ResultAccepted(previous, record.Inputs[previous.Inputs])) : Refuse(RunProblem.StartConflict);
            }

            if (!record.Attempts.TryGetValue(attempt, out var owner))
            {
                return Refuse(RunProblem.UnknownAttempt);
            }

            if (!record.Inputs.TryGetValue(inputs, out var capture))
            {
                return Refuse(RunProblem.UnknownInput);
            }

            if (record.Revisions[owner.Revision].Snapshot.Tasks[owner.Task].Blueprint.Work is not WorkSpec.Agent { Access: AgentAccess.ReadOnly } &&
                (record.Schema == 1 || record.Revisions[owner.Revision].Snapshot.Tasks[owner.Task].Blueprint.Work is not WorkSpec.Review))
            {
                return Refuse(RunProblem.UnsupportedResult);
            }

            if (record.CurrentResults.TryGetValue(owner.Task, out var current) ? supersedes != current.Id : supersedes is not null)
            {
                return Refuse(RunProblem.StartConflict);
            }

            if (record.Closures.GetValueOrDefault(attempt) is not AttemptEnd.Logged { Outcome: TerminalAttemptOutcome.Succeeded } closure)
            {
                return Refuse(RunProblem.OutcomeMismatch);
            }

            var read = OwnedEvidence(record, owner, closure.Evidence);
            if (read.Rejection is { } rejection)
            {
                return new Mutation.Rejected(rejection);
            }

            if (!AttemptEvidence.Matches(read, TerminalAttemptOutcome.Succeeded) || read.Record!.Result != report ||
                !record.Claims.TryGetValue(new(attempt, read.Record.Turns.Count), out var claim) || claim.Inputs.Id != inputs)
            {
                return Refuse(RunProblem.OutcomeMismatch);
            }

            return new Mutation.Append(new RunEvent.ResultAccepted(new(new(_ids()), owner.Task, owner.Revision, inputs,
                            new ResultOrigin.Executed(attempt), report, supersedes)
            {
                Code = record.Schema >= 2 && capture.Code is CodeSelection.Single or CodeSelection.Joined ? new CodeOutput.Forwarded(inputs) : null,
            }, capture));
        });

    public RunDecision ReuseReport(CoordinatorPermit permit, OperationId operation, TaskId task, AttemptSource.Standalone source,
        OperationId confirmation) =>
        Transact(permit, operation, Fingerprint("reuseReport", new
        {
            task,
            source,
            confirmation
        }), (record, _) =>
        {
            if (record is null)
            {
                return Missing();
            }

            if (confirmation.Value == Guid.Empty)
            {
                return Refuse(RunProblem.ConfirmationRequired);
            }

            var previous = record.Results.FirstOrDefault(result => result.Task == task && result.Origin is ResultOrigin.Reused reused &&
                RunReducer.Same<AttemptSource>(reused.Source, source));
            if (previous is not null)
            {
                return new Mutation.Existing(new RunEvent.ResultAccepted(previous, record.Inputs[previous.Inputs]));
            }
            if (!record.Revision.Snapshot.Tasks.TryGetValue(task, out var definition) ||
                record.Revision.Snapshot.Connections.Keys.Any(edge => edge.To == task))
            {
                return Refuse(RunProblem.ReuseUnverifiable);
            }

            if (record.Attempts.Values.Any(attempt => attempt.Task == task) || record.CurrentResults.ContainsKey(task))
            {
                return Refuse(RunProblem.StartConflict);
            }

            var reuse = ReportReuse.Validate(_project, definition, source, record.Base.Commit, confirmation);
            if (reuse.Rejection is { } rejection)
            {
                return new Mutation.Rejected(rejection);
            }

            var inputs = new InputRecord(new(_ids()), task, record.Revision.Id, [],
                record.Schema == 1 ? new CodeSelection.Legacy(record.Base.Commit) : new CodeSelection.Root(record.Base.Commit), "", [], null);
            return new Mutation.Append(new RunEvent.ResultAccepted(new(new(_ids()), task, record.Revision.Id, inputs.Id,
                new ResultOrigin.Reused(source, reuse.Evidence!), reuse.Report!, null), inputs));
        });

    public RunDecision Amend(CoordinatorPermit permit, OperationId operation, RevisionId previous, ApprovedRevision candidate,
        AmendmentOrigin origin, OperationId confirmation) =>
        Transact(permit, operation, Fingerprint("amend", new
        {
            previous,
            candidate.Id,
            snapshot = Revision.Canonical(candidate.Snapshot),
            origin,
            confirmation
        }), (record, _) =>
        {
            if (record is null)
            {
                return Missing();
            }

            if (origin is AmendmentOrigin.Planner)
            {
                return Refuse(RunProblem.InvalidData);
            }

            return new Mutation.Append(new RunEvent.Amended(previous, candidate, origin, confirmation));
        });

    public RunDecision AmendFromProposal(CoordinatorPermit permit, OperationId operation, RevisionId previous, Proposal proposal,
        IReadOnlySet<TaskId> chosen, OperationId confirmation, ExecutionSettings? fallback = null) =>
        Transact(permit, operation, Fingerprint("amendProposal", new
        {
            previous,
            proposal,
            chosen = chosen.Order().ToArray(),
            confirmation,
            fallback
        }), (record, _) =>
        {
            if (record is null)
            {
                return Missing();
            }

            if (record.Revision.Id != previous)
            {
                return Refuse(RunProblem.RevisionConflict);
            }

            if (!record.Attempts.TryGetValue(proposal.Attempt, out var planner) || planner.Task != proposal.Planner)
            {
                return Refuse(RunProblem.UnknownAttempt);
            }

            if (proposal.Turn < 1 || !record.TurnClosures.TryGetValue(new(planner.Id, proposal.Turn), out var checkpoint))
            {
                return Refuse(RunProblem.EvidenceMismatch);
            }

            var read = OwnedEvidence(record, planner, checkpoint);
            if (read.Rejection is not null || read.Record is null)
            {
                return Refuse(RunProblem.EvidenceMismatch);
            }

            if (Proposal.Read(read.Record, key => record.Revision.Snapshot.Blueprints.GetValueOrDefault(key)) is not ProposalRead.Ready ready ||
                RunJournal.Canonical(ready.Proposal) != RunJournal.Canonical(proposal))
            {
                return Refuse(RunProblem.EvidenceMismatch);
            }

            bool Started(TaskId id) => record.Attempts.Values.Any(attempt => attempt.Task == id) || record.Results.Any(result => result.Task == id);
            return record.Revision.Snapshot.Apply(proposal.Accept(record.Revision.Snapshot, chosen, Started, fallback)) is EditResult.Applied applied
                ? new Mutation.Append(new RunEvent.Amended(previous, Revision.Capture(applied.Workflow),
                    new AmendmentOrigin.Planner(planner.Id, proposal.Turn), confirmation))
                : Refuse(RunProblem.InvalidData);
        });

    public RunDecision Stop(CoordinatorPermit permit, OperationId operation) =>
        Transact(permit, operation, Fingerprint("stop", new
        {
        }), (record, _) => record is null ? Missing() :
            record.Phase == RunPhase.StopRequested ? new Mutation.Existing(new RunEvent.StopRequested()) :
                new Mutation.Append(new RunEvent.StopRequested()));

    public RunDecision Settle(CoordinatorPermit permit, OperationId operation, RunOutcome outcome) =>
        Transact(permit, operation, Fingerprint("settle", new
        {
            outcome
        }), (_, _) => new Mutation.Append(new RunEvent.Settled(outcome)));

    public RunDecision Abandon(CoordinatorPermit permit, OperationId operation, OperationId confirmation, string reason) =>
        Transact(permit, operation, Fingerprint("abandon", new
        {
            confirmation,
            reason
        }), (_, _) => new Mutation.Append(new RunEvent.Abandoned(confirmation, reason)));

    internal static (InputRecord? Inputs, RunRejection? Rejection) Inputs(RunRecord record, TaskId task, RevisionId revision,
        InputId id)
    {
        if (!record.Revisions.TryGetValue(revision, out var snapshot) || !snapshot.Snapshot.Tasks.ContainsKey(task))
        {
            return (null, new(RunProblem.RevisionConflict));
        }

        var bindings = ImmutableArray.CreateBuilder<InputBinding>();
        foreach (var edge in snapshot.Snapshot.Connections.Where(edge => edge.Key.To == task))
        {
            if (record.CurrentResults.TryGetValue(edge.Key.From, out var result))
            {
                if (record.StaleResults.Contains(result.Id))
                {
                    if (edge.Value == ConnectionKind.Dependency)
                    {
                        return (null, new(RunProblem.StaleInput, Task: result.Task));
                    }

                    bindings.Add(new InputBinding.MissingContext(edge.Key));
                    continue;
                }

                bindings.Add(new InputBinding.Provided(edge.Key, edge.Value, result.Id));
            }
            else if (edge.Value == ConnectionKind.Context)
            {
                bindings.Add(new InputBinding.MissingContext(edge.Key));
            }
            else
            {
                return (null, new(RunProblem.MissingDependencyResult, Task: edge.Key.From));
            }
        }
        return (new(id, task, revision, bindings.ToImmutable(), new CodeSelection.Root(record.Base.Commit), "", [], null), null);
    }

    private AttemptEvidence OwnedEvidence(RunRecord record, RunAttempt attempt, LogCheckpoint checkpoint)
    {
        var read = AttemptEvidence.Read(AttemptFolder(record.Workflow, record.Id, attempt.Task, attempt.Id), checkpoint);
        if (read.Rejection is not null)
        {
            return read;
        }

        var request = (AttemptEvent.Requested)read.Events[0];
        var task = record.Revisions[attempt.Revision].Snapshot.Tasks[attempt.Task];
        var binding = new RunBinding(record.Workflow, record.Id, attempt.Revision, attempt.InitialInputs);
        if (request.Attempt != attempt.Id || request.Task != attempt.Task || request.RunBinding != binding ||
            request.Settings != task.Execution || request.Conversation != task.Conversation || request.TaskTitle != task.Title ||
            request.ReadOnly != (task.Blueprint.Work is WorkSpec.Agent { Access: AgentAccess.ReadOnly } or WorkSpec.Review) ||
            record.Schema == 1 && attempt.Cause is AttemptCause.Initial && task.Blueprint.Work is WorkSpec.Agent { Proposes: false } &&
                request.Prompt != ReportReuse.Prompt(task, record.Inputs[attempt.InitialInputs].Text))
        {
            return read with
            {
                Rejection = new(RunProblem.EvidenceMismatch)
            };
        }

        var prompts = read.Events.Where(e => e is AttemptEvent.Requested or AttemptEvent.TurnRequested).Select(e => e switch
                {
                    AttemptEvent.Requested first => first.Prompt,
                    AttemptEvent.TurnRequested turn => turn.Prompt,
                    _ => "",
                }).ToArray();
        if (record.Schema >= 2 && prompts.Where((prompt, index) =>
            !record.Preparations.TryGetValue(new(attempt.Id, index + 1), out var prepared) || prepared.Prompt != prompt).Any())
        {
            return read with { Rejection = new(RunProblem.EvidenceMismatch) };
        }
        if (read.Record!.Terminal is not null || request.Fix != record.ReviewOf(attempt.Id) || record.Claims.Values.Any(claim =>
            claim.Key.Attempt == attempt.Id && (claim.Key.Turn > prompts.Length || claim.Prompt != Revision.Hash(prompts[claim.Key.Turn - 1]))))
        {
            return read with
            {
                Rejection = new(RunProblem.EvidenceMismatch)
            };
        }

        return read;
    }

    private RunProblem? PermitProblem(RunRecord record, CoordinatorPermit permit)
    {
        if (record.Schema != 3) return RunProblem.UnsupportedSchema;
        if (!permit.Held) return RunProblem.TaskBusy;
        if (!SameProject(permit.Project) || permit.Workflow != record.Workflow || permit.Run != record.Id)
            return RunProblem.IdentityMismatch;
        return null;
    }

    private bool SameProject(string project) => string.Equals(project, _project,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private RunProblem? LeaseProblem(RunRecord record, RunLease lease)
    {
        if (PermitProblem(record, lease.Permit) is { } problem) return problem;
        if (!lease.Held) return RunProblem.TaskBusy;
        return SameProject(lease.Project) ? null : RunProblem.IdentityMismatch;
    }

    private RunProblem? AttemptLeaseProblem(RunRecord record, RunLease lease, AttemptId attempt)
    {
        if (LeaseProblem(record, lease) is { } problem) return problem;
        if (!record.Attempts.TryGetValue(attempt, out var owner)) return RunProblem.UnknownAttempt;
        return owner.Task != lease.Task ? RunProblem.IdentityMismatch : null;
    }

    private static RunProblem? LegacyProblem(RunRecord record) => record.Schema is 1 or 2 ? null : RunProblem.UnsupportedSchema;

    private RunProblem? StandaloneProblem(RunRecord record, StandaloneLease lease, AttemptId attempt)
    {
        if (!lease.Held) return RunProblem.TaskBusy;
        if (!SameProject(lease.Project)) return RunProblem.IdentityMismatch;
        if (!record.Attempts.TryGetValue(attempt, out var owner)) return RunProblem.UnknownAttempt;
        return owner.Task != lease.Task ? RunProblem.IdentityMismatch : null;
    }

    private RunDecision Transact(CoordinatorPermit permit, OperationId operation, Digest fingerprint,
        Func<RunRecord?, ImmutableArray<RunRecord>, Mutation> decide, Func<RunRecord, RunProblem?>? validate = null) =>
        Transact(permit.Workflow, permit.Run, operation, fingerprint, decide,
            validate: record => PermitProblem(record, permit) ?? validate?.Invoke(record));

    public RunDecision Stop(LegacyRun run, OperationId operation) =>
        Transact(run.Workflow, run.Run, operation, Fingerprint("stop", new { }),
            (record, _) => record is null ? Missing() : record.Phase == RunPhase.StopRequested
                ? new Mutation.Existing(new RunEvent.StopRequested()) : new Mutation.Append(new RunEvent.StopRequested()),
            validate: LegacyProblem);

    public RunDecision Settle(LegacyRun run, OperationId operation, RunOutcome outcome) =>
        Transact(run.Workflow, run.Run, operation, Fingerprint("settle", new { outcome }),
            (_, _) => new Mutation.Append(new RunEvent.Settled(outcome)), validate: LegacyProblem);

    public RunDecision Abandon(LegacyRun run, OperationId operation, OperationId confirmation, string reason) =>
        Transact(run.Workflow, run.Run, operation, Fingerprint("abandon", new { confirmation, reason }),
            (_, _) => new Mutation.Append(new RunEvent.Abandoned(confirmation, reason)), validate: LegacyProblem);

    private RunDecision Transact(WorkflowId workflow, RunId run, OperationId operation, Digest fingerprint,
        Func<RunRecord?, ImmutableArray<RunRecord>, Mutation> decide, Func<RunRecord, RunProblem?>? validate = null)
    {
        if (workflow.Value == Guid.Empty || run.Value == Guid.Empty || operation.Value == Guid.Empty)
        {
            return new RunDecision.Rejected(new(RunProblem.InvalidData));
        }

        try
        {
            Directory.CreateDirectory(_runs);
            using var held = TakeWriteLock();
            if (held is null)
            {
                return new RunDecision.Rejected(new(RunProblem.JournalBusy));
            }

            var all = ImmutableArray.CreateBuilder<RunRecord>();
            var workflowFolder = Path.Combine(_runs, workflow.ToString());
            foreach (var runFolder in Directory.Exists(workflowFolder) ? Directory.EnumerateDirectories(workflowFolder) : [])
            {
                var path = Path.Combine(runFolder, "events.jsonl");
                if (!File.Exists(path) || new FileInfo(path).Length == 0)
                {
                    continue;
                }

                if (!Guid.TryParse(Path.GetFileName(runFolder), out var foundRun))
                {
                    return new RunDecision.Rejected(new(RunProblem.InvalidData));
                }

                var read = ReadJournal(workflow, new(foundRun));
                if (read is RunRead.Rejected rejected)
                {
                    if (rejected.Reason.Problem == RunProblem.IncompleteTail && (foundRun != run.Value || rejected.Prefix is null))
                    {
                        if (rejected.Prefix is { } prefix)
                        {
                            all.Add(prefix);
                        }
                        continue;
                    }

                    return new RunDecision.Rejected(rejected.Reason);
                }

                all.Add(((RunRead.Loaded)read).Record);
            }

            var record = all.FirstOrDefault(record => record.Workflow == workflow && record.Id == run);
            if (record is not null && validate?.Invoke(record) is { } invalid)
                return new RunDecision.Rejected(new(invalid));
            if (record?.Receipts.TryGetValue(operation, out var receipt) == true)
            {
                return receipt.Command == fingerprint ? new RunDecision.Existing(record, receipt.Event) :
                    new RunDecision.Rejected(new(RunProblem.OperationConflict, receipt.Sequence));
            }
            var mutation = decide(record, all.ToImmutable());
            if (mutation is Mutation.Rejected rejection)
            {
                return new RunDecision.Rejected(rejection.Reason);
            }

            if (mutation is Mutation.Existing existing)
            {
                return new RunDecision.Existing(record!, existing.Event);
            }

            var append = (Mutation.Append)mutation;
            var entry = new RunEntry(record?.Schema ?? 3, (record?.Sequence ?? 0) + 1, operation, fingerprint, _clock.GetUtcNow(), append.Event);
            var reduced = RunReducer.Apply(workflow, run, record, entry);
            if (reduced is RunRead.Rejected refused)
            {
                return new RunDecision.Rejected(refused.Reason);
            }

            var updated = ((RunRead.Loaded)reduced).Record;
            DataFolder.EnsureGitIgnore(_project);
            Directory.CreateDirectory(Folder(workflow, run));
            using (var stream = new FileStream(Journal(workflow, run), record is null ? FileMode.Create : FileMode.Append,
                FileAccess.Write, FileShare.Read))
            {
                stream.Write(Encoding.UTF8.GetBytes(RunJournal.Encode(entry)));
                stream.Flush(flushToDisk: true);
            }
            return append.Grant
                ? new RunDecision.Granted(updated, (RunEvent.TurnClaimed)append.Event)
                : append.Event is RunEvent.Reserved or RunEvent.Approved or RunEvent.ResultAccepted
                    ? new RunDecision.Created(updated, append.Event)
                    : new RunDecision.Recorded(updated, append.Event);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return new RunDecision.Rejected(new(RunProblem.StorageUnavailable));
        }
    }

    private FileStream? TakeWriteLock()
    {
        var patience = Stopwatch.StartNew();
        do
        {
            try
            {
                return new FileStream(Path.Combine(_runs, "write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                Thread.Sleep(20);
            }
        } while (patience.Elapsed < TimeSpan.FromSeconds(1));
        return null;
    }

    private RunRead ReadJournal(WorkflowId workflow, RunId run, string? path = null)
    {
        path ??= Journal(workflow, run);
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            return new RunRead.Rejected(new(RunProblem.NotApproved));
        }

        var journal = RunJournal.Decode(File.ReadAllBytes(path));
        var read = RunReducer.Replay(workflow, run, journal.Entries);
        if (journal.Entries.Length != 0 && read is RunRead.Rejected)
        {
            return read;
        }

        return journal.Rejection is { } problem ? new RunRead.Rejected(problem, (read as RunRead.Loaded)?.Record) : read;
    }

    private string Folder(WorkflowId workflow, RunId run) => Path.Combine(_runs, workflow.ToString(), run.ToString());

    private string Journal(WorkflowId workflow, RunId run) => Path.Combine(Folder(workflow, run), "events.jsonl");

    private static Digest Fingerprint<T>(string command, T content) => Revision.Hash(RunJournal.Canonical(new { command, content }));

    private static RunEvent.Reserved Reservation(RunRecord record, AttemptId attempt) => record.Receipts.Values
        .Select(entry => entry.Event).OfType<RunEvent.Reserved>().Single(reserved => reserved.Attempt.Id == attempt);

    private static Mutation Missing() => Refuse(RunProblem.NotApproved);

    private static Mutation Refuse(RunProblem problem) => new Mutation.Rejected(new(problem));

    internal abstract record Mutation
    {
        private Mutation() { }

        internal sealed record Append(RunEvent Event, bool Grant = false) : Mutation;

        internal sealed record Existing(RunEvent Event) : Mutation;

        internal sealed record Rejected(RunRejection Reason) : Mutation;
    }
}
