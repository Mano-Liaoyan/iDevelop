using System.Collections.Immutable;
using IDevelop.Execution;
using IDevelop.Projects;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Runs;

public sealed class RunStoreTests
{
    [Fact]
    public void Approval_reservation_closure_and_accepted_report_reopen_with_exact_ownership()
    {
        using var f = new RunFixtures();
        f.Approve();
        var attempt = f.Reserve();
        f.Complete(attempt);
        var record = Assert.IsType<RunRead.Loaded>(f.NewStore().Read(W, Run)).Record;
        Assert.Equal(V1, record.Revision.Id.Sha256);
        Assert.Equal([new AttemptId(Id(102))], record.Attempts.Keys);
        Assert.Equal([new ResultId(Id(103))], record.Results.Select(result => result.Id));
        Assert.Equal("Checked.", record.CurrentResults[T].Report);
        Assert.Equal(new ResultOrigin.Executed(new(Id(102))), record.CurrentResults[T].Origin);
        Assert.Equal(new AttemptEnd.Logged(TerminalAttemptOutcome.Succeeded, Checkpoint(f.Store.AttemptFolder(W, Run, T, A1))), record.Closures[A1]);
        Assert.Equal(At, record.Receipts.Values.OrderBy(entry => entry.Sequence).First().At);
    }

    [Fact]
    public void Recorded_commands_return_the_original_attempt_and_conflicting_operations_reject()
    {
        using var f = new RunFixtures();
        f.Approve();
        var op = f.Op();
        Assert.IsType<RunDecision.Created>(f.Store.Reserve(f.Lease(T), op, new(V1), new AttemptCause.Initial()));
        var repeat = Assert.IsType<RunDecision.Existing>(f.NewStore().Reserve(f.Lease(T), op, new(V1), new AttemptCause.Initial()));
        Assert.Equal(A1, Assert.IsType<RunEvent.Reserved>(repeat.Event).Attempt.Id);
        Assert.Equal(RunProblem.OperationConflict, Problem(f.Store.Reserve(f.Lease(U), op, new(V1), new AttemptCause.Initial())));
        Assert.Equal(3, f.Read().Sequence);
    }

    [Fact]
    public void Two_stores_reserve_one_permanent_initial_slot()
    {
        using var f = new RunFixtures();
        f.Approve();
        var second = f.NewStore();
        var created = f.Reserve();
        var existing = Assert.IsType<RunDecision.Existing>(second.Reserve(f.Lease(T), f.Op(), new(V1), new AttemptCause.Initial()));
        Assert.Equal(A1, created.Attempt.Id);
        Assert.Equal(A1, Assert.IsType<RunEvent.Reserved>(existing.Event).Attempt.Id);
        Assert.Equal([A1], f.Read().Attempts.Keys);
        Assert.Equal(3, f.Read().Sequence);
        Assert.Equal(RunProblem.StartConflict, Problem(second.Reserve(f.Lease(T), f.Op(), new(V2), new AttemptCause.Initial())));
    }

    [Theory]
    [InlineData(TerminalAttemptOutcome.Succeeded)]
    [InlineData(TerminalAttemptOutcome.Failed)]
    [InlineData(TerminalAttemptOutcome.Cancelled)]
    [InlineData(TerminalAttemptOutcome.Interrupted)]
    public void Initial_start_stays_the_same_after_every_terminal_outcome(object terminal)
    {
        var outcome = (TerminalAttemptOutcome)terminal;
        using var f = new RunFixtures();
        f.Approve();
        var reservation = f.Reserve();
        f.Claim(reservation);
        if (outcome == TerminalAttemptOutcome.Interrupted)
        {
            Assert.IsType<RunDecision.Recorded>(f.Store.Recover(f.Lease(T), f.Op(), A1, RecoveryOutcome.Stopped, f.Op(), "Confirmed stopped."));
        }
        else
        {
            Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), A1, outcome, f.WriteLog(reservation, outcome)));
        }

        var repeated = Assert.IsType<RunDecision.Existing>(f.NewStore().Reserve(f.Lease(T), f.Op(), new(V1), new AttemptCause.Initial()));
        Assert.Equal(A1, Assert.IsType<RunEvent.Reserved>(repeated.Event).Attempt.Id);
        Assert.Equal([A1], f.Read().Attempts.Keys);
    }

    [Fact]
    public void Only_the_first_of_two_store_claims_grants_launch_authority()
    {
        using var f = new RunFixtures();
        f.Approve();
        var reservation = f.Reserve();
        var op = f.Op();
        var second = f.NewStore();
        f.Prepare(reservation);
        var first = Assert.IsType<RunDecision.Granted>(f.Store.Claim(f.Lease(T), op, new(A1, 1), reservation.Inputs, Prompt));
        var repeated = Assert.IsType<RunDecision.Existing>(second.Claim(f.Lease(T), f.Op(), new(A1, 1), reservation.Inputs, Prompt));
        var transport = Assert.IsType<RunDecision.Existing>(second.Claim(f.Lease(T), op, new(A1, 1), reservation.Inputs, Prompt));
        Assert.Equal(new LaunchKey(A1, 1), first.Claim.Key);
        Assert.Equal(new LaunchKey(A1, 1), Assert.IsType<RunEvent.TurnClaimed>(repeated.Event).Key);
        Assert.Equal(new LaunchKey(A1, 1), Assert.IsType<RunEvent.TurnClaimed>(transport.Event).Key);
        Assert.Equal(7, f.Read().Sequence);
    }

    [Fact]
    public void Reservation_without_a_request_or_claim_reopens_as_RequestMissing()
    {
        using var f = new RunFixtures();
        f.Approve();
        f.Reserve();
        var second = f.NewStore();
        Assert.Equal([A1], Assert.IsType<RunRead.Loaded>(second.Read(W, Run)).Record.Attempts.Keys);
        Assert.Equal(new AttemptRecovery(A1, RecoveryState.RequestMissing, []), Assert.Single(Assert.IsType<RecoveryRead.Loaded>(second.InspectRecovery(W, Run)).Attempts));
        Assert.Equal(3, f.Read().Sequence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Claimed_launch_is_uncertain_with_or_without_a_request_log(bool requestExists)
    {
        using var f = new RunFixtures();
        f.Approve();
        var reservation = f.Reserve();
        f.Claim(reservation);
        if (requestExists)
        {
            var folder = f.Store.AttemptFolder(W, Run, T, A1);
            using var log = AttemptLog.Create(Path.GetDirectoryName(Path.GetDirectoryName(folder))!,
                new AttemptEvent.Requested(At, A1, T, "Plan", Task().Execution!, "Inspect", "codex", [])
                {
                    RunBinding = new(W, Run, new(V1), reservation.Inputs.Id),
                    Conversation = ConversationMode.Autonomous
                });
        }
        var recovery = Assert.Single(Assert.IsType<RecoveryRead.Loaded>(f.NewStore().InspectRecovery(W, Run)).Attempts);
        Assert.Equal((A1, RecoveryState.Uncertain), (recovery.Attempt, recovery.State));
        Assert.Equal([new LaunchKey(A1, 1)], recovery.Claims.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_free_lock_and_missing_or_reused_pid_cannot_resolve_a_claim(bool reusedPid)
    {
        using var f = new RunFixtures();
        f.Approve();
        var reservation = f.Reserve();
        f.Claim(reservation);
        if (reusedPid)
        {
            var folder = f.Store.AttemptFolder(W, Run, T, A1);
            using var log = AttemptLog.Create(Path.GetDirectoryName(Path.GetDirectoryName(folder))!, new AttemptEvent.Requested(At, A1, T,
                "Plan", Task().Execution!, "Inspect", "codex", []));
            log.Append(new AttemptEvent.Launched(At, Environment.ProcessId, At.AddYears(-10)));
        }
        Assert.Equal(RunProblem.RecoveryEvidenceInsufficient, Problem(f.Store.Recover(f.Lease(T), f.Op(), A1)));
        Assert.Equal(RecoveryState.Uncertain, Assert.Single(Assert.IsType<RecoveryRead.Loaded>(f.Store.InspectRecovery(W, Run)).Attempts).State);
    }

    [Fact]
    public void Confirmed_stopped_ownership_allows_one_separately_confirmed_retry()
    {
        using var f = new RunFixtures();
        f.Approve();
        var reservation = f.Reserve();
        f.Claim(reservation);
        var confirmation = f.Op();
        var recovered = Assert.IsType<RunDecision.Recorded>(f.Store.Recover(f.Lease(T), f.Op(), A1, RecoveryOutcome.Stopped, confirmation, "Owner and children stopped."));
        Assert.Equal(new AttemptEnd.Recovered(RecoveryOutcome.Stopped, confirmation, "Owner and children stopped."), recovered.Record.Closures[A1]);
        var cause = new AttemptCause.Retry(A1, f.Op());
        var next = f.Reserve(cause: cause);
        var repeated = Assert.IsType<RunDecision.Existing>(f.NewStore().Reserve(f.Lease(T), f.Op(), new(V1), cause));
        Assert.Equal(new AttemptId(Id(104)), next.Attempt.Id);
        Assert.Equal(A1, Assert.IsType<AttemptCause.Retry>(next.Attempt.Cause).Previous);
        Assert.Equal(new AttemptId(Id(104)), Assert.IsType<RunEvent.Reserved>(repeated.Event).Attempt.Id);
        Assert.Equal(RecoveryState.Closed, Assert.IsType<RecoveryRead.Loaded>(f.Store.InspectRecovery(W, Run)).Attempts.First(item => item.Attempt == A1).State);
    }

    [Fact]
    public void Abandonment_allows_approval_but_retains_affected_task_ownership()
    {
        using var f = new RunFixtures();
        f.Approve();
        var old = f.Reserve();
        f.Claim(old);
        Assert.IsType<RunDecision.Recorded>(f.Store.Abandon(f.Permit, f.Op(), f.Op(), "Administrative closure."));
        f.Approve(run: OtherRun);
        f.Release(T);
        var next = f.Reserve(run: OtherRun);
        f.Prepare(next, OtherRun);
        using var otherPermit = f.PermitFor(OtherRun);
        f.Release(T, OtherRun);
        using var otherLease = Assert.IsType<LeaseTake.Taken>(otherPermit.TakeTask(T)).Lease;
        Assert.Equal(RunPhase.Abandoned, f.Read().Phase);
        Assert.Equal(RunPhase.Approved, f.Read(OtherRun).Phase);
        Assert.Equal(RunProblem.UnresolvedOwnership, Problem(f.Store.Claim(otherLease, f.Op(), new(next.Attempt.Id, 1), next.Inputs, Prompt)));
        Assert.Equal([new LaunchKey(A1, 1)], f.Read().UnresolvedClaims.ToArray());
        otherLease.Dispose();
        Assert.IsType<RunDecision.Recorded>(f.Store.Recover(f.Lease(T), f.Op(), A1, RecoveryOutcome.Stopped, f.Op(), "Owner stopped."));
        f.Release(T);
        using var retaken = Assert.IsType<LeaseTake.Taken>(otherPermit.TakeTask(T)).Lease;
        Assert.Equal(new LaunchKey(new(Id(104)), 1), Assert.IsType<RunDecision.Granted>(f.Store.Claim(retaken, f.Op(), new(next.Attempt.Id, 1), next.Inputs, Prompt)).Claim.Key);
    }

    [Fact]
    public void Client_success_without_result_acceptance_does_not_satisfy_a_dependency()
    {
        using var f = new RunFixtures(Connect(FixtureWorkflow(Task(), Task(U)), T, U));
        f.Approve();
        var reservation = f.Reserve();
        f.Claim(reservation);
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), A1, TerminalAttemptOutcome.Succeeded, f.WriteLog(reservation)));
        var rejected = Assert.IsType<RunDecision.Rejected>(f.Store.Reserve(f.Lease(U), f.Op(), f.Read().Revision.Id, new AttemptCause.Initial()));
        Assert.Equal(new RunRejection(RunProblem.MissingDependencyResult, Task: T), rejected.Reason);
        Assert.Equal([A1], f.Read().Attempts.Keys);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Waiting_and_intermediate_review_checkpoints_cannot_be_terminal_success(bool reviewing)
    {
        var task = reviewing ? Task(work: new WorkSpec.Review(PromptTemplate.Parse("{{brief}}"), PromptTemplate.Parse("{{brief}}"))) : Task() with
        {
            Conversation = ConversationMode.Chat
        };
        using var f = new RunFixtures(FixtureWorkflow(task));
        f.Approve();
        var reservation = f.Reserve();
        f.Claim(reservation);
        var checkpoint = f.WriteLog(reservation, subject: reviewing ? U : null);
        Assert.Equal(RunProblem.OutcomeMismatch, Problem(f.Store.CloseAttempt(f.Permit, f.Op(), A1, TerminalAttemptOutcome.Succeeded, checkpoint)));
        Assert.Equal(RecoveryState.Uncertain, Assert.Single(Assert.IsType<RecoveryRead.Loaded>(f.Store.InspectRecovery(W, Run)).Attempts).State);
    }

    [Fact]
    public void Missing_context_remains_in_the_captured_input_after_context_acceptance()
    {
        using var f = new RunFixtures(Connect(FixtureWorkflow(Task(), Task(U)), U, T, ConnectionKind.Context));
        f.Approve();
        var dependent = f.Reserve();
        f.Complete(f.Reserve(U));
        var captured = f.NewStore().Read(W, Run) as RunRead.Loaded;
        Assert.Equal([new InputBinding.MissingContext(new(U, T))], captured!.Record.Inputs[dependent.Inputs.Id].Bindings.ToArray());
        Assert.Equal("Checked.", captured.Record.CurrentResults[U].Report);
        Assert.Equal(A1, captured.Record.Attempts[dependent.Attempt.Id].Id);
    }

    [Fact]
    public void Duplicate_B_and_C_acceptance_and_D_reservation_keep_one_join_input()
    {
        var workflow = Connect(Connect(FixtureWorkflow(Task(), Task(C), Task(D)), T, D), C, D);
        using var f = new RunFixtures(workflow);
        f.Approve();
        var b = f.Reserve();
        var rb = f.Complete(b);
        var c = f.Reserve(C);
        var rc = f.Complete(c);
        Assert.IsType<RunDecision.Existing>(f.Store.AcceptReport(f.Permit, f.Op(), b.Attempt.Id, b.Inputs.Id, "Checked."));
        Assert.IsType<RunDecision.Existing>(f.Store.AcceptReport(f.Permit, f.Op(), c.Attempt.Id, c.Inputs.Id, "Checked."));
        var d = f.Reserve(D);
        Assert.IsType<RunDecision.Existing>(f.NewStore().Reserve(f.Lease(D), f.Op(), f.Read().Revision.Id, new AttemptCause.Initial()));
        Assert.Equal([new ResultId(Id(103)), new ResultId(Id(106))], f.Read().Results.Select(result => result.Id));
        Assert.Equal([new ResultId(Id(103)), new ResultId(Id(106))], d.Inputs.Bindings.OfType<InputBinding.Provided>().Select(binding =>
            binding.Result));
        Assert.Equal([new AttemptId(Id(108))], f.Read().Attempts.Values.Where(attempt => attempt.Task == D).Select(attempt => attempt.Id));
        Assert.Equal("Checked.", rb.Report);
        Assert.Equal("Checked.", rc.Report);
    }

    [Fact]
    public void Person_amendment_configures_an_unstarted_task_and_keeps_the_started_revision()
    {
        using var f = new RunFixtures();
        f.Approve();
        f.Reserve();
        var candidate = FixtureWorkflow(Task(), Task(U, model: "m2"));
        Assert.IsType<RunDecision.Recorded>(f.Store.Amend(f.Permit, f.Op(), new(V1), Revision.Capture(candidate),
            new AmendmentOrigin.Person(), f.Op()));
        var reopened = f.Read();
        Assert.Equal("m2", reopened.Revision.Snapshot.Tasks[U].Execution!.Model);
        Assert.Equal(V1, reopened.Attempts[A1].Revision.Sha256);
        Assert.Equal("m1", reopened.Revisions[new(V1)].Snapshot.Tasks[T].Execution!.Model);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Changing_a_reserved_definition_or_its_incoming_edge_rejects(bool edge)
    {
        using var f = new RunFixtures();
        f.Approve();
        f.Reserve();
        var candidate = edge ? Connect(FixtureWorkflow(Task(), Task(U)), U, T, ConnectionKind.Context) : FixtureWorkflow(Task(model: "m2"));
        Assert.Equal(RunProblem.StartedTaskChanged, Problem(f.Store.Amend(f.Permit, f.Op(), new(V1), Revision.Capture(candidate),
            new AmendmentOrigin.Person(), f.Op())));
        Assert.Equal(V1, f.Read().Revision.Id.Sha256);
    }

    [Fact]
    public void An_obsolete_amendment_revision_rejects()
    {
        using var f = new RunFixtures();
        f.Approve();
        var candidate = Revision.Capture(FixtureWorkflow(Task(model: "m2")));
        Assert.IsType<RunDecision.Recorded>(f.Store.Amend(f.Permit, f.Op(), new(V1), candidate, new AmendmentOrigin.Person(), f.Op()));
        Assert.Equal(RunProblem.RevisionConflict, Problem(f.Store.Amend(f.Permit, f.Op(), new(V1), Revision.Capture(FixtureWorkflow()),
            new AmendmentOrigin.Person(), f.Op())));
        Assert.Equal(V2, f.Read().Revision.Id.Sha256);
    }

    [Fact]
    public void Superseding_a_captured_result_keeps_the_attempt_and_marks_its_output_stale()
    {
        var workflow = Connect(Connect(FixtureWorkflow(Task(), Task(D), Task(U)), T, D), D, U);
        using var f = new RunFixtures(workflow);
        f.Approve();
        var b = f.Reserve();
        var rb = f.Complete(b);
        var d = f.Reserve(D);
        f.Claim(d);
        var b2 = f.Reserve(cause: new AttemptCause.Continue(b.Attempt.Id, f.Op()));
        f.Complete(b2, "Updated.", rb.Id);
        Assert.True(f.Read().IsStale(d.Inputs.Id));
        Assert.Equal([new ResultId(Id(103))], f.Read().Inputs[d.Inputs.Id].Bindings.OfType<InputBinding.Provided>().Select(binding =>
            binding.Result));
        Assert.Equal(new AttemptId(Id(105)), d.Attempt.Id);
        var checkpoint = f.WriteLog(d);
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), d.Attempt.Id, TerminalAttemptOutcome.Succeeded, checkpoint));
        var result = Assert.IsType<RunEvent.ResultAccepted>(Assert.IsType<RunDecision.Created>(f.Store.AcceptReport(f.Permit, f.Op(),
            d.Attempt.Id, d.Inputs.Id, "Checked.")).Event).Result;
        Assert.Equal(new ResultId(Id(109)), result.Id);
        Assert.Contains(new ResultId(Id(109)), f.Read().StaleResults);
        Assert.Equal(RunProblem.StaleInput, Problem(f.Store.Reserve(f.Lease(U), f.Op(), f.Read().Revision.Id, new AttemptCause.Initial())));
    }

    [Fact]
    public void Stop_rejects_new_claims()
    {
        using var f = new RunFixtures();
        f.Approve();
        var reservation = f.Reserve();
        Assert.IsType<RunDecision.Recorded>(f.Store.Stop(f.Permit, f.Op()));
        Assert.Equal(RunProblem.RunStopped, Problem(f.Store.Claim(f.Lease(T), f.Op(), new(A1, 1), reservation.Inputs, Prompt)));
        Assert.Equal(RunPhase.StopRequested, f.Read().Phase);
    }

    [Fact]
    public void Identical_ids_under_two_project_folders_keep_separate_reports()
    {
        using var first = new RunFixtures();
        using var second = new RunFixtures();
        first.Approve();
        first.Complete(first.Reserve(), "one");
        second.Approve();
        second.Complete(second.Reserve(), "two");
        Assert.Equal("one", first.NewStore().Read(W, Run) is RunRead.Loaded one ? one.Record.CurrentResults[T].Report : "missing");
        Assert.Equal("two", second.NewStore().Read(W, Run) is RunRead.Loaded two ? two.Record.CurrentResults[T].Report : "missing");
    }

    [Theory]
    [InlineData("length")]
    [InlineData("digest")]
    [InlineData("torn")]
    [InlineData("unknown")]
    [InlineData("afterClosure")]
    public void Strict_checkpoints_reject_length_digest_and_malformed_logs(string damage)
    {
        using var f = new RunFixtures();
        f.Approve();
        var reservation = f.Reserve();
        f.Claim(reservation);
        var checkpoint = f.WriteLog(reservation);
        var path = Path.Combine(f.Store.AttemptFolder(W, Run, T, A1), "events.jsonl");
        checkpoint = damage switch
        {
            "length" => checkpoint with { ByteLength = checkpoint.ByteLength - 1 },
            "digest" => checkpoint with { Content = new(new string('0', 64)) },
            _ => checkpoint,
        };
        if (damage is "torn" or "unknown" or "afterClosure")
        {
            File.AppendAllText(path, damage switch
            {
                "torn" => "{\"type\":",
                "unknown" => "{\"type\":\"future\",\"at\":\"2026-10-07T00:00:00Z\"}\n",
                _ => "{\"type\":\"turnRequested\",\"at\":\"2026-10-07T00:00:00Z\",\"prompt\":\"Inspect\",\"command\":\"codex\",\"arguments\":[]}\n",
            });
            checkpoint = Checkpoint(Path.GetDirectoryName(path)!);
        }
        Assert.Equal(RunProblem.EvidenceMismatch, Problem(f.Store.CloseAttempt(f.Permit, f.Op(), A1, TerminalAttemptOutcome.Succeeded, checkpoint)));
        Assert.Equal(RecoveryState.Uncertain, Assert.Single(Assert.IsType<RecoveryRead.Loaded>(f.Store.InspectRecovery(W, Run)).Attempts).State);
    }

    [Fact]
    public void Recovery_retry_amendment_and_abandonment_require_explicit_confirmation()
    {
        using var f = new RunFixtures();
        f.Approve();
        var reservation = f.Reserve();
        f.Claim(reservation);
        Assert.Equal(RunProblem.ConfirmationRequired, Problem(f.Store.Recover(f.Lease(T), f.Op(), A1, RecoveryOutcome.Stopped, default(OperationId), "Stopped.")));
        Assert.Equal(RunProblem.ConfirmationRequired, Problem(f.Store.Recover(f.Lease(T), f.Op(), A1, RecoveryOutcome.Stopped, f.Op(), " ")));
        Assert.Equal(RunProblem.ConfirmationRequired, Problem(f.Store.Amend(f.Permit, f.Op(), new(V1), Revision.Capture(FixtureWorkflow()),
            new AmendmentOrigin.Person(), default)));
        Assert.Equal(RunProblem.ConfirmationRequired, Problem(f.Store.Abandon(f.Permit, f.Op(), default, "Close.")));
        Assert.IsType<RunDecision.Recorded>(f.Store.Recover(f.Lease(T), f.Op(), A1, RecoveryOutcome.Stopped, f.Op(), "Stopped."));
        Assert.Equal(RunProblem.ConfirmationRequired, Problem(f.Store.Reserve(f.Lease(T), f.Op(), new(V1), new AttemptCause.Retry(A1,
            default))));
        Assert.Equal(8, f.Read().Sequence);
    }

    [Fact]
    public void Inputs_are_immutable_and_turn_one_must_use_the_reservation_inputs()
    {
        using var f = new RunFixtures();
        f.Approve();
        var reservation = f.Reserve();
        Assert.Equal(RunProblem.InputConflict, Problem(f.Store.Claim(f.Lease(T), f.Op(), new(A1, 1), reservation.Inputs with
        {
            Text = "different"
        }, Prompt)));
        Assert.Equal(RunProblem.InputConflict, Problem(f.Store.Claim(f.Lease(T), f.Op(), new(A1, 1), reservation.Inputs with
        {
            Id = new(Id(999))
        }, Prompt)));
        f.Claim(reservation);
        Assert.Equal("", f.Read().Inputs[new(Id(101))].Text);
        Assert.Equal([new LaunchKey(A1, 1)], f.Read().Claims.Keys);
    }

    [Fact]
    public void Settlement_requires_terminal_closures_and_current_results()
    {
        using var f = new RunFixtures();
        f.Approve();
        var reservation = f.Reserve();
        Assert.Equal(RunProblem.UnclosedAttempts, Problem(f.Store.Settle(f.Permit, f.Op(), RunOutcome.Completed)));
        f.Claim(reservation);
        var evidence = f.WriteLog(reservation);
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), A1, TerminalAttemptOutcome.Succeeded, evidence));
        Assert.Equal(RunProblem.IncompleteResults, Problem(f.Store.Settle(f.Permit, f.Op(), RunOutcome.Completed)));
        Assert.IsType<RunDecision.Created>(f.Store.AcceptReport(f.Permit, f.Op(), A1, reservation.Inputs.Id, "Checked."));
        Assert.Equal(RunPhase.Completed, Assert.IsType<RunDecision.Recorded>(f.Store.Settle(f.Permit, f.Op(), RunOutcome.Completed)).Record.Phase);
    }

    [Fact]
    public void Stale_context_is_missing_while_stale_dependency_rejects()
    {
        var workflow = Connect(Connect(Connect(FixtureWorkflow(Task(), Task(U), Task(C), Task(D)), T, U),
            U, C, ConnectionKind.Context), U, D);
        using var f = new RunFixtures(workflow);
        f.Approve();
        var first = f.Reserve();
        var r1 = f.Complete(first);
        f.Complete(f.Reserve(U));
        f.Complete(f.Reserve(cause: new AttemptCause.Retry(A1, f.Op())), "Updated.", r1.Id);
        var context = Assert.IsType<RunDecision.Created>(f.Store.Reserve(f.Lease(C), f.Op(), f.Read().Revision.Id,
            new AttemptCause.Initial()));
        Assert.Equal<InputBinding>([new InputBinding.MissingContext(new(U, C))],
            Assert.IsType<RunEvent.Reserved>(context.Event).Inputs.Bindings);
        Assert.Equal(new RunRejection(RunProblem.StaleInput, Task: U),
            Assert.IsType<RunDecision.Rejected>(f.Store.Reserve(f.Lease(D), f.Op(), f.Read().Revision.Id,
                new AttemptCause.Initial())).Reason);
    }
}
