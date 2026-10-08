using System.Collections.Immutable;
using IDevelop.Execution;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Runs;

/// <summary>Approval node requests and answers in the journal, on hand-built runs without Git or clients (E3e).</summary>
public sealed class GateJournalTests
{
    private static readonly TaskId G = new(Guid.Parse("00000000-0000-0000-0000-000000000006"));
    private const string Forwarded = "## Plan (dependency)\n\nT ready.\n";

    private static TaskDefinition Gate() => new(G, BuiltInBlueprints.Approval) { Title = "Gate" };

    /// <summary><c>T → G</c>, with <c>G → U</c> when <paramref name="consumer"/>.</summary>
    private static Workflow Graph(bool consumer = false)
    {
        var workflow = Connect(FixtureWorkflow(Task(), Gate(), Task(U)), T, G);
        return consumer ? Connect(workflow, G, U) : workflow;
    }

    private static async Task<GatePreparation> Prepare(RunFixtures f, OperationId? root = null) =>
        await Materializer.Open(f.Project, f.Store).RequestGate(f.Permit, root ?? RunOperations.Gate(Run, G), G);

    private static async Task<GateRequest> Request(RunFixtures f) => Assert.IsType<GatePreparation.Requested>(await Prepare(f)).Request;

    private static RunDecision Answer(RunFixtures f, GateRequest request, GateAnswer answer, InputId? inputs = null, OperationId? operation = null) =>
        f.Store.AnswerGate(f.Permit, operation ?? f.Op(), request.Id, inputs ?? request.Inputs, answer);

    private static string Json(RunEvent e) => RunJournal.Canonical(e);

    private static RunEntry Entry(RunFixtures f, RunRecord record, RunEvent e) => new(record.Schema, record.Sequence + 1, f.Op(), Prompt, At, e);

    private static RunProblem Refusal(RunFixtures f, RunRecord record, RunEvent e) =>
        Assert.IsType<RunRead.Rejected>(RunReducer.Apply(W, Run, record, Entry(f, record, e))).Reason.Problem;

    private static void Accepts(RunFixtures f, RunRecord record, RunEvent e) =>
        Assert.IsType<RunRead.Loaded>(RunReducer.Apply(W, Run, record, Entry(f, record, e)));

    [Fact]
    public async Task A_request_fixes_its_composed_inputs_and_creates_no_attempt()
    {
        using var f = new RunFixtures(Graph());
        f.Approve();
        var early = Assert.IsType<GatePreparation.Rejected>(await Prepare(f));
        Assert.Equal((RunProblem.MissingDependencyResult, (TaskId?)T), (early.Reason.Problem, early.Reason.Task));
        var t = f.Complete(f.Reserve(T), report: "T ready.\n");

        var request = await Request(f);
        var record = f.Read();
        Assert.Equal((G, record.Revision.Id, 1), (request.Task, request.Revision, request.Sequence));
        Assert.Single(record.Attempts);
        Assert.Equal(request, Assert.Single(record.Gates).Value.Request);
        var inputs = record.Inputs[request.Inputs];
        Assert.Equal([new InputBinding.Provided(new(T, G), ConnectionKind.Dependency, t.Id)], inputs.Bindings.ToArray());
        Assert.Equal(new CodeSelection.Root(Base), inputs.Code);
        Assert.Contains("T ready.\n", inputs.Text);

        var sequence = record.Sequence;
        Assert.Equal(request, (await Request(f)));
        Assert.Equal(sequence, f.Read().Sequence);
        Assert.Equal(RunProblem.StartConflict, Assert.IsType<GatePreparation.Rejected>(await Prepare(f, f.Op())).Reason.Problem);
    }

    [Fact]
    public async Task Approval_records_one_human_result_and_a_repeat_returns_the_original_entry()
    {
        using var f = new RunFixtures(Graph());
        f.Approve();
        f.Complete(f.Reserve(T), report: "T ready.\n");
        f.Complete(f.Reserve(U), report: "U ready.\n");
        var request = await Request(f);
        var operation = f.Op();

        var created = Assert.IsType<RunDecision.Created>(Answer(f, request, new GateAnswer.Approve(), operation: operation));
        var result = Assert.IsType<RunEvent.ResultAccepted>(created.Event).Result;
        Assert.Equal(new ResultOrigin.Human(request.Id), result.Origin);
        Assert.Equal((request.Result, request.Inputs, Forwarded), (result.Id, result.Inputs, result.Report));
        Assert.Null(result.Code);
        Assert.Empty(result.Artifacts);
        var sequence = f.Read().Sequence;

        Assert.Equal(Json(created.Event), Json(Assert.IsType<RunDecision.Existing>(Answer(f, request, new GateAnswer.Approve())).Event));
        Assert.Equal(Json(created.Event), Json(Assert.IsType<RunDecision.Existing>(Answer(f, request, new GateAnswer.Approve(), operation: operation)).Event));
        Assert.Equal(RunProblem.StaleInput, Problem(Answer(f, request, new GateAnswer.SendBack("Too late."))));
        var record = f.Read();
        Assert.Equal(sequence, record.Sequence);
        Assert.Single(record.Results, item => item.Origin is ResultOrigin.Human);
        Assert.Equal(new GateDecision.Approved(request.Result, operation), record.Gates[request.Id].Decision);
        Assert.IsType<RunDecision.Recorded>(f.Store.Settle(f.Permit, f.Op(), RunOutcome.Completed));
    }

    [Fact]
    public async Task Send_back_records_its_reason_and_holds_the_request()
    {
        using var f = new RunFixtures(Graph());
        f.Approve();
        f.Complete(f.Reserve(T), report: "T ready.\n");
        f.Complete(f.Reserve(U), report: "U ready.\n");
        var request = await Request(f);
        Assert.Equal(RunProblem.ConfirmationRequired, Problem(Answer(f, request, new GateAnswer.SendBack(" "))));

        var sent = Assert.IsType<RunDecision.Recorded>(Answer(f, request, new GateAnswer.SendBack("Add tests.")));
        Assert.Equal(new RunEvent.GateSentBack(request.Id, request.Inputs, "Add tests."), sent.Event);
        Assert.Equal(Json(sent.Event), Json(Assert.IsType<RunDecision.Existing>(Answer(f, request, new GateAnswer.SendBack("Add tests."))).Event));
        Assert.Equal(RunProblem.StaleInput, Problem(Answer(f, request, new GateAnswer.SendBack("Other."))));
        Assert.Equal(RunProblem.StaleInput, Problem(Answer(f, request, new GateAnswer.Approve())));
        Assert.Equal("Add tests.", Assert.IsType<GateDecision.SentBack>(f.Read().Gates[request.Id].Decision).Reason);

        Assert.Equal(request, await Request(f));
        Assert.Equal(RunProblem.StartConflict, Assert.IsType<GatePreparation.Rejected>(await Prepare(f, f.Op())).Reason.Problem);
        Assert.DoesNotContain(f.Read().Results, result => result.Task == G);
        Assert.Equal(RunProblem.IncompleteResults, Problem(f.Store.Settle(f.Permit, f.Op(), RunOutcome.Completed)));
    }

    [Fact]
    public async Task A_superseded_request_answers_stale_and_the_gate_takes_a_new_request()
    {
        using var f = new RunFixtures(Graph());
        f.Approve();
        var first = f.Reserve(T);
        var old = f.Complete(first, report: "T ready.\n");
        var stale = await Request(f);
        var current = f.Complete(f.Reserve(T, new AttemptCause.Retry(first.Attempt.Id, f.Op())), report: "T again.\n", supersedes: old.Id);

        var refusal = Assert.IsType<RunDecision.Rejected>(Answer(f, stale, new GateAnswer.Approve())).Reason;
        Assert.Equal((RunProblem.StaleInput, (TaskId?)G), (refusal.Problem, refusal.Task));
        Assert.Equal(RunProblem.StaleInput, Problem(Answer(f, stale, new GateAnswer.SendBack("Old."))));
        Assert.Null(RunReducer.LiveGate(f.Read(), G));

        var renewed = await Request(f);
        Assert.Equal(2, renewed.Sequence);
        Assert.NotEqual(stale.Id, renewed.Id);
        Assert.Equal(current.Id, Assert.IsType<InputBinding.Provided>(Assert.Single(f.Read().Inputs[renewed.Inputs].Bindings)).Result);
        var approved = Assert.IsType<RunEvent.ResultAccepted>(Assert.IsType<RunDecision.Created>(Answer(f, renewed, new GateAnswer.Approve())).Event);
        Assert.Equal("## Plan (dependency)\n\nT again.\n", approved.Result.Report);
        Assert.Equal(RunProblem.StaleInput, Problem(Answer(f, stale, new GateAnswer.Approve())));
    }

    [Fact]
    public async Task A_stale_approval_takes_a_new_request_that_supersedes_it()
    {
        using var f = new RunFixtures(Graph());
        f.Approve();
        var first = f.Reserve(T);
        var old = f.Complete(first, report: "T ready.\n");
        var request = await Request(f);
        var approval = Assert.IsType<RunDecision.Created>(Answer(f, request, new GateAnswer.Approve()));
        Assert.Equal(RunProblem.StartConflict, Assert.IsType<GatePreparation.Rejected>(await Prepare(f, f.Op())).Reason.Problem);
        f.Complete(f.Reserve(T, new AttemptCause.Retry(first.Attempt.Id, f.Op())), report: "T again.\n", supersedes: old.Id);

        var renewed = await Request(f);
        Assert.Equal(2, renewed.Sequence);
        var second = Assert.IsType<RunEvent.ResultAccepted>(Assert.IsType<RunDecision.Created>(Answer(f, renewed, new GateAnswer.Approve())).Event).Result;
        Assert.Equal(request.Result, second.Supersedes);
        Assert.Equal(Json(approval.Event), Json(Assert.IsType<RunDecision.Existing>(Answer(f, request, new GateAnswer.Approve())).Event));
    }

    [Fact]
    public async Task Answers_name_their_request_and_its_inputs()
    {
        using var f = new RunFixtures(Graph());
        f.Approve();
        f.Complete(f.Reserve(T), report: "T ready.\n");
        var request = await Request(f);
        Assert.Equal(RunProblem.UnknownInput, Problem(f.Store.AnswerGate(f.Permit, f.Op(), new(Guid.NewGuid()), request.Inputs, new GateAnswer.Approve())));
        Assert.Equal(RunProblem.StaleInput, Problem(Answer(f, request, new GateAnswer.Approve(), inputs: new(Guid.NewGuid()))));
        Assert.IsType<RunDecision.Created>(Answer(f, request, new GateAnswer.Approve()));
        Assert.Equal(RunProblem.StaleInput, Problem(Answer(f, request, new GateAnswer.Approve(), inputs: new(Guid.NewGuid()))));
    }

    [Fact]
    public async Task A_stopped_run_takes_no_request_and_no_answer()
    {
        using var f = new RunFixtures(Graph());
        f.Approve();
        f.Complete(f.Reserve(T), report: "T ready.\n");
        var request = await Request(f);
        Assert.IsType<RunDecision.Recorded>(f.Store.Stop(f.Permit, f.Op()));
        Assert.Equal(RunProblem.RunStopped, Problem(Answer(f, request, new GateAnswer.Approve())));
        Assert.Equal(RunProblem.RunStopped, Problem(Answer(f, request, new GateAnswer.SendBack("Stopped."))));
        Assert.Equal(RunProblem.RunStopped, Assert.IsType<GatePreparation.Rejected>(await Prepare(f, f.Op())).Reason.Problem);
    }

    [Fact]
    public async Task The_reducer_takes_only_a_composed_request_for_an_approval_node()
    {
        using var f = new RunFixtures(Graph());
        f.Approve();
        var first = f.Reserve(T);
        var t = f.Complete(first, report: "T ready.\n");
        var record = f.Read();
        var id = new InputId(Guid.NewGuid());
        RunEvent.GateRequested Requested(RunRecord on, TaskId task, int sequence = 1, RevisionId? revision = null, ResultId? result = null,
            Func<InputRecord, InputRecord>? change = null, ImmutableArray<InputBinding>? bindings = null)
        {
            var bound = bindings ?? RunStore.Inputs(on, task, on.Revision.Id, id).Inputs!.Bindings;
            var inputs = InputMaterial.Build(on, id, task, on.Revision.Id, bound, InputMaterial.Sources(on, bound), null);
            inputs = change?.Invoke(inputs) ?? inputs;
            return new(new(new(Guid.NewGuid()), task, revision ?? on.Revision.Id, id, sequence, result ?? new(Guid.NewGuid())), inputs);
        }

        Accepts(f, record, Requested(record, G));
        Assert.Equal(RunProblem.UnsupportedWork, Refusal(f, record, Requested(record, U)));
        var missing = Requested(record, G);
        Assert.Equal(RunProblem.InvalidData, Refusal(f, record, missing with { Request = missing.Request with { Id = new(Guid.Empty) } }));
        Assert.Equal(RunProblem.IdentityMismatch, Refusal(f, record, missing with { Request = missing.Request with { Task = new(Guid.NewGuid()) } }));
        Assert.Equal(RunProblem.InputConflict, Refusal(f, record, missing with { Request = missing.Request with { Inputs = new(Guid.NewGuid()) } }));
        Assert.Equal(RunProblem.InputConflict, Refusal(f, record, Requested(record, G, change: inputs => inputs with { Text = "Other." })));
        Assert.Equal(RunProblem.InvalidData, Refusal(f, record, Requested(record, G, sequence: 2)));
        Assert.Equal(RunProblem.RevisionConflict, Refusal(f, record, Requested(record, G, revision: new(new string('0', 64)))));
        Assert.Equal(RunProblem.StartConflict, Refusal(f, record, Requested(record, G, result: t.Id)));
        Assert.Equal(RunProblem.RunStopped, Refusal(f, record with { Phase = RunPhase.StopRequested }, Requested(record, G)));
        Assert.Equal(RunProblem.UnsupportedSchema, Refusal(f, record with { Schema = 2 }, Requested(record, G)));

        var artifact = new ArtifactRecord("payload", RunStorage.ArtifactPath(t.Id, "payload"), Revision.Hash("C"), 1);
        var other = new ArtifactRecord("PAYLOAD", RunStorage.ArtifactPath(t.Id, "PAYLOAD"), Revision.Hash("X"), 1);
        RunRecord Carrying(params ArtifactRecord[] artifacts) => record with { Results = [t with { Artifacts = [.. artifacts] }] };
        Accepts(f, Carrying(artifact), Requested(Carrying(artifact), G));
        Assert.Equal(RunProblem.InputConflict, Refusal(f, Carrying(artifact, other), Requested(Carrying(artifact, other), G)));

        var request = await Request(f);
        Assert.Equal(RunProblem.StartConflict, Refusal(f, f.Read(), Requested(f.Read(), G, sequence: 2)));
        Assert.IsType<RunDecision.Created>(Answer(f, request, new GateAnswer.Approve()));
        Assert.Equal(RunProblem.StartConflict, Refusal(f, f.Read(), Requested(f.Read(), G, sequence: 2)));

        f.Complete(f.Reserve(T, new AttemptCause.Retry(first.Attempt.Id, f.Op())), report: "T again.\n", supersedes: t.Id);
        var superseded = f.Read();
        Accepts(f, superseded, Requested(superseded, G, sequence: 2));
        var renewed = Requested(superseded, G, sequence: 2);
        Assert.Equal(RunProblem.StartConflict, Refusal(f, superseded, renewed with { Request = renewed.Request with { Id = request.Id } }));
        Assert.Equal(RunProblem.StartConflict, Refusal(f, superseded, renewed with { Request = renewed.Request with { Result = request.Result } }));
        Assert.Equal(RunProblem.StartConflict, Refusal(f, superseded, renewed with
        {
            Request = renewed.Request with { Inputs = request.Inputs }, Inputs = renewed.Inputs with { Id = request.Inputs },
        }));
        Assert.Equal(RunProblem.StaleInput, Refusal(f, superseded, Requested(superseded, G, sequence: 2,
            bindings: [new InputBinding.Provided(new(T, G), ConnectionKind.Dependency, t.Id)])));
    }

    [Fact]
    public async Task The_reducer_takes_only_an_approval_that_forwards_the_current_request()
    {
        using var f = new RunFixtures(Graph());
        f.Approve();
        f.Complete(f.Reserve(T), report: "T ready.\n");
        var request = await Request(f);
        var record = f.Read();
        var inputs = record.Inputs[request.Inputs];
        var valid = new ResultRecord(request.Result, G, request.Revision, request.Inputs, new ResultOrigin.Human(request.Id), Forwarded, null);
        RunEvent Accepted(ResultRecord result) => new RunEvent.ResultAccepted(result, inputs);

        Accepts(f, record, Accepted(valid));
        Assert.Equal(RunProblem.OutcomeMismatch, Refusal(f, record, Accepted(valid with { Report = "Approved." })));
        Assert.Equal(RunProblem.InputConflict, Refusal(f, record, Accepted(valid with
        {
            Artifacts = [new("payload", RunStorage.ArtifactPath(request.Result, "payload"), Revision.Hash("C"), 1)],
        })));
        Assert.Equal(RunProblem.InputConflict, Refusal(f, record, Accepted(valid with { Code = new CodeOutput.Forwarded(request.Inputs) })));
        Assert.Equal(RunProblem.InputConflict, Refusal(f, record, Accepted(valid with { Id = new(Guid.NewGuid()) })));
        Assert.Equal(RunProblem.UnknownInput, Refusal(f, record, Accepted(valid with { Origin = new ResultOrigin.Human(new(Guid.NewGuid())) })));
        Assert.Equal(RunProblem.InvalidData, Refusal(f, record, Accepted(valid with { Origin = new ResultOrigin.Human(new(Guid.Empty)) })));
        Assert.Equal(RunProblem.RunStopped, Refusal(f, record with { Phase = RunPhase.StopRequested }, Accepted(valid)));
        Assert.Equal(RunProblem.UnsupportedSchema, Refusal(f, record with { Schema = 2 }, Accepted(valid)));
        var onAgent = f.Reserve(U);
        Assert.Equal(RunProblem.UnsupportedResult, Refusal(f, f.Read(), new RunEvent.ResultAccepted(valid with
        {
            Task = U, Inputs = onAgent.Inputs.Id,
        }, onAgent.Inputs)));

        Assert.IsType<RunDecision.Recorded>(Answer(f, request, new GateAnswer.SendBack("Add tests.")));
        Assert.Equal(RunProblem.StaleInput, Refusal(f, f.Read(), Accepted(valid)));
    }

    [Fact]
    public async Task The_reducer_takes_no_approval_of_superseded_inputs()
    {
        using var f = new RunFixtures(Graph());
        f.Approve();
        var first = f.Reserve(T);
        var old = f.Complete(first, report: "T ready.\n");
        var request = await Request(f);
        var inputs = f.Read().Inputs[request.Inputs];
        var valid = new RunEvent.ResultAccepted(new(request.Result, G, request.Revision, request.Inputs, new ResultOrigin.Human(request.Id), Forwarded, null), inputs);
        Accepts(f, f.Read(), valid);
        f.Complete(f.Reserve(T, new AttemptCause.Retry(first.Attempt.Id, f.Op())), report: "T again.\n", supersedes: old.Id);
        Assert.Equal(RunProblem.StaleInput, Refusal(f, f.Read(), valid));
    }

    [Fact]
    public async Task The_reducer_takes_only_a_send_back_of_the_current_request_with_a_reason()
    {
        using var f = new RunFixtures(Graph());
        f.Approve();
        f.Complete(f.Reserve(T), report: "T ready.\n");
        var request = await Request(f);
        var record = f.Read();
        RunEvent Sent(string reason = "Add tests.", InputId? inputs = null, GateId? gate = null) =>
            new RunEvent.GateSentBack(gate ?? request.Id, inputs ?? request.Inputs, reason);

        Accepts(f, record, Sent());
        Assert.Equal(RunProblem.ConfirmationRequired, Refusal(f, record, Sent(reason: "")));
        Assert.Equal(RunProblem.InvalidData, Refusal(f, record, Sent(gate: new(Guid.Empty))));
        Assert.Equal(RunProblem.InputConflict, Refusal(f, record, Sent(inputs: new(Guid.NewGuid()))));
        Assert.Equal(RunProblem.UnknownInput, Refusal(f, record, Sent(gate: new(Guid.NewGuid()))));
        Assert.Equal(RunProblem.RunStopped, Refusal(f, record with { Phase = RunPhase.StopRequested }, Sent()));
        Assert.Equal(RunProblem.UnsupportedSchema, Refusal(f, record with { Schema = 2 }, Sent()));

        Assert.IsType<RunDecision.Created>(Answer(f, request, new GateAnswer.Approve()));
        Assert.Equal(RunProblem.StaleInput, Refusal(f, f.Read(), Sent()));

        using var g = new RunFixtures(Graph());
        g.Approve();
        var retried = g.Reserve(T);
        var replaced = g.Complete(retried, report: "T ready.\n");
        var open = await Request(g);
        g.Complete(g.Reserve(T, new AttemptCause.Retry(retried.Attempt.Id, g.Op())), report: "T again.\n", supersedes: replaced.Id);
        Assert.Equal(RunProblem.StaleInput, Refusal(g, g.Read(), new RunEvent.GateSentBack(open.Id, open.Inputs, "Add tests.")));
    }

    [Fact]
    public async Task An_amendment_keeps_a_requested_gate_and_its_incoming_connections()
    {
        using var f = new RunFixtures(Graph());
        f.Approve();
        var changed = Edit(f.Workflow, new WorkflowEdit.SetField(G, "checklist", "Check the tests."));
        var revision = Revision.Capture(changed);
        Assert.IsType<RunDecision.Recorded>(f.Store.Amend(f.Permit, f.Op(), f.Read().Revision.Id, revision, new AmendmentOrigin.Person(), f.Op()));
        f.Complete(f.Reserve(T), report: "T ready.\n");
        await Request(f);
        var again = Revision.Capture(Edit(changed, new WorkflowEdit.SetField(G, "checklist", "Check everything.")));
        Assert.Equal(RunProblem.StartedTaskChanged, Problem(f.Store.Amend(f.Permit, f.Op(), f.Read().Revision.Id, again, new AmendmentOrigin.Person(), f.Op())));
    }

    [Fact]
    public void Forwarding_keeps_artifact_identities_once_and_names_a_collision()
    {
        using var f = new RunFixtures(Connect(Connect(FixtureWorkflow(Task(), Task(U), Gate()), T, G), U, G));
        f.Approve();
        var t = f.Complete(f.Reserve(T), report: "T ready.\n");
        var u = f.Complete(f.Reserve(U), report: "U ready.\n");
        var target = new ResultId(Guid.NewGuid());
        var payload = new ArtifactRecord("payload", RunStorage.ArtifactPath(t.Id, "payload"), Revision.Hash("C"), 1);
        RunRecord With(ArtifactRecord[] onT, ArtifactRecord[] onU) =>
            f.Read() with { Results = [t with { Artifacts = [.. onT] }, u with { Artifacts = [.. onU] }] };
        InputRecord Inputs(RunRecord record) => InputMaterial.Build(record, new(Guid.NewGuid()), G, record.Revision.Id,
            RunStore.Inputs(record, G, record.Revision.Id, default).Inputs!.Bindings, [], null);

        var same = With([payload], [payload with { StoredPath = RunStorage.ArtifactPath(u.Id, "payload") }]);
        var forwarded = GateForwarding.Artifacts(same, Inputs(same), target);
        Assert.Null(forwarded.Collision);
        Assert.Equal([payload with { StoredPath = RunStorage.ArtifactPath(target, "payload") }], forwarded.Artifacts.ToArray());

        var clash = With([payload], [new("Payload", RunStorage.ArtifactPath(u.Id, "Payload"), Revision.Hash("U"), 1)]);
        Assert.Equal(("Payload", 0), (GateForwarding.Artifacts(clash, Inputs(clash), target).Collision, GateForwarding.Artifacts(clash, Inputs(clash), target).Artifacts.Length));
        Assert.Equal("## Plan (dependency)\n\nT ready.\n\n\n## Plan (dependency)\n\nU ready.\n", GateForwarding.Report(same, Inputs(same)));
    }
}
