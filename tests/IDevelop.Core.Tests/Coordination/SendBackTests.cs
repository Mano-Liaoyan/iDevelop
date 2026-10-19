using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Coordination.CoordinatorFixture;

namespace IDevelop.Core.Tests.Coordination;

/// <summary>After a send back: Ask again on the same inputs, and a Retry's note to the agent with the reason (#126).</summary>
public sealed class SendBackTests
{
    private static readonly TaskId G = new(Guid.Parse("00000000-0000-0000-0000-0000000000e6"));

    private static TaskDefinition Gate() => new(G, BuiltInBlueprints.Approval) { Title = "Gate" };

    private static GateResponse Response(TaskView gate) => new(gate.Gate!.Request.Id, gate.Gate.Request.Inputs);

    private static OperationId Op() => new(Guid.NewGuid());

    private static Task<RunView> Waiting(CoordinatorFixture f, int sequence) =>
        f.Until(view => view.Tasks[G] is { State: TaskState.Waiting, Gate.Request.Sequence: var number } && number == sequence);

    [Fact]
    public async Task Ask_again_opens_a_new_request_on_the_same_inputs_and_keeps_the_sent_back_one()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Gate(), Agent(B)], (A, G), (G, B)));
        f.Answer(A, Writes(A, "a.txt", "A\n")).Answer(B, Writes(B, "b.txt", "B\n"));
        await f.Open();
        await f.Resume();
        var first = (await Waiting(f, 1)).Tasks[G];
        Assert.IsType<GateReply.Recorded>(await f.Coordinator.SendBack(f.Address, Response(first), "Add tests.", Op()).WaitAsync(Bound));
        await f.UntilStatus(RunStatus.NeedsAttention);

        var command = Op();
        var asked = Assert.IsType<GateReply.Recorded>(await f.Coordinator.AskAgain(f.Address, first.Gate!.Request.Id, command).WaitAsync(Bound));
        Assert.Equal((new RunEvent.GateAskedAgain(first.Gate.Request.Id), false), (asked.Receipt.Event, asked.Repeat));

        var renewed = (await Waiting(f, 2)).Tasks[G];
        var record = f.Read();
        Assert.NotEqual(first.Gate.Request.Id, renewed.Gate!.Request.Id);
        Assert.NotEqual(first.Gate.Request.Inputs, renewed.Gate.Request.Inputs);
        Assert.True(RunReducer.Same(record.Inputs[first.Gate.Request.Inputs].Bindings, record.Inputs[renewed.Gate.Request.Inputs].Bindings));
        // The sent-back request stays in the run's history with its reason.
        var kept = record.Gates[first.Gate.Request.Id];
        Assert.Equal(("Add tests.", true), (Assert.IsType<GateDecision.SentBack>(kept.Decision).Reason, kept.AskedAgain));
        Assert.Equal(2, record.Gates.Count);
        Assert.Equal(0, f.Launches(B));

        var repeat = Assert.IsType<GateReply.Recorded>(await f.Coordinator.AskAgain(f.Address, first.Gate.Request.Id, command).WaitAsync(Bound));
        Assert.Equal((true, asked.Receipt.Sequence), (repeat.Repeat, repeat.Receipt.Sequence));
        // A repeat under another operation, as from a second click, returns the recorded entry too.
        Assert.True(Assert.IsType<GateReply.Recorded>(await f.Coordinator.AskAgain(f.Address, first.Gate.Request.Id, Op()).WaitAsync(Bound)).Repeat);
        // Only a sent-back request is asked again.
        Assert.Equal(new GateReply.Stale(renewed.Gate.Request), await f.Coordinator.AskAgain(f.Address, renewed.Gate.Request.Id, Op()).WaitAsync(Bound));

        Assert.IsType<GateReply.Recorded>(await f.Coordinator.Approve(f.Address, Response(renewed), Op()).WaitAsync(Bound));
        await f.UntilStatus(RunStatus.Completed);
        Assert.Equal([1, 1, 2], new[] { f.Launches(A), f.Launches(B), f.TotalLaunches });
    }

    [Fact]
    public async Task A_retry_of_a_task_that_feeds_a_sent_back_approval_offers_the_reason_as_its_note_and_its_prompt_ends_with_the_note()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Agent(B), Gate(), Agent(X, readOnly: true)], (A, B), (B, G)));
        f.Answer(A, Writes(A, "a.txt", "A1\n"), Writes(A, "a.txt", "A2\n"))
            .Answer(B, Writes(B, "b.txt", "B1\n"), Writes(B, "b.txt", "B2\n")).Answer(X, Reports(X));
        await f.Open();
        await f.Resume();
        var first = (await Waiting(f, 1)).Tasks[G];
        Assert.IsType<GateReply.Recorded>(await f.Coordinator.SendBack(f.Address, Response(first), "Handle empty input.", Op()).WaitAsync(Bound));
        var sent = await f.Until(view => view.Tasks[G].State == TaskState.SentBack && view.Tasks[X].State == TaskState.Done);

        // A feeds the approval through B, and X feeds nothing.
        Assert.Equal("Handle empty input.", WorkflowRunCoordinator.SentBackNote(sent.Snapshot!, sent, A));
        Assert.Equal("Handle empty input.", WorkflowRunCoordinator.SentBackNote(sent.Snapshot!, sent, B));
        Assert.Null(WorkflowRunCoordinator.SentBackNote(sent.Snapshot!, sent, X));

        var note = WorkflowRunCoordinator.SentBackNote(sent.Snapshot!, sent, A);
        var reply = Assert.IsType<RetryReply.Reserved>(await f.Coordinator.Retry(f.Address, A, Op(), "  " + note + "\n").WaitAsync(Bound));
        Assert.Equal("Handle empty input.", Assert.IsType<AttemptCause.Retry>(f.Read().Attempts[reply.Attempt].Cause).Note);

        var renewed = (await Waiting(f, 2)).Tasks[G];
        Assert.EndsWith("\n\n" + Materializer.RetryNote("Handle empty input."), f.Prompt(A, 2));
        // The task after it runs again by itself, as a Retry with no note.
        Assert.DoesNotContain("Handle empty input.", f.Prompt(B, 2));
        var decided = await f.Decided();
        Assert.Null(WorkflowRunCoordinator.SentBackNote(decided.Snapshot!, decided, A));
        Assert.Equal("Waiting for approval", renewed.Gate!.Label);
    }

    [Fact]
    public async Task A_retry_without_a_note_records_none()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Gate()], (A, G)));
        f.Answer(A, Writes(A, "a.txt", "A1\n"), Writes(A, "a.txt", "A2\n"));
        await f.Open();
        await f.Resume();
        var first = (await Waiting(f, 1)).Tasks[G];
        Assert.IsType<GateReply.Recorded>(await f.Coordinator.SendBack(f.Address, Response(first), "No.", Op()).WaitAsync(Bound));
        await f.UntilStatus(RunStatus.NeedsAttention);

        var reply = Assert.IsType<RetryReply.Reserved>(await f.Coordinator.Retry(f.Address, A, Op(), " ").WaitAsync(Bound));

        Assert.Null(Assert.IsType<AttemptCause.Retry>(f.Read().Attempts[reply.Attempt].Cause).Note);
        await Waiting(f, 2);
        Assert.DoesNotContain(Materializer.RetryNote(""), f.Prompt(A, 2));
    }

    [Fact]
    public async Task A_second_send_back_asked_again_opens_a_third_request_on_the_same_joined_inputs()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Agent(B), Gate()], (A, G), (B, G)));
        f.Answer(A, Writes(A, "a.txt", "A\n")).Answer(B, Writes(B, "b.txt", "B\n"));
        await f.Open();
        await f.Resume();
        var requests = new List<TaskView> { (await Waiting(f, 1)).Tasks[G] };
        for (var sequence = 2; sequence <= 3; sequence++)
        {
            var shown = requests[^1];
            Assert.IsType<GateReply.Recorded>(await f.Coordinator.SendBack(f.Address, Response(shown), $"No {sequence}.", Op()).WaitAsync(Bound));
            Assert.True((await f.Until(view => view.Tasks[G].State == TaskState.SentBack)).Tasks[G].Gate!.CanAskAgain);
            Assert.IsType<GateReply.Recorded>(await f.Coordinator.AskAgain(f.Address, shown.Gate!.Request.Id, Op()).WaitAsync(Bound));
            requests.Add((await Waiting(f, sequence)).Tasks[G]);
        }

        var record = f.Read();
        Assert.Equal(3, requests.Select(each => each.Gate!.Request.Id).Distinct().Count());
        Assert.All(requests, each => Assert.True(RunReducer.Same(record.Inputs[requests[0].Gate!.Request.Inputs].Bindings,
            record.Inputs[each.Gate!.Request.Inputs].Bindings)));
        Assert.All(requests, each => Assert.IsType<CodeSelection.Joined>(record.Inputs[each.Gate!.Request.Inputs].Code));
        Assert.IsType<GateReply.Recorded>(await f.Coordinator.Approve(f.Address, Response(requests[^1]), Op()).WaitAsync(Bound));
        await f.UntilStatus(RunStatus.Completed);
        Assert.Equal([1, 1], new[] { f.Launches(A), f.Launches(B) });
    }

    [Fact]
    public async Task A_stopped_run_refuses_Ask_again()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Gate()], (A, G)));
        f.Answer(A, Writes(A, "a.txt", "A\n"));
        await f.Open();
        await f.Resume();
        var gate = (await Waiting(f, 1)).Tasks[G];
        Assert.IsType<GateReply.Recorded>(await f.Coordinator.SendBack(f.Address, Response(gate), "No.", Op()).WaitAsync(Bound));
        Assert.IsType<RunCommand.Accepted>(await f.Coordinator.Stop(f.Address, Op()).WaitAsync(Bound));
        var stopped = await f.UntilStatus(RunStatus.Stopped);

        Assert.False(stopped.Tasks[G].Gate!.CanAskAgain);
        Assert.Equal(RunProblem.RunStopped,
            Assert.IsType<GateReply.Refused>(await f.Coordinator.AskAgain(f.Address, gate.Gate!.Request.Id, Op()).WaitAsync(Bound)).Reason.Problem);
        Assert.Single(f.Read().Gates);
    }

    [Fact]
    public async Task A_send_back_whose_inputs_went_out_of_date_offers_no_Ask_again()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Agent(B), Gate()], (A, B), (B, G)));
        f.Answer(A, Writes(A, "a.txt", "A1\n"), Writes(A, "a.txt", "A2\n"))
            .Answer(B, Writes(B, "b.txt", "B1\n"), FakeRule.On().Print(FakeAgents.SessionLine(ClientId.Codex, "session-B2")).Exit(1));
        await f.Open();
        await f.Resume();
        var gate = (await Waiting(f, 1)).Tasks[G];
        Assert.IsType<GateReply.Recorded>(await f.Coordinator.SendBack(f.Address, Response(gate), "No.", Op()).WaitAsync(Bound));
        Assert.True((await f.Until(view => view.Tasks[G].State == TaskState.SentBack)).Tasks[G].Gate!.CanAskAgain);

        // A's Retry puts B's result out of date, and B's rerun fails, so the sent-back request's inputs are superseded.
        Assert.IsType<RetryReply.Reserved>(await f.Coordinator.Retry(f.Address, A, Op()).WaitAsync(Bound));
        var superseded = await f.Until(view => view.Tasks[B].State == TaskState.Failed && f.Launches(B) == 2);

        Assert.False(superseded.Tasks[G].Gate?.CanAskAgain ?? false);
        Assert.IsType<GateReply.Stale>(await f.Coordinator.AskAgain(f.Address, gate.Gate!.Request.Id, Op()).WaitAsync(Bound));
        Assert.Single(f.Read().Gates);
    }
}
