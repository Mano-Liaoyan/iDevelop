using System.Text;
using IDevelop.Execution;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Runs;

public sealed class JournalAccessTests
{
    [Fact]
    public void Own_torn_first_line_allows_approval_and_idempotent_retry()
    {
        using var f = new RunFixtures();
        File.WriteAllText(f.Journal(W, Run), "{\"schema\":1");
        var operation = f.Op();
        var revision = Revision.Capture(f.Workflow);
        var codeBase = new RunBase(Base, BaseChoice.Head);
        Assert.IsType<RunDecision.Created>(f.Store.Approve(W, Run, operation, revision, codeBase));
        var record = Assert.IsType<RunRead.Loaded>(f.Store.Read(W, Run)).Record;
        Assert.Equal(RunPhase.Approved, record.Phase);
        Assert.Equal(1, record.Sequence);
        Assert.IsType<RunDecision.Existing>(f.Store.Approve(W, Run, operation, revision, codeBase));
    }

    [Fact]
    public void Empty_journals_allow_approval_and_reservation_retries()
    {
        using var f = new RunFixtures();
        File.WriteAllBytes(f.Journal(W, Run), []);
        Assert.IsType<RunDecision.Created>(f.Store.Approve(W, Run, f.Op(), Revision.Capture(f.Workflow), new(Base, BaseChoice.Head)));
        Assert.Equal(RunPhase.Approved, f.Read().Phase);
        File.WriteAllBytes(f.Journal(W, OtherRun), []);
        Assert.Equal(A1, Assert.IsType<RunEvent.Reserved>(Assert.IsType<RunDecision.Created>(
            f.Store.Reserve(W, Run, f.Op(), T, new(V1), new AttemptCause.Initial(), Base, "")).Event).Attempt.Id);
    }

    [Fact]
    public void Another_workflows_torn_journal_does_not_block_approval()
    {
        using var f = new RunFixtures();
        var otherWorkflow = new WorkflowId(Id(20));
        File.WriteAllText(f.Journal(otherWorkflow, OtherRun), "{\"schema\":1");
        Assert.IsType<RunDecision.Created>(f.Store.Approve(W, Run, f.Op(), Revision.Capture(f.Workflow), new(Base, BaseChoice.Head)));
        Assert.Equal(RunPhase.Approved, f.Read().Phase);
    }

    [Fact]
    public void Another_runs_torn_journal_preserves_its_approved_prefix()
    {
        using var f = new RunFixtures();
        f.Approve(run: OtherRun);
        File.AppendAllText(f.Journal(W, OtherRun), "{\"schema\":1");
        Assert.Equal(RunProblem.RunBusy,
            Problem(f.Store.Approve(W, Run, f.Op(), Revision.Capture(f.Workflow), new(Base, BaseChoice.Head))));
        Assert.Equal(RunPhase.Approved, Assert.IsType<RunRead.Rejected>(f.Store.Read(W, OtherRun)).Prefix!.Phase);
    }

    [Fact]
    public void Torn_multibyte_tail_is_IncompleteTail()
    {
        using var f = new RunFixtures();
        f.Approve();
        var path = f.Journal(W, Run);
        using (var stream = new FileStream(path, FileMode.Append))
        {
            stream.Write(Encoding.UTF8.GetBytes("{\"reason\":\""));
            stream.WriteByte(0xe2);
            stream.WriteByte(0x82);
        }
        var read = Assert.IsType<RunRead.Rejected>(f.Store.Read(W, Run));
        Assert.Equal(new RunRejection(RunProblem.IncompleteTail, 2), read.Reason);
        Assert.Equal(RunPhase.Approved, read.Prefix!.Phase);
    }

    [Fact]
    public void Invalid_UTF8_in_a_complete_line_is_InvalidData()
    {
        using var f = new RunFixtures();
        f.Approve();
        using (var stream = new FileStream(f.Journal(W, Run), FileMode.Append))
        {
            stream.Write([0xff, 0x0a]);
        }
        Assert.Equal(new RunRejection(RunProblem.InvalidData, 2),
            Assert.IsType<RunRead.Rejected>(f.Store.Read(W, Run)).Reason);
    }

    [Fact]
    public void Sixteen_threads_reserve_one_initial_attempt()
    {
        using var f = new RunFixtures();
        f.Approve();
        var decisions = Race(f, (store, op) => store.Reserve(W, Run, op, T, new(V1), new AttemptCause.Initial(), Base, ""));
        Assert.Equal((1, 15, 0), (decisions.Count(d => d is RunDecision.Created), decisions.Count(d => d is RunDecision.Existing),
            decisions.Count(d => d is not (RunDecision.Created or RunDecision.Existing))));
        Assert.Equal([A1], f.Read().Attempts.Keys);
        Assert.Equal(2, f.Read().Sequence);
    }

    [Fact]
    public void Sixteen_threads_claim_one_launch_grant()
    {
        using var f = new RunFixtures();
        f.Approve();
        var reservation = f.Reserve();
        var decisions = Race(f, (store, op) => store.Claim(W, Run, op, new(A1, 1), reservation.Inputs, Prompt));
        Assert.Equal((1, 15, 0), (decisions.Count(d => d is RunDecision.Granted), decisions.Count(d => d is RunDecision.Existing),
            decisions.Count(d => d is not (RunDecision.Granted or RunDecision.Existing))));
        Assert.Equal([new LaunchKey(A1, 1)], f.Read().Claims.Keys);
        Assert.Equal(3, f.Read().Sequence);
    }

    [Fact]
    public void Foreign_journal_lock_rejects_then_allows_the_same_reservation()
    {
        using var f = new RunFixtures();
        f.Approve();
        var op = f.Op();
        using (var held = new FileStream(Path.Combine(f.Project, ".idp", "runs", "write.lock"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(RunProblem.JournalBusy,
                Problem(f.Store.Reserve(W, Run, op, T, new(V1), new AttemptCause.Initial(), Base, "")));
        }
        var created = Assert.IsType<RunDecision.Created>(f.Store.Reserve(W, Run, op, T, new(V1), new AttemptCause.Initial(), Base, ""));
        Assert.Equal(A1, Assert.IsType<RunEvent.Reserved>(created.Event).Attempt.Id);
    }

    [Fact]
    public void Another_runs_torn_first_line_counts_as_absent()
    {
        using var f = new RunFixtures();
        f.Approve();
        File.WriteAllBytes(f.Journal(W, OtherRun), [0xe2, 0x82]);
        Assert.Equal(A1, Assert.IsType<RunEvent.Reserved>(Assert.IsType<RunDecision.Created>(
            f.Store.Reserve(W, Run, f.Op(), T, new(V1), new AttemptCause.Initial(), Base, "")).Event).Attempt.Id);
    }

    [Fact]
    public void Torn_tail_does_not_hide_another_runs_invalid_complete_prefix()
    {
        using var f = new RunFixtures();
        f.Approve();
        File.WriteAllText(f.Journal(W, OtherRun), File.ReadAllText(f.Journal(W, Run)) + "{\"schema\":1");
        Assert.Equal(RunProblem.IdentityMismatch,
            Problem(f.Store.Reserve(W, Run, f.Op(), T, new(V1), new AttemptCause.Initial(), Base, "")));
    }

    private static RunDecision[] Race(RunFixtures f, Func<RunStore, OperationId, RunDecision> command)
    {
        using var barrier = new Barrier(16);
        var workers = Enumerable.Range(0, 16).Select(_ => (Store: f.NewStore(), Operation: f.Op())).ToArray();
        var tasks = workers.Select(worker => System.Threading.Tasks.Task.Factory.StartNew(() =>
        {
            barrier.SignalAndWait();
            return command(worker.Store, worker.Operation);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        return System.Threading.Tasks.Task.WhenAll(tasks).GetAwaiter().GetResult();
    }
}
