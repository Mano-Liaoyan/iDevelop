using System.Collections.Immutable;
using IDevelop.Execution;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Runs;

/// <summary>
/// Results carried from earlier runs in the journal (#90), on hand-built runs of read-only tasks without Git or clients.
/// An approval or a request carries a result only for a task the run cannot start, only while it is still its task's
/// current result, after every result it took, and as the run's own materialization would build it. Run Workflow carries
/// nothing.
/// </summary>
public sealed class CarriedJournalTests
{
    private static readonly RunId Third = new(Guid.Parse("00000000-0000-0000-0000-000000000012"));

    /// <summary><c>T → U → C</c>, and a root <c>D</c>.</summary>
    private static Workflow Chain() => Connect(Connect(FixtureWorkflow(Task(), Task(U), Task(C), Task(D)), T, U), U, C);

    private static void Settle(RunFixtures f, RunId run)
    {
        Assert.IsType<RunDecision.Recorded>(f.Store.Settle(f.PermitFor(run), f.Op(), RunOutcome.Stopped));
        f.ReleaseControl();
    }

    /// <summary>The first run, started from <c>T</c>, runs <c>T</c> and <c>U</c>.</summary>
    private static (ResultRecord T, ResultRecord U) First(RunFixtures f)
    {
        f.Approve(node: T);
        var t = f.Complete(f.Reserve(T));
        var u = f.Complete(f.Reserve(U));
        Settle(f, Run);
        return (t, u);
    }

    private static RunHistory History(RunFixtures f) => RunHistory.Of(f.Store.Records(W), f.Workflow);

    /// <summary>What <paramref name="run"/>, started from <paramref name="node"/>, carries under <paramref name="operation"/>.</summary>
    private static ImmutableArray<IncludedResult> Built(RunFixtures f, RunId run, TaskId node, OperationId operation) => Carrying.Build(null,
        new RunRecord(run, W, new(Base, BaseChoice.Head), Revision.Capture(f.Workflow)) { Schema = 3, Requested = [node] }, History(f), operation,
        preview: false).Carried;

    private static RunDecision Approve(RunFixtures f, RunId run, OperationId operation, TaskId? node, ImmutableArray<IncludedResult> carried) =>
        f.Store.Approve(W, run, operation, Revision.Capture(f.Workflow), new(Base, BaseChoice.Head), node: node, carried: carried);

    private static RunRejection Refused(RunDecision decision) => Assert.IsType<RunDecision.Rejected>(decision).Reason with { Sequence = 0 };

    [Fact]
    public void A_node_s_approval_carries_each_result_it_needs_after_the_ones_it_took_and_Run_Workflow_carries_none()
    {
        using var f = new RunFixtures(Chain());
        var (t, u) = First(f);
        var operation = f.Op();
        var carried = Built(f, OtherRun, C, operation);
        Assert.Equal([T, U], carried.Select(item => item.Result.Task));

        Assert.Equal(new RunRejection(RunProblem.UnsupportedResult), Refused(Approve(f, OtherRun, operation, null, carried)));
        // The order a chain needs: U's result took T's, so T comes first.
        Assert.Equal(new RunRejection(RunProblem.UnknownResult, Task: U), Refused(Approve(f, OtherRun, operation, C, [carried[1], carried[0]])));
        Assert.IsType<RunDecision.Created>(Approve(f, OtherRun, operation, C, carried));

        var record = f.Read(OtherRun);
        Assert.Equal([new ResultOrigin.Carried(Run, t.Id), new ResultOrigin.Carried(Run, u.Id)],
            new[] { T, U }.Select(task => record.CurrentResults[task].Origin));
        Assert.Equal([C], RunScope.InFlow(record));
        Assert.Equal(RunProblem.NotRequested, Refused(f.Store.Reserve(f.Lease(U, OtherRun), f.Op(), record.Revision.Id, new AttemptCause.Initial())).Problem);
        Assert.IsType<RunDecision.Created>(f.Store.Reserve(f.Lease(C, OtherRun), f.Op(), record.Revision.Id, new AttemptCause.Initial()));
    }

    [Fact]
    public void A_carried_result_must_still_be_its_task_s_current_result_and_one_the_run_cannot_start()
    {
        using var f = new RunFixtures(Chain());
        First(f);
        var operation = f.Op();
        var carried = Built(f, Third, C, operation);
        // A task the run may start is never carried.
        Assert.Equal(new RunRejection(RunProblem.StartConflict, Task: U), Refused(Approve(f, Third, operation, U, carried)));
        // A later run runs U again, so the result built from the first run no longer is U's current result.
        var again = f.Op();
        Assert.IsType<RunDecision.Created>(Approve(f, OtherRun, again, U, Built(f, OtherRun, U, again)));
        var rerun = f.Complete(f.Reserve(U, run: OtherRun), run: OtherRun);
        Settle(f, OtherRun);

        // T's newest run is now the second, which carried it, so the first run's result is no longer its source either.
        Assert.Equal(new RunRejection(RunProblem.StaleInput, Task: T), Refused(Approve(f, Third, operation, C, carried)));
        Assert.Equal(new RunRejection(RunProblem.StaleInput, Task: U), Refused(Approve(f, Third, operation, C,
            [Built(f, Third, U, operation).Single(item => item.Result.Task == T), carried[1]])));
        var current = Built(f, Third, C, operation);
        Assert.Equal(new ResultOrigin.Carried(OtherRun, rerun.Id), current.Single(item => item.Result.Task == U).Result.Origin);
        Assert.IsType<RunDecision.Created>(Approve(f, Third, operation, C, current));
    }

    [Fact]
    public void A_carried_result_takes_the_ids_and_inputs_its_carrying_event_derives()
    {
        using var f = new RunFixtures(Chain());
        First(f);
        var operation = f.Op();
        var carried = Built(f, OtherRun, C, operation);

        Assert.Equal(new RunRejection(RunProblem.InputConflict, Task: T), Refused(Approve(f, OtherRun, f.Op(), C, carried)));
        var tampered = carried.SetItem(0, carried[0] with { Inputs = carried[0].Inputs with { Text = "Something else." } });
        Assert.Equal(new RunRejection(RunProblem.InputConflict, Task: T), Refused(Approve(f, OtherRun, operation, C, tampered)));
        var renamed = carried.SetItem(0, carried[0] with
        {
            Result = carried[0].Result with { Artifacts = [new("notes", "results/elsewhere/notes", Revision.Hash("x"), 1)] }
        });
        Assert.Equal(new RunRejection(RunProblem.InputConflict, Task: T), Refused(Approve(f, OtherRun, operation, C, renamed)));
        var coded = carried.SetItem(0, carried[0] with { Result = carried[0].Result with { Code = new CodeOutput.Forwarded(carried[0].Inputs.Id) } });
        Assert.Equal(new RunRejection(RunProblem.InputConflict, Task: T), Refused(Approve(f, OtherRun, operation, C, coded)));
        Assert.Equal([Run], f.Store.Records(W).Select(record => record.Id));
    }

    [Fact]
    public void A_carried_result_must_take_the_current_results_of_the_run()
    {
        using var f = new RunFixtures(Chain());
        First(f);
        var seed = new RunRecord(OtherRun, W, new(Base, BaseChoice.Head), Revision.Capture(f.Workflow)) { Schema = 3, Requested = [C] };
        var operation = f.Op();
        var carried = Carrying.Build(null, seed, History(f), operation, preview: false).Carried;
        var withT = RunReducer.WithCarried(seed, carried[0]);
        Assert.Null(RunReducer.CarriedProblem(withT, carried[1], operation));

        // Once T's carried result is superseded, U's carried result, which took it, no longer fits.
        var newer = carried[0].Result with { Id = new(Id(900)), Supersedes = carried[0].Result.Id };
        var superseded = withT with { Results = withT.Results.Add(newer) };
        Assert.Equal(RunProblem.StaleInput, RunReducer.CarriedProblem(superseded, carried[1], operation));
    }

    [Fact]
    public void A_result_that_took_a_result_of_a_task_the_run_runs_again_is_not_carried_and_neither_is_one_after_it()
    {
        // X ⇢ Y (context), Y → W, and X, W → Z. The first run runs everything; running X again cannot carry Y, whose result
        // took X's, nor W, whose result took Y's, so Z waits for W.
        var x = T;
        var (y, w, z) = (U, C, D);
        var workflow = Connect(Connect(Connect(Connect(FixtureWorkflow(Task(), Task(U), Task(C), Task(D)), x, y, ConnectionKind.Context), y, w), x, z), w, z);
        using var f = new RunFixtures(workflow);
        f.Approve();
        foreach (var task in new[] { x, y, w, z }) f.Complete(f.Reserve(task));
        Settle(f, Run);

        var build = Carrying.Build(null, new RunRecord(OtherRun, W, new(Base, BaseChoice.Head), Revision.Capture(f.Workflow)) { Schema = 3, Requested = [x] },
            History(f), f.Op(), preview: false);

        Assert.Empty(build.Carried);
        Assert.Equal(new CarryRefusal.InputRefused(y), build.Refused[w]);
        Assert.Equal(new CarryRefusal.InputRuns(x), build.Refused[y]);
    }

    [Fact]
    public void A_request_carries_what_the_tasks_it_adds_need_once_and_a_task_with_a_result_is_not_requested()
    {
        using var f = new RunFixtures(Chain());
        var (t, _) = First(f);
        // The second run starts from D, an unrelated root.
        f.Approve(run: OtherRun, node: D);
        var record = f.Read(OtherRun);
        var operation = f.Op();
        var built = Carrying.Build(null, record with { Requested = [D, U] }, History(f), operation, preview: false).Carried;
        Assert.Equal([T], built.Select(item => item.Result.Task));

        Assert.Equal(new RunRejection(RunProblem.MissingDependencyResult, Task: T), Refused(f.Store.Request(f.PermitFor(OtherRun), f.Op(), U)));
        Assert.IsType<RunDecision.Recorded>(f.Store.Request(f.PermitFor(OtherRun), operation, U, built));
        Assert.IsType<RunDecision.Existing>(f.Store.Request(f.PermitFor(OtherRun), operation, U, built));

        record = f.Read(OtherRun);
        Assert.Equal(new ResultOrigin.Carried(Run, t.Id), record.CurrentResults[T].Origin);
        Assert.Equal([D, U], record.Requested!);
        Assert.Equal(1, record.Receipts.Values.Count(entry => entry.Event is RunEvent.Requested));
        Assert.Equal(new RunRejection(RunProblem.StartConflict, Task: T), Refused(f.Store.Request(f.PermitFor(OtherRun), f.Op(), T)));
        Assert.IsType<RunDecision.Created>(f.Store.Reserve(f.Lease(U, OtherRun), f.Op(), record.Revision.Id, new AttemptCause.Initial()));
        // A journal that requests the carried task anyway is refused on replay.
        record = f.Read(OtherRun);
        var forged = new RunEntry(3, record.Sequence + 1, f.Op(), Revision.Hash("forged"), At, new RunEvent.Requested(T));
        Assert.Equal(RunProblem.StartConflict, Assert.IsType<RunRead.Rejected>(RunReducer.Apply(W, OtherRun, record, forged)).Reason.Problem);
    }

}
