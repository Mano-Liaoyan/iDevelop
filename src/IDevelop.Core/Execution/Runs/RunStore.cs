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

    public RunDecision Reserve(WorkflowId workflow, RunId run, OperationId operation, TaskId task, RevisionId revision,
        AttemptCause cause, CommitId codeBase, string text) =>
        Transact(workflow, run, operation, Fingerprint("reserve", new
        {
            task,
            revision,
            cause,
            codeBase,
            text
        }), (record, _) =>
        {
            if (record is null)
            {
                return Missing();
            }

            if (cause is AttemptCause.Retry { Confirmation.Value: var retry } && retry == Guid.Empty ||
                cause is AttemptCause.Continue { Confirmation.Value: var continued } && continued == Guid.Empty)
            {
                return Refuse(RunProblem.ConfirmationRequired);
            }

            if (RunReducer.Slot(record, task, cause) is { } existing)
            {
                var original = Reservation(record, existing.Id);
                var inputs = original.Inputs;
                var sameCause = cause switch
                {
                    AttemptCause.Retry => existing.Cause is AttemptCause.Retry,
                    AttemptCause.Continue => existing.Cause is AttemptCause.Continue,
                    _ => existing.Cause == cause,
                };
                return existing.Revision == revision && inputs.CodeBase == codeBase && inputs.Text == text && sameCause
                    ? new Mutation.Existing(original)
                    : new Mutation.Rejected(new(RunReducer.Previous(cause) is null ? RunProblem.StartConflict : RunProblem.ReplacementConflict));
            }
            if (RunReducer.ReservationTaskProblem(record, task, revision) is { } taskProblem)
            {
                return new Mutation.Rejected(taskProblem);
            }

            if (!Revision.IsCommit(codeBase.Hex) || text is null)
            {
                return Refuse(RunProblem.InvalidData);
            }

            if (RunReducer.ReservationProblem(record, task, cause) is { } problem)
            {
                return Refuse(problem);
            }

            var capture = Inputs(record, task, revision, default, codeBase, text);
            if (capture.Rejection is { } rejection)
            {
                return new Mutation.Rejected(rejection);
            }

            var capturedInputs = capture.Inputs! with
            {
                Id = new(_ids())
            };
            var attempt = new RunAttempt(new(_ids()), task, revision, capturedInputs.Id, cause);
            return new Mutation.Append(new RunEvent.Reserved(attempt, capturedInputs));
        });

    public RunDecision Claim(WorkflowId workflow, RunId run, OperationId operation, LaunchKey key, InputRecord inputs, Digest prompt) =>
        Transact(workflow, run, operation, Fingerprint("claim", new
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
        });

    public RunDecision CloseTurn(WorkflowId workflow, RunId run, OperationId operation, LaunchKey key, LogCheckpoint evidence) =>
        Transact(workflow, run, operation, Fingerprint("closeTurn", new
        {
            key,
            evidence
        }), (record, _) =>
        {
            if (record is null)
            {
                return Missing();
            }

            if (record.TurnClosures.TryGetValue(key, out var existing))
            {
                return existing == evidence
                            ? new Mutation.Existing(new RunEvent.TurnClosed(key, existing)) : Refuse(RunProblem.EvidenceMismatch);
            }

            if (!record.Attempts.TryGetValue(key.Attempt, out var attempt))
            {
                return Refuse(RunProblem.UnknownAttempt);
            }

            var read = OwnedEvidence(record, attempt, evidence);
            if (read.Rejection is { } rejection)
            {
                return new Mutation.Rejected(rejection);
            }

            if (key.Turn < 1 || read.Record!.Turns.Count < key.Turn || read.Record.Turns[key.Turn - 1].Outcome == TurnOutcome.Running)
            {
                return Refuse(RunProblem.OutcomeMismatch);
            }

            return new Mutation.Append(new RunEvent.TurnClosed(key, evidence));
        });

    public RunDecision CloseAttempt(WorkflowId workflow, RunId run, OperationId operation, AttemptId attempt,
        TerminalAttemptOutcome outcome, LogCheckpoint evidence) =>
        Transact(workflow, run, operation, Fingerprint("closeAttempt", new
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

            var end = new AttemptEnd.Logged(outcome, evidence);
            if (record.Closures.TryGetValue(attempt, out var existing))
            {
                return existing == end
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

    public ImmutableArray<AttemptRecovery> InspectRecovery(WorkflowId workflow, RunId run) => Read(workflow, run) is RunRead.Loaded loaded
        ? [.. loaded.Record.Attempts.Values.OrderBy(attempt => attempt.Id.Value).Select(attempt =>
        {
            var claims = loaded.Record.UnresolvedClaims.Where(key => key.Attempt == attempt.Id).ToImmutableArray();
            var state = loaded.Record.Closures.ContainsKey(attempt.Id) ? RecoveryState.Closed : claims.Length != 0 ? RecoveryState.Uncertain :
                File.Exists(Path.Combine(AttemptFolder(workflow, run, attempt.Task, attempt.Id), "events.jsonl")) ? RecoveryState.Reserved :
                    RecoveryState.RequestMissing;
            return new AttemptRecovery(attempt.Id, state, claims);
        })]
        : [];

    public RunDecision Recover(WorkflowId workflow, RunId run, OperationId operation, AttemptId attempt,
        RecoveryOutcome? outcome = null, OperationId? confirmation = null, string? reason = null)
    {
        RunLock? held = null;
        try
        {
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

                held = RunLock.TryTake(DataFolder.Attempts(_project), owner.Task);
                if (held is null)
                {
                    return Refuse(RunProblem.TaskBusy);
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
            });
        }
        finally
        {
            held?.Dispose();
        }
    }

    public RunDecision AcceptReport(WorkflowId workflow, RunId run, OperationId operation, AttemptId attempt, InputId inputs,
        string report, ResultId? supersedes = null) =>
        Transact(workflow, run, operation, Fingerprint("acceptReport", new
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

            if (record.Revisions[owner.Revision].Snapshot.Tasks[owner.Task].Blueprint.Work is not WorkSpec.Agent { Access: AgentAccess.ReadOnly })
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
                            new ResultOrigin.Executed(attempt), report, supersedes), capture));
        });

    public RunDecision ReuseReport(WorkflowId workflow, RunId run, OperationId operation, TaskId task, AttemptSource.Standalone source,
        CommitId codeBase, OperationId confirmation) =>
        Transact(workflow, run, operation, Fingerprint("reuseReport", new
        {
            task,
            source,
            codeBase,
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
                reused.Source == source);
            if (previous is not null)
            {
                return record.Inputs[previous.Inputs].CodeBase == codeBase
                    ? new Mutation.Existing(new RunEvent.ResultAccepted(previous, record.Inputs[previous.Inputs])) : Refuse(RunProblem.StartConflict);
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

            var reuse = ReportReuse.Validate(_project, definition, source, codeBase, confirmation);
            if (reuse.Rejection is { } rejection)
            {
                return new Mutation.Rejected(rejection);
            }

            var inputs = new InputRecord(new(_ids()), task, record.Revision.Id, [], codeBase, "");
            return new Mutation.Append(new RunEvent.ResultAccepted(new(new(_ids()), task, record.Revision.Id, inputs.Id,
                new ResultOrigin.Reused(source, reuse.Evidence!), reuse.Report!, null), inputs));
        });

    public RunDecision Amend(WorkflowId workflow, RunId run, OperationId operation, RevisionId previous, ApprovedRevision candidate,
        AmendmentOrigin origin, OperationId confirmation) =>
        Transact(workflow, run, operation, Fingerprint("amend", new
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

    public RunDecision AmendFromProposal(WorkflowId workflow, RunId run, OperationId operation, RevisionId previous, Proposal proposal,
        IReadOnlySet<TaskId> chosen, OperationId confirmation, ExecutionSettings? fallback = null) =>
        Transact(workflow, run, operation, Fingerprint("amendProposal", new
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

    public RunDecision Stop(WorkflowId workflow, RunId run, OperationId operation) =>
        Transact(workflow, run, operation, Fingerprint("stop", new
        {
        }), (record, _) => record is null ? Missing() :
            record.Phase == RunPhase.StopRequested ? new Mutation.Existing(new RunEvent.StopRequested()) :
                new Mutation.Append(new RunEvent.StopRequested()));

    public RunDecision Settle(WorkflowId workflow, RunId run, OperationId operation, RunOutcome outcome) =>
        Transact(workflow, run, operation, Fingerprint("settle", new
        {
            outcome
        }), (_, _) => new Mutation.Append(new RunEvent.Settled(outcome)));

    public RunDecision Abandon(WorkflowId workflow, RunId run, OperationId operation, OperationId confirmation, string reason) =>
        Transact(workflow, run, operation, Fingerprint("abandon", new
        {
            confirmation,
            reason
        }), (_, _) => new Mutation.Append(new RunEvent.Abandoned(confirmation, reason)));

    public static (InputRecord? Inputs, RunRejection? Rejection) CaptureInputs(RunRecord record, TaskId task, RevisionId revision,
        InputId id, CommitId codeBase, string text) => Inputs(record, task, revision, id, codeBase, text);

    private static (InputRecord? Inputs, RunRejection? Rejection) Inputs(RunRecord record, TaskId task, RevisionId revision,
        InputId id, CommitId codeBase, string text)
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
                    return (null, new(RunProblem.StaleInput, Task: result.Task));
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
        return (new(id, task, revision, bindings.ToImmutable(), codeBase, text), null);
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
            attempt.Cause is AttemptCause.Initial && task.Blueprint.Work is WorkSpec.Agent { Proposes: false } &&
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

    private RunDecision Transact(WorkflowId workflow, RunId run, OperationId operation, Digest fingerprint,
        Func<RunRecord?, ImmutableArray<RunRecord>, Mutation> decide)
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
            foreach (var workflowFolder in Directory.EnumerateDirectories(_runs))
            {
                foreach (var runFolder in Directory.EnumerateDirectories(workflowFolder))
                {
                    foreach (var path in Directory.EnumerateFiles(runFolder, "events.jsonl", SearchOption.TopDirectoryOnly))
                    {
                        if (!Guid.TryParse(Path.GetFileName(runFolder), out var foundRun) ||
                            !Guid.TryParse(Path.GetFileName(workflowFolder), out var foundWorkflow))
                        {
                            return new RunDecision.Rejected(new(RunProblem.InvalidData));
                        }

                        var read = ReadJournal(new(foundWorkflow), new(foundRun));
                        if (read is RunRead.Rejected rejected)
                        {
                            return new RunDecision.Rejected(rejected.Reason);
                        }

                        all.Add(((RunRead.Loaded)read).Record);
                    }
                }
            }

            var record = all.FirstOrDefault(record => record.Workflow == workflow && record.Id == run);
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
            var entry = new RunEntry(1, (record?.Sequence ?? 0) + 1, operation, fingerprint, _clock.GetUtcNow(), append.Event);
            var reduced = RunReducer.Apply(workflow, run, record, entry);
            if (reduced is RunRead.Rejected refused)
            {
                return new RunDecision.Rejected(refused.Reason);
            }

            var updated = ((RunRead.Loaded)reduced).Record;
            DataFolder.EnsureGitIgnore(_project);
            Directory.CreateDirectory(Folder(workflow, run));
            using (var stream = new FileStream(Journal(workflow, run), FileMode.Append, FileAccess.Write, FileShare.Read))
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

    private RunRead ReadJournal(WorkflowId workflow, RunId run)
    {
        if (!File.Exists(Journal(workflow, run)))
        {
            return new RunRead.Rejected(new(RunProblem.NotApproved));
        }

        var journal = RunJournal.Decode(File.ReadAllText(Journal(workflow, run), new UTF8Encoding(false, true)));
        var read = RunReducer.Replay(workflow, run, journal.Entries);
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
