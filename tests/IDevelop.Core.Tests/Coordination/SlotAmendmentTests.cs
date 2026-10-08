using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Coordination.AmendmentTests;
using static IDevelop.Core.Tests.Coordination.CoordinatorFixture;

namespace IDevelop.Core.Tests.Coordination;

/// <summary>
/// A planner in the run fills the empty task after it, whose fields are all optional, so it can start blank. The
/// person's acceptance races that task's start (E3d.2).
/// </summary>
public sealed class SlotAmendmentTests
{
    private static readonly OperationId Accept = new(Guid.Parse("00000000-0000-0000-0000-0000000e0101"));

    /// <summary>A writer whose prompt is <c>"Build C."</c> and then its optional note.</summary>
    private static readonly Blueprint Note = new(new("example.note", 1), "Note",
        new WorkSpec.Agent(AgentAccess.Edit, false, PromptTemplate.Parse("Build C.\n\n{{note}}")),
        [new("note", "Note", FieldShape.Text, false, "")], new(null, ConversationMode.Autonomous));

    private const string FillsC = """
        C needs a note.

        ```idevelop
        {"status": "proposal", "fill": [{"slot": "slot-1", "fields": {"note": "Fill it."}}]}
        ```
        """;

    /// <summary>A task Y that C would wait for, though the proposal leaves C itself unfilled.</summary>
    private const string FeedsC = """
        Y goes before C.

        ```idevelop
        {"status": "proposal", "add": [{"id": "new-1", "type": "type-1", "title": "Y", "fields": {"instructions": "Build Y."}}],
         "connect": [{"from": "new-1", "to": "slot-1"}]}
        ```
        """;

    /// <summary>Planner A, whose turn ends with <paramref name="proposal"/>, and the empty task C after it.</summary>
    private static CoordinatorFixture Fixture(string proposal = FillsC) =>
        new CoordinatorFixture(Graph([Planner(A), new TaskDefinition(C, Note) { Title = "C", Execution = Agent(A).Execution }], (A, C)))
            .Answer(A, Proposes(A, proposal)).Answer(C, Writes(C, "c.txt", "C\n"));

    [Fact]
    public async Task A_fill_accepted_once_the_slot_planned_its_start_is_left_out_and_the_slot_starts_as_planned()
    {
        await using var f = Fixture();
        await f.Open();
        var original = f.Read().Revision.Id;
        var (reached, release) = HoldSecond(f, "journal.reserve.before");
        await f.Resume();
        await reached.Task.WaitAsync(Bound);
        Assert.Contains(f.Read().Plans.Values, plan => plan is MaterializationPlan.Preparation { Task: var task } && task == C);
        var (_, _, proposal) = Proposed(f);

        var amended = await f.Coordinator.Amend(f.Address, new(original, proposal, [C], null, Accept)).WaitAsync(Bound);
        release.TrySetResult();

        Assert.IsType<RunCommand.Accepted>(amended);
        await f.UntilStatus(RunStatus.Completed);
        var record = f.Read();
        Assert.Equal(1, f.Launches(C));
        // The prompt quotes A's proposal among its inputs, but the template renders no note.
        Assert.DoesNotContain("Build C.\n\nFill it.", f.Prompt(C));
        Assert.Equal(original, record.Attempts.Values.Single(attempt => attempt.Task == C).Revision);
        Assert.Equal("", record.Revision.Snapshot.Tasks[C].Field("note"));
    }

    [Fact]
    public async Task A_fill_accepted_while_the_slot_plans_its_start_reaches_it_and_a_later_repeat_converges()
    {
        await using var f = Fixture();
        await f.Open();
        var original = f.Read().Revision.Id;
        var (reached, release) = HoldSecond(f, "journal.plan.before");
        await f.Resume();
        await reached.Task.WaitAsync(Bound);
        var (_, _, proposal) = Proposed(f);

        var amended = await f.Coordinator.Amend(f.Address, new(original, proposal, [C], null, Accept)).WaitAsync(Bound);
        release.TrySetResult();

        Assert.IsType<RunCommand.Accepted>(amended);
        await f.UntilStatus(RunStatus.Completed);
        var record = f.Read();
        Assert.Equal(1, f.Launches(C));
        Assert.StartsWith("Build C.\n\nFill it.", f.Prompt(C));
        Assert.Equal(record.Revision.Id, record.Attempts.Values.Single(attempt => attempt.Task == C).Revision);
        Assert.NotEqual(original, record.Revision.Id);

        var repeated = await f.Coordinator.Amend(f.Address, new(original, proposal, [C], null, new(Guid.NewGuid()))).WaitAsync(Bound);

        Assert.IsType<RunCommand.Accepted>(repeated);
        Assert.Single(f.Read().Receipts.Values, entry => entry.Event is RunEvent.Amended);
    }

    [Fact]
    public async Task An_input_for_a_slot_that_planned_its_start_is_refused_and_the_slot_starts_as_planned()
    {
        await using var f = Fixture(FeedsC);
        await f.Open();
        var original = f.Read().Revision.Id;
        var (reached, release) = HoldSecond(f, "journal.reserve.before");
        await f.Resume();
        await reached.Task.WaitAsync(Bound);
        var (_, _, proposal) = Proposed(f);

        var refused = await f.Coordinator.Amend(f.Address, new(original, proposal, [.. proposal.Items], Agent(A).Execution, Accept)).WaitAsync(Bound);
        release.TrySetResult();

        Assert.Equal(RunProblem.StartedTaskChanged, Assert.IsType<RunCommand.Refused>(refused).Reason.Problem);
        await f.UntilStatus(RunStatus.Completed);
        Assert.Equal(original, f.Read().Revision.Id);
        Assert.Equal(1, f.Launches(C));
    }
}
