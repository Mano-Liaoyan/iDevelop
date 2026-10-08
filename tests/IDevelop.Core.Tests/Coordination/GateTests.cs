using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Coordination.CoordinatorFixture;
using RunFixtures = IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Coordination;

/// <summary>Approval nodes under the coordinator: requests, answers, and the results they hand on (E3e).</summary>
public sealed class GateTests
{
    private static readonly TaskId G = new(Guid.Parse("00000000-0000-0000-0000-0000000000e5"));
    private static readonly byte[] Payload = [67, 0, 127];

    private static TaskDefinition Gate() => new(G, BuiltInBlueprints.Approval) { Title = "Gate" };

    private static GateResponse Response(TaskView gate) => new(gate.Gate!.Request.Id, gate.Gate.Request.Inputs);

    private static OperationId Op() => new(Guid.NewGuid());

    private static Task<RunView> Waiting(CoordinatorFixture f, int sequence = 1) =>
        f.Until(view => view.Tasks[G] is { State: TaskState.Waiting, Gate.Request.Sequence: var number } && number == sequence);

    private static int HumanResults(CoordinatorFixture f) => f.Read().Results.Count(result => result.Origin is ResultOrigin.Human);

    private static FakeRule Copies(TaskId task, params string[] files) => files.Aggregate(FakeRule.On()
        .Print(FakeAgents.SessionLine(ClientId.Codex, "session-" + Name(task))), (rule, file) => rule.Copy(file, file + "." + Name(task)))
        .Print(FakeAgents.ReplyLines(ClientId.Codex, $"{Name(task)} ready.\n"));

    [Fact]
    public async Task A_duplicate_approval_records_one_human_result_and_launches_no_client()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Gate(), Agent(B), Agent(X, readOnly: true)], (A, G), (G, B)));
        f.Answer(A, Writes(A, "a.txt", "A\n")).Answer(B, Writes(B, "b.txt", "B\n")).Answer(X, Reports(X));
        await f.Open();
        await f.Resume();
        var waiting = await f.Until(view => view.Tasks[G].State == TaskState.Waiting && view.Tasks[X].State == TaskState.Done);
        Assert.Equal(("Waiting", "Waiting for approval", 1), (waiting.Label, waiting.Tasks[G].Gate!.Label, waiting.Tasks[G].Gate!.Request.Sequence));
        Assert.Equal((TaskState.Pending, 0, 0), (waiting.Tasks[B].State, waiting.Slots, f.Launches(B)));

        var command = Op();
        var replies = await Task.WhenAll(f.Coordinator.Approve(f.Address, Response(waiting.Tasks[G]), command),
            f.Coordinator.Approve(f.Address, Response(waiting.Tasks[G]), Op()), f.Coordinator.Approve(f.Address, Response(waiting.Tasks[G]), command));
        var recorded = replies.Select(reply => Assert.IsType<GateReply.Recorded>(reply)).ToArray();
        Assert.Single(recorded.Select(reply => reply.Receipt.Sequence).Distinct());
        Assert.Equal([false, true, true], recorded.Select(reply => reply.Repeat));
        Assert.Equal(command, recorded[0].Receipt.Operation);

        var done = await f.UntilStatus(RunStatus.Completed);
        Assert.Equal("Approved", done.Tasks[G].Gate!.Label);
        Assert.Equal(1, HumanResults(f));
        Assert.Single(f.Read().Receipts.Values, entry => entry.Event is RunEvent.ResultAccepted { Result.Origin: ResultOrigin.Human });
        Assert.Equal([1, 1, 1, 3], new[] { f.Launches(A), f.Launches(B), f.Launches(X), f.TotalLaunches });
        var gate = f.Result(G);
        Assert.Equal(new CodeOutput.Forwarded(gate.Inputs), gate.Code);
        Assert.Contains("## A (dependency)\n\nA ready.\n", f.Prompt(B));
        Assert.Equal("A\n", f.ResultFile(B, "a.txt"));
    }

    [Fact]
    public async Task A_superseded_request_answers_stale_and_the_gate_waits_for_approval_again()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(X, readOnly: true), Gate()], (X, G)));
        f.Answer(X, Reports(X), FakeRule.On().Print(FakeAgents.SessionLine(ClientId.Codex, "session-X2"))
            .Print(FakeAgents.ReplyLines(ClientId.Codex, "X again.\n")));
        await f.Open();
        await f.Resume();
        var first = (await Waiting(f)).Tasks[G];

        // Paused after the restart, the coordinator decides nothing while X runs once more and its report supersedes the first.
        await f.Reopen();
        var permit = f.Coordinator.Permit!;
        var retry = new AttemptCause.Retry(RunProjection.LatestAttempts(f.Read())[X], Op());
        var start = Assert.IsType<TurnStart.Started>(await f.Runs.StartTurn(permit,
            new TurnIntent.First(RunOperations.First(f.Preparation.RunId, X, retry), X, retry)).WaitAsync(Bound));
        var settled = Assert.IsType<TurnSettlement.Settled>(await start.Turn.Settlement.WaitAsync(Bound)).Turn;
        var attempt = settled.Address.Launch.Attempt;
        Assert.IsType<RunDecision.Recorded>(f.Preparation.Store.CloseAttempt(permit, Op(), attempt, TerminalAttemptOutcome.Succeeded, settled.Log));
        var closed = f.Read();
        Assert.IsType<RunDecision.Created>(f.Preparation.Store.AcceptReport(permit, Op(), attempt, closed.Claims[new(attempt, 1)].Inputs.Id,
            "X again.\n", closed.CurrentResults[X].Id));
        Assert.IsType<Release.Released>(settled.Release());

        Assert.Equal(new GateReply.Stale(null), await f.Coordinator.Approve(f.Address, Response(first), Op()).WaitAsync(Bound));
        await f.Resume();
        var renewed = (await Waiting(f, sequence: 2)).Tasks[G];
        Assert.Equal("Waiting for approval", renewed.Gate!.Label);
        Assert.NotEqual(first.Gate!.Request.Id, renewed.Gate.Request.Id);
        Assert.Equal(new GateReply.Stale(renewed.Gate.Request), await f.Coordinator.Approve(f.Address, Response(first), Op()).WaitAsync(Bound));
        Assert.Equal(0, HumanResults(f));

        Assert.IsType<GateReply.Recorded>(await f.Coordinator.Approve(f.Address, Response(renewed), Op()).WaitAsync(Bound));
        await f.UntilStatus(RunStatus.Completed);
        Assert.Equal(1, HumanResults(f));
        Assert.Equal("## X (dependency)\n\nX again.\n", f.Result(G).Report);
        Assert.Equal([2, 2], new[] { f.Launches(X), f.TotalLaunches });
    }

    [Fact]
    public async Task Send_back_records_its_reason_and_holds_the_dependents()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Gate(), Agent(B)], (A, G), (G, B)));
        f.Answer(A, Writes(A, "a.txt", "A\n")).Answer(B, Writes(B, "b.txt", "B\n"));
        await f.Open();
        await f.Resume();
        var gate = (await Waiting(f)).Tasks[G];
        var sent = Assert.IsType<GateReply.Recorded>(await f.Coordinator.SendBack(f.Address, Response(gate), "Add tests.", Op()).WaitAsync(Bound));
        Assert.Equal(new RunEvent.GateSentBack(gate.Gate!.Request.Id, gate.Gate.Request.Inputs, "Add tests."), sent.Receipt.Event);

        var stuck = await f.UntilStatus(RunStatus.NeedsAttention);
        Assert.Equal(TaskState.SentBack, stuck.Tasks[G].State);
        Assert.Equal(("Sent back", "Add tests."), (stuck.Tasks[G].Gate!.Label, stuck.Tasks[G].Gate!.Reason));
        Assert.Equal((TaskState.Pending, 0), (stuck.Tasks[B].State, f.Launches(B)));
        Assert.Equal("Needs attention", stuck.Label);
        var repeat = Assert.IsType<GateReply.Recorded>(await f.Coordinator.SendBack(f.Address, Response(gate), "Add tests.", Op()));
        Assert.Equal((true, sent.Receipt.Sequence), (repeat.Repeat, repeat.Receipt.Sequence));
        Assert.Equal(new GateReply.Stale(gate.Gate.Request), await f.Coordinator.Approve(f.Address, Response(gate), Op()));
        Assert.Equal(new GateReply.Stale(gate.Gate.Request), await f.Coordinator.SendBack(f.Address, Response(gate), "Other.", Op()));
        await f.Coordinator.Resume(f.Address);
        Assert.Equal(RunStatus.NeedsAttention, (await f.Decided()).Status);
        Assert.Equal([1, 0, 1], new[] { f.Launches(A), f.Launches(B), f.TotalLaunches });
        Assert.Single(f.Read().Gates);
    }

    [Fact]
    public async Task An_approval_hands_on_a_named_join_with_the_reports_and_artifacts_it_saw()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Agent(B), Agent(C), Gate(), Agent(D), Agent(X, readOnly: true)],
            (A, B), (A, C), (B, G), (C, G), (G, D)));
        f.Answer(A, Writes(A, "a.txt", "A\n")).Answer(B, Writes(B, "b.txt", "B\n").Outbox("payload", Payload)).Answer(C, Writes(C, "c.txt", "C\n"))
            .Answer(X, Reports(X)).Answer(D, Copies(D, "plan.txt", "a.txt", "b.txt", "c.txt"));
        await f.Open();
        await f.Resume();
        var gate = (await Waiting(f)).Tasks[G];
        var record = f.Read();
        var inputs = record.Inputs[gate.Gate!.Request.Inputs];
        var join = Assert.IsType<CodeSelection.Joined>(inputs.Code).Join;
        Assert.Equal(RunLayout.JoinBranch(record.RunKey!, record.TaskKeys[G]), join.Ref);
        Assert.Equal([B, C], join.Sources.Select(source => source.Task));
        Assert.Equal(0, f.Launches(D));
        // The request's join must be the one its plan recorded and published.
        var requested = record.Receipts.Values.Select(entry => entry.Event).OfType<RunEvent.GateRequested>().Single();
        var before = record with { Gates = record.Gates.Clear(), Inputs = record.Inputs.Remove(inputs.Id) };
        RunRead Apply(RunEvent e) => RunReducer.Apply(RunFixtures.W, f.Preparation.RunId, before, new(3, record.Sequence + 1, Op(), RunFixtures.Prompt, RunFixtures.At, e));
        Assert.IsType<RunRead.Loaded>(Apply(requested));
        var forged = requested with { Inputs = inputs with { Code = new CodeSelection.Joined(join with { Commit = join.Sources[0].Commit }) } };
        Assert.Equal(RunProblem.InputConflict, Assert.IsType<RunRead.Rejected>(Apply(forged)).Reason.Problem);

        Assert.IsType<GateReply.Recorded>(await f.Coordinator.Approve(f.Address, Response(gate), Op()).WaitAsync(Bound));
        await f.UntilStatus(RunStatus.Completed);
        var approved = f.Result(G);
        var artifact = Assert.Single(approved.Artifacts);
        Assert.Equal(("payload", Revision.Hash(Payload), 3L), (artifact.Name, artifact.Content, artifact.ByteLength));
        Assert.Equal(RunStorage.ArtifactPath(approved.Id, "payload"), artifact.StoredPath);
        Assert.Equal(["approved\n", "A\n", "B\n", "C\n"], new[] { "plan", "a", "b", "c" }.Select(name =>
            File.ReadAllText(Path.Combine(f.Checkout(D), $"{name}.txt.D"))));
        Assert.Equal(join.Commit, Assert.IsType<CodeSelection.Single>(f.Read().Inputs[f.Result(D).Inputs].Code).Source.Commit);
        Assert.Contains("## B (dependency)\n\nB ready.\n", f.Prompt(D));
        Assert.Contains("## C (dependency)\n\nC ready.\n", f.Prompt(D));
        var delivered = Assert.Single(f.Read().Inputs[f.Result(D).Inputs].Files, file => file.RelativePath.EndsWith("/artifacts/payload", StringComparison.Ordinal));
        Assert.Equal(Payload, File.ReadAllBytes(Path.Combine(f.Checkout(D), delivered.RelativePath)));
        Assert.Equal(1, f.Launches(D));
    }

    [Fact]
    public async Task Conflicting_inputs_block_the_gate_while_unrelated_work_continues()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Agent(B), Agent(C), Gate(), Agent(D), Agent(X, readOnly: true)],
            (A, B), (A, C), (B, G), (C, G), (G, D)));
        f.Answer(A, Writes(A, "a.txt", "A\n")).Answer(B, Writes(B, "settings.txt", "B\n")).Answer(C, Writes(C, "settings.txt", "C\n"))
            .Answer(X, Reports(X)).Answer(D, Reports(D));
        await f.Open();
        await f.Resume();
        var stuck = await f.Until(view => view.Status == RunStatus.NeedsAttention && view.Tasks[X].State == TaskState.Done);
        Assert.Equal(TaskState.Blocked, stuck.Tasks[G].State);
        Assert.Equal(MaterializationProblem.FanInConflict, stuck.Tasks[G].Block!.Problem);
        Assert.Equal(["settings.txt"], stuck.Tasks[G].Block!.Conflict!.Paths.ToArray());
        Assert.Empty(f.Read().Gates);
        Assert.Equal([0, 1], new[] { f.Launches(D), f.Launches(X) });
    }

    [Fact]
    public async Task Different_artifacts_with_one_name_block_the_gate()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(B), Agent(C), Gate()], (B, G), (C, G)));
        f.Answer(B, Writes(B, "b.txt", "B\n").Outbox("payload", Payload)).Answer(C, Writes(C, "c.txt", "C\n").Outbox("payload", [88]));
        await f.Open();
        await f.Resume();
        var stuck = await f.UntilStatus(RunStatus.NeedsAttention);
        Assert.Equal(MaterializationProblem.ArtifactCollision, stuck.Tasks[G].Block!.Problem);
        Assert.Contains("payload", stuck.Tasks[G].Block!.Detail);
        Assert.Empty(f.Read().Gates);
    }

    [Fact]
    public async Task A_second_window_cannot_answer_and_answers_name_their_run()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Gate()], (A, G)));
        f.Answer(A, Writes(A, "a.txt", "A\n"));
        await f.Open();
        await f.Resume();
        var gate = (await Waiting(f)).Tasks[G];
        var sequence = f.Read().Sequence;
        var (runs, second) = await f.SecondWindow();
        await using (runs)
        {
            Assert.Equal(TaskState.Waiting, second.View.Tasks[G].State);
            Assert.Equal(new GateReply.Unavailable(WorkflowRunCoordinator.ElsewhereMessage), await second.Approve(second.Address, Response(gate), Op()));
            Assert.Equal(new GateReply.Unavailable(WorkflowRunCoordinator.ElsewhereMessage), await second.SendBack(second.Address, Response(gate), "No.", Op()));
        }
        var elsewhere = f.Address with { Run = new(Guid.NewGuid()) };
        Assert.Equal(RunProblem.IdentityMismatch, Assert.IsType<GateReply.Refused>(await f.Coordinator.Approve(elsewhere, Response(gate), Op())).Reason.Problem);
        Assert.Equal(sequence, f.Read().Sequence);
        Assert.Equal(0, HumanResults(f));
    }

    [Fact]
    public async Task A_reopened_run_restores_its_waiting_gate_and_an_approval_starts_nothing_before_resume()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Gate(), Agent(B)], (A, G), (G, B)));
        f.Answer(A, Writes(A, "a.txt", "A\n")).Answer(B, Writes(B, "b.txt", "B\n"));
        await f.Open();
        await f.Resume();
        var before = (await Waiting(f)).Tasks[G];
        await f.Reopen();
        var restored = await f.Decided();
        Assert.Equal((RunStatus.Paused, TaskState.Waiting), (restored.Status, restored.Tasks[G].State));
        Assert.Equal(before.Gate, restored.Tasks[G].Gate);

        Assert.IsType<GateReply.Recorded>(await f.Coordinator.Approve(f.Address, Response(restored.Tasks[G]), Op()).WaitAsync(Bound));
        var approved = await f.Until(view => view.Tasks[G].State == TaskState.Done);
        Assert.Equal((RunStatus.Paused, TaskState.Ready, 0), (approved.Status, approved.Tasks[B].State, f.Launches(B)));
        await f.Resume();
        await f.UntilStatus(RunStatus.Completed);
        Assert.Equal([1, 1, 1], new[] { f.Launches(A), f.Launches(B), f.Read().Gates.Count });
    }

    [Fact]
    public async Task Stop_closes_a_waiting_gate_and_refuses_its_answers()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Gate(), Agent(B)], (A, G), (G, B)));
        f.Answer(A, Writes(A, "a.txt", "A\n")).Answer(B, Writes(B, "b.txt", "B\n"));
        await f.Open();
        await f.Resume();
        var gate = (await Waiting(f)).Tasks[G];
        Assert.IsType<RunCommand.Accepted>(await f.Coordinator.Stop(f.Address, Op()).WaitAsync(Bound));
        var stopped = await f.UntilStatus(RunStatus.Stopped);
        Assert.Equal((TaskState.Failed, "Closed"), (stopped.Tasks[G].State, stopped.Tasks[G].Gate!.Label));
        Assert.Equal(RunProblem.RunStopped, Assert.IsType<GateReply.Refused>(await f.Coordinator.Approve(f.Address, Response(gate), Op())).Reason.Problem);
        Assert.Equal(RunProblem.RunStopped, Assert.IsType<GateReply.Refused>(await f.Coordinator.SendBack(f.Address, Response(gate), "No.", Op())).Reason.Problem);
        Assert.Equal((0, 0), (f.Launches(B), HumanResults(f)));
        Assert.Equal(RunPhase.Stopped, f.Read().Phase);
    }

    [Fact]
    public async Task A_stop_that_wins_over_a_request_leaves_no_request_and_no_hold()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Gate()], (A, G)));
        f.Answer(A, Writes(A, "a.txt", "A\n"));
        await f.Open();
        using (var held = new Turns.TurnFixture.ProbeBarrier("journal.gate-request.before"))
        {
            f.Runs.Probe = held.Probe;
            await f.Resume();
            await held.Reached.Task.WaitAsync(Bound);
            Assert.IsType<RunCommand.Accepted>(await f.Coordinator.Stop(f.Address, Op()).WaitAsync(Bound));
        }
        var stopped = await f.UntilStatus(RunStatus.Stopped);
        Assert.Empty(f.Read().Gates);
        Assert.Equal(TaskState.Ready, stopped.Tasks[G].State);
        Assert.Equal(1, f.Launches(A));
    }

    [Theory]
    [InlineData("journal.gate-request.before")]
    [InlineData("journal.gate-request.after")]
    public async Task A_request_that_fails_on_the_way_is_recorded_once(string point)
    {
        await using var f = new CoordinatorFixture(Graph([Agent(B), Gate()], (B, G)));
        f.Answer(B, Writes(B, "b.txt", "B\n").Outbox("payload", Payload));
        await f.Open();
        var failed = 0;
        f.Runs.Probe = step =>
        {
            if (step == point && Interlocked.Exchange(ref failed, 1) == 0) throw new InvalidOperationException("The window failed on the way.");
        };
        await f.Resume();
        var gate = (await Waiting(f)).Tasks[G];
        Assert.Equal(1, failed);
        Assert.Single(f.Read().Gates);
        Assert.IsType<GateReply.Recorded>(await f.Coordinator.Approve(f.Address, Response(gate), Op()).WaitAsync(Bound));
        await f.UntilStatus(RunStatus.Completed);
        Assert.Equal(Revision.Hash(Payload), Assert.Single(f.Result(G).Artifacts).Content);
    }

    [Fact]
    public async Task Missing_context_from_a_waiting_gate_never_holds_a_task_back()
    {
        var workflow = RunFixtures.Connect(Graph([Agent(A), Agent(B), Gate()], (A, B)), G, B, ConnectionKind.Context);
        await using var f = new CoordinatorFixture(workflow);
        f.Answer(A, Writes(A, "a.txt", "A\n")).Answer(B, Writes(B, "b.txt", "B\n"));
        await f.Open();
        await f.Resume();
        var waiting = await f.Until(view => view.Tasks[B].State == TaskState.Done && view.Tasks[G].State == TaskState.Waiting);
        Assert.Equal(RunStatus.Waiting, waiting.Status);
        Assert.Contains(f.Read().Inputs[f.Result(B).Inputs].Bindings, binding => binding is InputBinding.MissingContext missing && missing.Edge == new ConnectionKey(G, B));
    }
}
