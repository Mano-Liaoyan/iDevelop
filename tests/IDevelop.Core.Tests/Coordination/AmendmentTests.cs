using System.Collections.Immutable;
using IDevelop.Execution;
using IDevelop.Nodes;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Coordination.CoordinatorFixture;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Coordination;

/// <summary>A planner that runs in the run proposes, and the person amends the run from its proposal (E3d.2).</summary>
public sealed class AmendmentTests
{
    private static readonly OperationId Accept = new(Guid.Parse("00000000-0000-0000-0000-0000000e0001"));

    /// <summary>A read-only planner whose prompt starts with <c>"Build A."</c>.</summary>
    internal static TaskDefinition Planner(TaskId task)
    {
        var agent = Agent(task, readOnly: true);
        var blueprint = agent.Blueprint;
        return new TaskDefinition(task, new(new("example.planner", 1), "Planner", (WorkSpec.Agent)blueprint.Work with { Proposes = true },
            blueprint.Fields, blueprint.Defaults)) { Title = agent.Title, Execution = agent.Execution }.WithField("brief", $"Build {Name(task)}.")!;
    }

    /// <summary>
    /// A's proposal: a writer X after it whose brief is <c>"Build X again."</c>, of the type that B's writer has, and a
    /// built-in Implement Y, which no task of the run uses and the person leaves out.
    /// </summary>
    internal const string AddsX = """
        X goes after me.

        ```idevelop
        {"status": "proposal",
         "add": [{"id": "new-1", "type": "type-7", "title": "X", "fields": {"brief": "Build X again."}},
                 {"id": "new-2", "type": "type-1", "title": "Y", "fields": {"instructions": "Build Y."}}],
         "connect": [{"from": "planner", "to": "new-1"}, {"from": "new-1", "to": "new-2"}]}
        ```
        """;

    internal static FakeRule Proposes(TaskId task, string proposal) => FakeRule.On()
        .Print(FakeAgents.SessionLine(ClientId.Codex, "session-" + Name(task))).Print(FakeAgents.ReplyLines(ClientId.Codex, proposal));

    internal static CoordinatorFixture Fixture() => new CoordinatorFixture(Graph([Planner(A), Agent(B)]))
        .Answer(A, Proposes(A, AddsX)).Answer(B, Writes(B, "b.txt", "B\n"))
        .Answer("X again", FakeRule.On().Print(FakeAgents.SessionLine(ClientId.Codex, "session-X")).Write("x.txt", "X\n")
            .Print(FakeAgents.ReplyLines(ClientId.Codex, "X ready.\n")));

    /// <summary>The proposal A's turn ended with, read from its run-owned log.</summary>
    internal static (RunAttempt Attempt, AttemptRecord Log, Proposal Proposal) Proposed(CoordinatorFixture f)
    {
        var record = f.Read();
        var attempt = record.Attempts.Values.Single(attempt => attempt.Task == A);
        var log = AttemptEvidence.Read(f.Preparation.Store.AttemptFolder(W, f.Preparation.RunId, A, attempt.Id)).Record!;
        var proposal = Assert.IsType<ProposalRead.Ready>(Proposal.Read(log, key => RunPlanning.Find(record.Revision.Snapshot, key))).Proposal;
        return (attempt, log, proposal);
    }

    /// <summary>Blocks the second preparation, B's, after its reservation and before its preparation is recorded.</summary>
    private static (TaskCompletionSource Reached, TaskCompletionSource Release) HoldSecondPreparation(CoordinatorFixture f) =>
        HoldSecond(f, "journal.prepared.before");

    /// <summary>Blocks the second preparation at its step <paramref name="step"/>; the first is the planner's.</summary>
    internal static (TaskCompletionSource Reached, TaskCompletionSource Release) HoldSecond(CoordinatorFixture f, string step)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparations = 0;
        f.Runs.Probe = point =>
        {
            if (point != step || Interlocked.Increment(ref preparations) != 2) return;
            reached.TrySetResult();
            release.Task.WaitAsync(Bound).GetAwaiter().GetResult();
        };
        return (reached, release);
    }

    [Fact]
    public async Task An_amendment_after_a_reservation_keeps_the_original_preparation_and_runs_the_amended_task()
    {
        await using var f = Fixture();
        await f.Open();
        var original = f.Read().Revision.Id;
        var (reached, release) = HoldSecondPreparation(f);
        await f.Resume();
        await reached.Task.WaitAsync(Bound);
        await f.Until(view => view.Tasks[A].State == TaskState.Done);
        var reservedB = f.Read().Attempts.Values.Single(attempt => attempt.Task == B);
        Assert.DoesNotContain(f.Read().Preparations.Keys, launch => launch.Attempt == reservedB.Id);
        var (planner, log, proposal) = Proposed(f);
        var added = proposal.Nodes.Single(node => node.Name == "new-1").Id;

        var amended = await f.Coordinator.Amend(f.Address, new(original, proposal, [added], f.Read().Revision.Snapshot.Tasks[A].Execution, Accept))
            .WaitAsync(Bound);

        Assert.IsType<RunCommand.Accepted>(amended);
        release.TrySetResult();
        await f.UntilStatus(RunStatus.Completed);
        var record = f.Read();
        Assert.Equal(1, record.Preparations.Keys.Count(launch => launch.Attempt == reservedB.Id));
        Assert.Equal(1, f.Claims(B));
        Assert.Equal(original, record.Attempts[reservedB.Id].Revision);
        Assert.StartsWith("Build B.", f.Prompt(B));
        Assert.Equal("Build B.", record.Revisions[original].Snapshot.Tasks[B].Field("brief"));
        Assert.Equal("Build X again.", record.Revision.Snapshot.Tasks[added].Field("brief"));
        Assert.Equal(record.Revision.Id, record.Attempts.Values.Single(attempt => attempt.Task == added).Revision);
        Assert.Equal(1, f.Launches("X again"));
        Assert.StartsWith("Build X again.", f.Prompt("X again"));
        Assert.Contains("X goes after me.", f.Prompt("X again"));
        var recorded = Assert.Single(record.Receipts.Values.Select(entry => entry.Event).OfType<RunEvent.Amended>());
        Assert.Equal((original, new AmendmentOrigin.Planner(planner.Id, 1), Accept), (recorded.Previous, recorded.Origin, recorded.Confirmation));
        Assert.Equal(new PlanningHandles(planner.Id.Value, [], [.. RunPlanning.Types(record.Revisions[original].Snapshot).Select(type => type.Key)]),
            log.Planning);
        var projection = Assert.IsType<AmendmentProjection.Apply>(AmendmentProjection.Of(record.Revisions[original].Snapshot, record));
        Assert.Equal(record.Revision.Id, Revision.Capture(Edit(record.Revisions[original].Snapshot, projection.Edit)).Id);
        Assert.Contains("Empty tasks you may fill:\n- None.\n", f.Prompt(A));
        Assert.Contains("- type-7: Writer. Fields: brief (Brief, required).\n", f.Prompt(A));
    }

    [Fact]
    public async Task A_planner_prepared_before_a_crash_resumes_its_preparation_with_the_same_handles()
    {
        await using var f = Fixture();
        await f.Crash(A, "runner.prepared");
        var prepared = Assert.Single(f.Read().Preparations.Values);
        await f.Open();

        await f.Resume();
        await f.Until(view => view.Tasks[A].State == TaskState.Done && view.Tasks[B].State == TaskState.Done);

        Assert.Equal(1, f.Launches(A));
        Assert.Equal(prepared, Assert.Single(f.Read().Preparations.Values, preparation => preparation.Launch.Attempt == prepared.Launch.Attempt));
        Assert.Equal(prepared.Prompt, f.Prompt(A));
        var (planner, log, proposal) = Proposed(f);
        Assert.Equal(planner.Id.Value, log.Planning!.Plan);
        Assert.Equal(2, proposal.Nodes.Length);
    }

    [Fact]
    public async Task Another_window_cannot_amend_a_repeat_converges_and_a_stale_revision_is_refused()
    {
        await using var f = Fixture();
        await f.Open();
        var original = f.Read().Revision.Id;
        var (reached, release) = HoldSecondPreparation(f);
        await f.Resume();
        await reached.Task.WaitAsync(Bound);
        await f.Until(view => view.Tasks[A].State == TaskState.Done);
        var (_, _, proposal) = Proposed(f);
        var added = proposal.Nodes.Single(node => node.Name == "new-1").Id;
        var execution = f.Read().Revision.Snapshot.Tasks[A].Execution;
        var (second, other) = await f.SecondWindow();
        try
        {
            var unavailable = await other.Amend(other.Address, new(original, proposal, [added], execution, Accept)).WaitAsync(Bound);
            Assert.Equal(new RunCommand.Unavailable(WorkflowRunCoordinator.ElsewhereMessage), unavailable);
        }
        finally { await second.DisposeAsync(); }
        Assert.DoesNotContain(f.Read().Receipts.Values, entry => entry.Event is RunEvent.Amended);
        var unconfirmed = await f.Coordinator.Amend(f.Address, new(original, proposal, [added], execution, default)).WaitAsync(Bound);
        Assert.Equal(RunProblem.ConfirmationRequired, Assert.IsType<RunCommand.Refused>(unconfirmed).Reason.Problem);

        var first = await f.Coordinator.Amend(f.Address, new(original, proposal, [added], execution, Accept)).WaitAsync(Bound);
        var same = await f.Coordinator.Amend(f.Address, new(original, proposal, [added], execution, Accept)).WaitAsync(Bound);
        var again = await f.Coordinator.Amend(f.Address, new(original, proposal, [added], execution, new(Guid.NewGuid()))).WaitAsync(Bound);
        var unknown = await f.Coordinator.Amend(f.Address, new(new(new string('a', 64)), proposal, [added], execution, new(Guid.NewGuid())))
            .WaitAsync(Bound);
        var stale = await f.Coordinator.Amend(f.Address, new(original, proposal, [], execution, new(Guid.NewGuid()))).WaitAsync(Bound);
        release.TrySetResult();

        Assert.All(new[] { first, same, again }, outcome => Assert.IsType<RunCommand.Accepted>(outcome));
        Assert.Equal(RunProblem.RevisionConflict, Assert.IsType<RunCommand.Refused>(unknown).Reason.Problem);
        Assert.Equal(RunProblem.RevisionConflict, Assert.IsType<RunCommand.Refused>(stale).Reason.Problem);
        Assert.Single(f.Read().Receipts.Values, entry => entry.Event is RunEvent.Amended);
        await f.UntilStatus(RunStatus.Completed);
        Assert.Equal(1, f.Launches("X again"));
    }
}
