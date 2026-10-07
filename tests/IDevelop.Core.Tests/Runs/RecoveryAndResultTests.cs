using System.Collections.Immutable;
using System.Diagnostics;
using IDevelop.Execution;
using IDevelop.Nodes;
using IDevelop.Projects;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Runs;

[Collection(ProcessCollection.Name)]
public sealed class RecoveryAndResultTests
{
    [Fact]
    public async System.Threading.Tasks.Task Recovery_is_TaskBusy_while_another_process_holds_the_task_lock()
    {
        using var f = new RunFixtures();
        f.Approve();
        var reservation = f.Reserve();
        f.Claim(reservation);
        var lease = f.Lease(T);
        f.Release(T);
        var release = Path.Combine(f.Project, "release");
        var fakes = new FakeClients(Path.Combine(f.Project, "fakes"));
        Directory.CreateDirectory(AttemptLog.TaskFolder(DataFolder.Attempts(f.Project), T));
        var shim = fakes.Install("holder", FakeRule.On()
            .LockFile(Path.Combine(AttemptLog.TaskFolder(DataFolder.Attempts(f.Project), T), "run.lock"))
            .Print("locked").WaitForFile(release));
        using var process = Process.Start(new ProcessStartInfo(shim)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        })!;
        try
        {
            Assert.Equal("locked", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(RunProblem.TaskBusy, Problem(f.Store.Recover(W, Run, lease, f.Op(), A1, RecoveryOutcome.Stopped, f.Op(), "Stopped.")));
            Assert.Equal(RecoveryState.Uncertain, Assert.Single(Assert.IsType<RecoveryRead.Loaded>(f.Store.InspectRecovery(W, Run)).Attempts).State);
        }
        finally
        {
            File.WriteAllText(release, "release");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.IsType<RunDecision.Recorded>(f.Store.Recover(W, Run, f.Lease(T), f.Op(), A1, RecoveryOutcome.Stopped, f.Op(), "Stopped."));
        Assert.Equal(RecoveryState.Closed, Assert.Single(Assert.IsType<RecoveryRead.Loaded>(f.Store.InspectRecovery(W, Run)).Attempts).State);
    }

    [Fact]
    public void Logged_terminal_evidence_recovers_without_machine_inference()
    {
        using var f = new RunFixtures();
        f.Approve();
        var reservation = f.Reserve();
        f.Claim(reservation);
        f.WriteLog(reservation);
        var recovered = Assert.IsType<RunDecision.Recorded>(f.Store.Recover(W, Run, f.Lease(T), f.Op(), A1));
        Assert.Equal(TerminalAttemptOutcome.Succeeded, Assert.IsType<AttemptEnd.Logged>(recovered.Record.Closures[A1]).Outcome);
        Assert.Equal(RecoveryState.Closed, Assert.Single(Assert.IsType<RecoveryRead.Loaded>(f.Store.InspectRecovery(W, Run)).Attempts).State);
        Assert.Equal(8, f.Read().Sequence);
    }

    [Fact]
    public void Abandoned_ownership_is_released_by_logged_terminal_evidence()
    {
        using var f = new RunFixtures();
        f.Approve();
        var reservation = f.Reserve();
        f.Claim(reservation);
        Assert.Equal(A1, reservation.Attempt.Id);
        Assert.IsType<RunDecision.Recorded>(f.Store.Abandon(W, Run, f.Op(), f.Op(), "Administrative closure."));
        f.WriteLog(reservation);

        var recovered = Assert.IsType<RunDecision.Recorded>(f.Store.Recover(W, Run, f.Lease(T), f.Op(), A1));
        Assert.Equal(TerminalAttemptOutcome.Succeeded, Assert.IsType<AttemptEnd.Logged>(recovered.Record.Closures[A1]).Outcome);
        Assert.Equal(RunPhase.Abandoned, recovered.Record.Phase);
        Assert.Equal(RecoveryState.Closed, Assert.Single(Assert.IsType<RecoveryRead.Loaded>(f.Store.InspectRecovery(W, Run)).Attempts).State);

        f.Approve(run: OtherRun);
        var next = f.Reserve(run: OtherRun);
        f.Prepare(next, OtherRun);
        using var otherPermit = Assert.IsType<ControlTake.Owned>(RunStore.Open(f.Project).TakeControl(W, OtherRun)).Permit;
        f.Release(T);
        using var otherLease = Assert.IsType<LeaseTake.Taken>(otherPermit.TakeTask(T)).Lease;
        var granted = Assert.IsType<RunDecision.Granted>(f.Store.Claim(otherLease, f.Op(), new(next.Attempt.Id, 1), next.Inputs, Prompt));
        Assert.Equal(new LaunchKey(new(Id(104)), 1), granted.Claim.Key);
        Assert.Equal(RunPhase.Approved, granted.Record.Phase);
    }

    [Fact]
    public void Abandoned_ownership_is_released_when_the_claimed_turn_closes()
    {
        using var f = new RunFixtures(FixtureWorkflow(Task() with { Conversation = ConversationMode.Chat }));
        f.Approve();
        var reservation = f.Reserve();
        f.Claim(reservation);
        Assert.IsType<RunDecision.Recorded>(f.Store.Abandon(W, Run, f.Op(), f.Op(), "Administrative closure."));
        var evidence = f.WriteLog(reservation);

        var closed = Assert.IsType<RunDecision.Recorded>(f.Store.CloseTurn(W, Run, f.Op(), new(A1, 1), evidence));
        Assert.Equal(RunPhase.Abandoned, closed.Record.Phase);
        Assert.Equal([new LaunchKey(A1, 1)], closed.Record.TurnClosures.Keys);
        Assert.Equal(9, closed.Record.Sequence);

        f.Approve(run: OtherRun);
        var next = f.Reserve(run: OtherRun);
        f.Prepare(next, OtherRun);
        using var otherPermit = Assert.IsType<ControlTake.Owned>(RunStore.Open(f.Project).TakeControl(W, OtherRun)).Permit;
        f.Release(T);
        using var otherLease = Assert.IsType<LeaseTake.Taken>(otherPermit.TakeTask(T)).Lease;
        var granted = Assert.IsType<RunDecision.Granted>(f.Store.Claim(otherLease, f.Op(), new(next.Attempt.Id, 1), next.Inputs, Prompt));
        Assert.Equal(new LaunchKey(new(Id(104)), 1), granted.Claim.Key);
        Assert.Equal(RunPhase.Approved, granted.Record.Phase);
    }

    [Fact]
    public void Recovery_never_creates_a_successful_result_and_retry_confirmation_is_separate()
    {
        using var f = new RunFixtures();
        f.Approve();
        var reservation = f.Reserve();
        f.Claim(reservation);
        var decision = Assert.IsType<RunDecision.Recorded>(f.Store.Recover(W, Run, f.Lease(T), f.Op(), A1, RecoveryOutcome.NotStarted, f.Op(), "Launcher cannot continue."));
        Assert.Equal(RecoveryOutcome.NotStarted, Assert.IsType<AttemptEnd.Recovered>(decision.Record.Closures[A1]).Outcome);
        Assert.Equal(RunProblem.OutcomeMismatch, Problem(f.Store.AcceptReport(W, Run, f.Op(), A1, reservation.Inputs.Id, "Checked.")));
        Assert.Equal(RunProblem.IncompleteResults, Problem(f.Store.Settle(W, Run, f.Op(), RunOutcome.Completed)));
    }

    [Fact]
    public void Writer_reports_are_unavailable_in_E1()
    {
        var work = new WorkSpec.Agent(AgentAccess.Edit, false, PromptTemplate.Parse("{{brief}}"));
        using var f = new RunFixtures(FixtureWorkflow(Task(work: work)));
        f.Approve();
        var reservation = f.Reserve();
        f.Claim(reservation);
        var evidence = f.WriteLog(reservation);
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(W, Run, f.Op(), A1, TerminalAttemptOutcome.Succeeded, evidence));
        Assert.Equal(RunProblem.UnsupportedResult, Problem(f.Store.AcceptReport(W, Run, f.Op(), A1, reservation.Inputs.Id, "Checked.")));
        Assert.Equal(8, f.Read().Sequence);
    }

    [Fact]
    public void A_read_only_planner_report_can_be_accepted_after_logged_terminal_success()
    {
        using var f = new RunFixtures(FixtureWorkflow(Task(work: new WorkSpec.Agent(AgentAccess.ReadOnly, true, PromptTemplate.Parse("{{brief}}")))));
        f.Approve();
        var reservation = f.Reserve();
        var result = f.Complete(reservation);
        Assert.Equal(new ResultId(Id(103)), result.Id);
        Assert.Equal("Checked.", result.Report);
        Assert.Equal(new ResultOrigin.Executed(A1), result.Origin);
        Assert.Equal(9, f.Read().Sequence);
    }

    [Fact]
    public void Planner_amendments_accept_the_exact_logged_proposal_through_Workflow_Apply()
    {
        var plannerBlueprint = new Blueprint(new("example.planner", 1), "Planner", new WorkSpec.Agent(AgentAccess.ReadOnly, true,
            PromptTemplate.Parse("{{brief}}")),
            [new("brief", "Brief", FieldShape.Text, true, "Inspect")], new(Task().Execution, ConversationMode.Autonomous));
        var workflow = FixtureWorkflow(new TaskDefinition(T, plannerBlueprint) { Title = "Plan" }, Task(U).WithField("brief", "")!);
        using var f = new RunFixtures(workflow);
        f.Approve();
        var reservation = f.Reserve();
        f.Claim(reservation);
        var folder = f.Store.AttemptFolder(W, Run, T, A1);
        const string report = "```idevelop\n{\"status\":\"proposal\",\"fill\":[{\"slot\":\"slot-1\",\"fields\":{\"brief\":\"Reviewed\"}}]}\n```";
        using (var log = AttemptLog.Create(Path.GetDirectoryName(Path.GetDirectoryName(folder))!, new AttemptEvent.Requested(At, A1, T,
            "Plan", Task().Execution!, "Inspect", "codex", [])
        {
            RunBinding = new(W, Run, reservation.Attempt.Revision, reservation.Inputs.Id),
            Conversation = ConversationMode.Autonomous,
            ReadOnly = true,
            Planning = new(Id(700), [U], []),
        }))
        {
            log.Append(new AttemptEvent.Agent(At, new AgentEvent.Succeeded(report)));
            log.Append(new AttemptEvent.Exited(At, 0, ""));
        }
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseTurn(W, Run, f.Op(), new(A1, 1), Checkpoint(folder)));
        var proposal = new Proposal(T, A1, 1, [new(U, null, ImmutableSortedDictionary<string, string>.Empty.Add("brief", "Reviewed"))], [], []);
        var decision = f.Store.AmendFromProposal(W, Run, f.Op(), reservation.Attempt.Revision, proposal, new HashSet<TaskId> { U }, f.Op());
        Assert.Equal("Reviewed", Assert.IsType<RunDecision.Recorded>(decision).Record.Revision.Snapshot.Tasks[U].Field("brief"));
        Assert.Equal("Inspect", f.Read().Revision.Snapshot.Tasks[T].Field("brief"));
        var amended = Assert.Single(f.Read().Receipts.Values.Select(entry => entry.Event).OfType<RunEvent.Amended>());
        Assert.Equal(new AmendmentOrigin.Planner(A1, 1), amended.Origin);
        Assert.Equal(9, f.Read().Sequence);
    }

    [Fact]
    public void A_logged_failure_can_be_continued_but_recovered_ownership_requires_a_retry()
    {
        using var f = new RunFixtures();
        f.Approve();
        var reservation = f.Reserve();
        f.Claim(reservation);
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(W, Run, f.Op(), A1, TerminalAttemptOutcome.Failed,
            f.WriteLog(reservation, TerminalAttemptOutcome.Failed)));
        var continuation = f.Reserve(cause: new AttemptCause.Continue(A1, f.Op()));
        Assert.Equal(new AttemptId(Id(104)), continuation.Attempt.Id);
        Assert.Equal(A1, Assert.IsType<AttemptCause.Continue>(continuation.Attempt.Cause).Previous);
        f.Claim(continuation);
        Assert.IsType<RunDecision.Recorded>(f.Store.Recover(W, Run,
            f.Lease(continuation.Attempt.Task), f.Op(), continuation.Attempt.Id, RecoveryOutcome.Stopped, f.Op(), "Stopped."));
        Assert.Equal(RunProblem.OutcomeMismatch, Problem(f.Store.Reserve(W, Run, f.Op(), T, new(V1),
            new AttemptCause.Continue(continuation.Attempt.Id, f.Op()))));
    }

    [Fact]
    public void Person_work_cannot_be_reserved()
    {
        using var f = new RunFixtures(FixtureWorkflow(Task(work: new WorkSpec.Person())));
        f.Approve();
        Assert.Equal(RunProblem.UnsupportedWork, Problem(f.Store.Reserve(W, Run, f.Op(), T, f.Read().Revision.Id,
            new AttemptCause.Initial())));
        Assert.Equal(1, f.Read().Sequence);
    }

    [Fact]
    public void Invalid_configuration_is_rejected_before_allocating_a_reservation()
    {
        using var f = new RunFixtures(FixtureWorkflow(Task() with
        {
            Execution = null
        }));
        f.Approve();
        var rejected = Assert.IsType<RunDecision.Rejected>(f.Store.Reserve(W, Run, f.Op(), T, f.Read().Revision.Id,
            new AttemptCause.Initial()));
        Assert.Equal(new RunRejection(RunProblem.TaskUnconfigured, Task: T), rejected.Reason);
        var candidate = Revision.Capture(FixtureWorkflow());
        Assert.IsType<RunDecision.Recorded>(f.Store.Amend(W, Run, f.Op(), f.Read().Revision.Id, candidate, new AmendmentOrigin.Person(), f.Op()));
        Assert.Equal(A1, f.Reserve().Attempt.Id);
        Assert.Equal(V1, f.Read().Revision.Id.Sha256);
    }

    [Fact]
    public void Reconciled_turn_cannot_release_ownership_after_abandonment()
    {
        using var f = new RunFixtures();
        f.Approve();
        var reservation = f.Reserve();
        f.Claim(reservation);
        var folder = f.Store.AttemptFolder(W, Run, T, A1);
        using (var log = AttemptLog.Create(Path.GetDirectoryName(Path.GetDirectoryName(folder))!,
            new AttemptEvent.Requested(At, A1, T, "Plan", Task().Execution!, "Inspect", "codex", [])
            {
                RunBinding = new(W, Run, new(V1), reservation.Inputs.Id),
                ReadOnly = true,
                Conversation = ConversationMode.Autonomous,
            }))
        {
            log.Append(new AttemptEvent.Reconciled(At, ProcessMatch.Gone));
        }
        Assert.Equal(RunProblem.RecoveryEvidenceInsufficient,
            Problem(f.Store.CloseTurn(W, Run, f.Op(), new(A1, 1), Checkpoint(folder))));
        Assert.IsType<RunDecision.Recorded>(f.Store.Abandon(W, Run, f.Op(), f.Op(), "Administrative closure."));
        f.Approve(run: OtherRun);
        var next = f.Reserve(run: OtherRun);
        f.Prepare(next, OtherRun);
        using var otherPermit = Assert.IsType<ControlTake.Owned>(RunStore.Open(f.Project).TakeControl(W, OtherRun)).Permit;
        f.Release(T);
        using var otherLease = Assert.IsType<LeaseTake.Taken>(otherPermit.TakeTask(T)).Lease;
        Assert.Equal(RunProblem.UnresolvedOwnership,
            Problem(f.Store.Claim(otherLease, f.Op(), new(next.Attempt.Id, 1), next.Inputs, Prompt)));
    }

    [Fact]
    public void Recovery_inspection_reports_a_sequence_gap()
    {
        using var f = new RunFixtures();
        f.Approve();
        File.AppendAllText(f.Journal(W, Run), """{"schema":1,"sequence":3,"event":{"type":"stopRequested"}}""" + "\n");
        Assert.Equal(new RunRejection(RunProblem.SequenceGap, 2),
            Assert.IsType<RecoveryRead.Rejected>(f.Store.InspectRecovery(W, Run)).Reason);
    }
}
