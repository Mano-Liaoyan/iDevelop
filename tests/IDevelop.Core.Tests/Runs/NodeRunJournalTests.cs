using IDevelop.Execution;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Runs;

/// <summary>
/// A run that a node's Run started, in the journal (#90), on hand-built runs without Git or clients. Only the node, the
/// nodes the person adds, and the tasks after them start, each once all of its dependency predecessors have results.
/// </summary>
public sealed class NodeRunJournalTests
{
    private static readonly TaskId G = new(Guid.Parse("00000000-0000-0000-0000-000000000006"));

    /// <summary><c>T → C ← U → D</c>: two roots, a join, and a task after the second root.</summary>
    private static Workflow Join() => Connect(Connect(Connect(FixtureWorkflow(Task(), Task(U), Task(C), Task(D)), T, C), U, C), U, D);

    private static RunDecision Reserve(RunFixtures f, TaskId task) =>
        f.Store.Reserve(f.Lease(task), f.Op(), f.Read().Revision.Id, new AttemptCause.Initial());

    private static RunRejection Refused(RunDecision decision) => Assert.IsType<RunDecision.Rejected>(decision).Reason;

    private static int Requests(RunFixtures f) => f.Read().Receipts.Values.Count(entry => entry.Event is RunEvent.Requested);

    [Fact]
    public void A_node_run_records_its_node_and_reserves_only_it_and_the_tasks_after_it()
    {
        using var f = new RunFixtures(Join());
        f.Approve(node: T);

        Assert.Equal([T], f.Read().Requested!);
        Assert.Equal([T, C], RunScope.InFlow(f.Read()).Order());
        Assert.Equal(new RunRejection(RunProblem.NotRequested), Refused(Reserve(f, U)));
        Assert.Equal(new RunRejection(RunProblem.NotRequested), Refused(Reserve(f, D)));
        f.Complete(f.Reserve(T));
        // The join is part of the run, and it waits for the root nobody ran.
        Assert.Equal(new RunRejection(RunProblem.MissingDependencyResult, Task: U), Refused(Reserve(f, C)) with { Sequence = 0 });
        Assert.Single(f.Read().Attempts);
    }

    [Fact]
    public void A_node_with_a_predecessor_cannot_start_a_run_without_that_predecessor_s_result()
    {
        using var f = new RunFixtures(Join());

        var refused = Refused(f.Store.Approve(W, Run, f.Op(), Revision.Capture(f.Workflow), new(Base, BaseChoice.Head), node: C));

        Assert.Equal((RunProblem.MissingDependencyResult, (TaskId?)T), (refused.Problem, refused.Task));
        Assert.Equal(new RunRejection(RunProblem.IdentityMismatch, Task: G) with { Sequence = 1 },
            Refused(f.Store.Approve(W, Run, f.Op(), Revision.Capture(f.Workflow), new(Base, BaseChoice.Head), node: G)));
        Assert.IsType<RunRead.Rejected>(f.Store.Read(W, Run));
    }

    [Fact]
    public void A_request_needs_each_predecessor_s_result_and_is_recorded_once()
    {
        using var f = new RunFixtures(Join());
        f.Approve(node: T);
        f.Complete(f.Reserve(T));

        Assert.Equal(new RunRejection(RunProblem.MissingDependencyResult, Task: U), Refused(f.Store.Request(f.Permit, f.Op(), D)));
        Assert.Equal(new RunRejection(RunProblem.IdentityMismatch, Task: G), Refused(f.Store.Request(f.Permit, f.Op(), G)));
        Assert.Equal(0, Requests(f));
        var operation = f.Op();
        Assert.IsType<RunDecision.Recorded>(f.Store.Request(f.Permit, operation, U));
        Assert.IsType<RunDecision.Existing>(f.Store.Request(f.Permit, operation, U));
        Assert.IsType<RunDecision.Existing>(f.Store.Request(f.Permit, f.Op(), U));
        // A task the run already starts once its predecessors finish needs no request.
        Assert.IsType<RunDecision.Existing>(f.Store.Request(f.Permit, f.Op(), C));

        Assert.Equal(1, Requests(f));
        Assert.Equal([T, U], f.Read().Requested!);
        Assert.Equal([T, U, C, D], RunScope.InFlow(f.Read()).OrderBy(task => task.Value.ToString()));
        f.Complete(f.Reserve(U));
        Assert.IsType<RunDecision.Created>(Reserve(f, C));
        Assert.IsType<RunDecision.Created>(Reserve(f, D));
    }

    [Fact]
    public void The_journal_rejects_a_request_it_could_not_have_granted()
    {
        using var f = new RunFixtures(Join());
        f.Approve(node: T);
        var record = f.Read();
        RunProblem Refusal(RunRecord record, RunEvent e) => Assert.IsType<RunRead.Rejected>(RunReducer.Apply(W, Run, record,
            new RunEntry(record.Schema, record.Sequence + 1, f.Op(), Prompt, At, e))).Reason.Problem;

        Assert.Equal(RunProblem.StartConflict, Refusal(record, new RunEvent.Requested(C)));
        Assert.Equal(RunProblem.MissingDependencyResult, Refusal(record, new RunEvent.Requested(D)));
        Assert.Equal(RunProblem.IdentityMismatch, Refusal(record, new RunEvent.Requested(G)));
        Assert.Equal(RunProblem.InvalidData, Refusal(record, new RunEvent.Requested(default)));
        Assert.IsType<RunDecision.Recorded>(f.Store.Request(f.Permit, f.Op(), U));
        Assert.Equal(RunProblem.StartConflict, Refusal(f.Read(), new RunEvent.Requested(U)));
    }

    [Fact]
    public void A_run_of_every_root_takes_no_request_and_records_no_node()
    {
        using var f = new RunFixtures(Join());
        f.Approve();
        var record = f.Read();

        Assert.Null(record.Requested);
        Assert.Equal(4, RunScope.InFlow(record).Count);
        Assert.DoesNotContain("\"node\"", RunJournal.Canonical(record.Receipts.Values.Single().Event));
        Assert.IsType<RunDecision.Existing>(f.Store.Request(f.Permit, f.Op(), U));
        Assert.Equal(0, Requests(f));
        Assert.Equal(RunProblem.StartConflict, Assert.IsType<RunRead.Rejected>(RunReducer.Apply(W, Run, record,
            new RunEntry(record.Schema, record.Sequence + 1, f.Op(), Prompt, At, new RunEvent.Requested(U)))).Reason.Problem);
    }

    [Fact]
    public void A_stopped_node_run_takes_no_request()
    {
        using var f = new RunFixtures(Join());
        f.Approve(node: T);
        f.Complete(f.Reserve(T));
        Assert.IsType<RunDecision.Recorded>(f.Store.Stop(f.Permit, f.Op()));

        Assert.Equal(RunProblem.RunStopped, Refused(f.Store.Request(f.Permit, f.Op(), U)).Problem);
        Assert.Equal(0, Requests(f));
        var record = f.Read();
        Assert.Equal(RunProblem.RunStopped, Assert.IsType<RunRead.Rejected>(RunReducer.Apply(W, Run, record,
            new RunEntry(record.Schema, record.Sequence + 1, f.Op(), Prompt, At, new RunEvent.Requested(U)))).Reason.Problem);
    }

    [Fact]
    public void A_node_run_completes_once_nothing_more_can_start_and_not_before()
    {
        using var f = new RunFixtures(Join());
        f.Approve(node: T);

        Assert.Equal(RunProblem.IncompleteResults, Refused(f.Store.Settle(f.Permit, f.Op(), RunOutcome.Completed)).Problem);
        f.Complete(f.Reserve(T));
        // The join waits for the root nobody ran, and the task after that root is not part of the run.
        Assert.Equal([U, C, D], RunScope.Dormant(f.Read()).OrderBy(task => task.Value.ToString()));
        Assert.IsType<RunDecision.Recorded>(f.Store.Settle(f.Permit, f.Op(), RunOutcome.Completed));
        Assert.Equal(RunPhase.Completed, f.Read().Phase);
    }

    [Fact]
    public void A_node_run_with_a_task_after_its_node_cannot_complete_before_that_task_has_a_result()
    {
        using var f = new RunFixtures(Join());
        f.Approve(node: U);
        f.Complete(f.Reserve(U));

        // D comes after U alone, so it can start, and the run is not done without it.
        Assert.Equal([T, C], RunScope.Dormant(f.Read()).OrderBy(task => task.Value.ToString()));
        Assert.Equal(RunProblem.IncompleteResults, Refused(f.Store.Settle(f.Permit, f.Op(), RunOutcome.Completed)).Problem);
        f.Complete(f.Reserve(D));
        Assert.IsType<RunDecision.Recorded>(f.Store.Settle(f.Permit, f.Op(), RunOutcome.Completed));
    }

    [Fact]
    public async Task An_approval_outside_a_node_run_asks_for_nothing()
    {
        using var f = new RunFixtures(FixtureWorkflow(Task(), new TaskDefinition(G, BuiltInBlueprints.Approval) { Title = "Gate" }));
        f.Approve(node: T);

        var refused = Assert.IsType<GatePreparation.Rejected>(await Materializer.Open(f.Project, f.Store).RequestGate(f.Permit, RunOperations.Gate(Run, G), G));

        Assert.Equal(RunProblem.NotRequested, refused.Reason.Problem);
        Assert.Empty(f.Read().Gates);
    }
}
