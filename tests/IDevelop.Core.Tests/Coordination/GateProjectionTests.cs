using IDevelop.Core.Tests.Runs;
using IDevelop.Execution;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Coordination;

/// <summary>Approval nodes in the run projection, on hand-built journals (E3e).</summary>
public sealed class GateProjectionTests
{
    private static readonly TaskId G = new(Guid.Parse("00000000-0000-0000-0000-000000000006"));
    private static readonly Dictionary<TaskId, LiveStage> NoLive = [];

    /// <summary><c>T → G → U</c>.</summary>
    private static Workflow Graph() => Connect(Connect(FixtureWorkflow(Task(), new TaskDefinition(G, BuiltInBlueprints.Approval) { Title = "Gate" }, Task(U)), T, G), G, U);

    private static RunView View(RunFixtures f, IReadOnlyDictionary<TaskId, TaskHold>? holds = null) => RunProjection.Of(new(f.Project, W, Run), f.Read(),
        attempt => AttemptEvidence.Read(f.Store.AttemptFolder(W, Run, attempt.Task, attempt.Id)).Record, NoLive, holds ?? new Dictionary<TaskId, TaskHold>(), true, true);

    private static async Task<GateRequest> Request(RunFixtures f) => Assert.IsType<GatePreparation.Requested>(
        await Materializer.Open(f.Project, f.Store).RequestGate(f.Permit, RunOperations.Gate(Run, G), G)).Request;

    private static RunDecision Answer(RunFixtures f, GateRequest request, GateAnswer answer) =>
        f.Store.AnswerGate(f.Permit, f.Op(), request.Id, request.Inputs, answer);

    [Fact]
    public async Task A_gate_is_ready_once_its_inputs_hand_on_waits_once_requested_and_hands_on_once_approved()
    {
        using var f = new RunFixtures(Graph());
        f.Approve();
        var start = View(f);
        Assert.Equal(TaskState.Pending, start.Tasks[G].State);
        Assert.Equal([T], start.Tasks[G].HeldBy.ToArray());

        f.Complete(f.Reserve(T), report: "T ready.\n");
        var ready = View(f);
        Assert.Equal(TaskState.Ready, ready.Tasks[G].State);
        Assert.Null(ready.Tasks[G].Gate);

        var request = await Request(f);
        var waiting = View(f);
        Assert.Equal(TaskState.Waiting, waiting.Tasks[G].State);
        Assert.Equal((request, "Waiting for approval"), (waiting.Tasks[G].Gate!.Request, waiting.Tasks[G].Gate!.Label));
        Assert.Equal(TaskState.Pending, waiting.Tasks[U].State);
        Assert.Equal((RunStatus.Waiting, 0), (waiting.Status, waiting.Slots));

        Assert.IsType<RunDecision.Created>(Answer(f, request, new GateAnswer.Approve()));
        var approved = View(f);
        Assert.Equal((TaskState.Done, request.Result), (approved.Tasks[G].State, approved.Tasks[G].Result));
        Assert.Equal("Approved", approved.Tasks[G].Gate!.Label);
        Assert.Equal(TaskState.Ready, approved.Tasks[U].State);
    }

    [Fact]
    public async Task A_sent_back_gate_needs_attention_and_holds_its_dependents()
    {
        using var f = new RunFixtures(Graph());
        f.Approve();
        f.Complete(f.Reserve(T), report: "T ready.\n");
        Assert.IsType<RunDecision.Recorded>(Answer(f, await Request(f), new GateAnswer.SendBack("Add tests.")));
        var sent = View(f);
        Assert.Equal(TaskState.SentBack, sent.Tasks[G].State);
        Assert.Equal(("Sent back", "Add tests."), (sent.Tasks[G].Gate!.Label, sent.Tasks[G].Gate!.Reason));
        Assert.Equal(TaskState.Pending, sent.Tasks[U].State);
        Assert.Equal([G], sent.Tasks[U].HeldBy.ToArray());
        Assert.Equal(RunStatus.NeedsAttention, sent.Status);
    }

    [Fact]
    public async Task Superseded_inputs_make_the_gate_ready_for_a_new_request()
    {
        using var f = new RunFixtures(Graph());
        f.Approve();
        var first = f.Reserve(T);
        var old = f.Complete(first, report: "T ready.\n");
        await Request(f);
        var retry = f.Complete(f.Reserve(T, new AttemptCause.Retry(first.Attempt.Id, f.Op())), report: "T again.\n", supersedes: old.Id);
        Assert.Equal(TaskState.Ready, View(f).Tasks[G].State);

        var renewed = await Request(f);
        Assert.IsType<RunDecision.Created>(Answer(f, renewed, new GateAnswer.Approve()));
        Assert.Equal(TaskState.Done, View(f).Tasks[G].State);
        f.Complete(f.Reserve(T, new AttemptCause.Retry(RunProjection.LatestAttempts(f.Read())[T], f.Op())), report: "T third.\n", supersedes: retry.Id);
        var stale = View(f);
        Assert.Equal(TaskState.Ready, stale.Tasks[G].State);
        Assert.Equal(TaskState.Pending, stale.Tasks[U].State);
    }

    [Fact]
    public async Task A_stop_closes_an_open_request()
    {
        using var f = new RunFixtures(Graph());
        f.Approve();
        f.Complete(f.Reserve(T), report: "T ready.\n");
        await Request(f);
        Assert.IsType<RunDecision.Recorded>(f.Store.Stop(f.Permit, f.Op()));
        var stopping = View(f);
        Assert.Equal(TaskState.Failed, stopping.Tasks[G].State);
        Assert.Equal("Closed", stopping.Tasks[G].Gate!.Label);
        Assert.Equal(RunStatus.Stopping, stopping.Status);
    }

    [Fact]
    public async Task A_held_or_blocked_request_shows_on_the_gate()
    {
        using var f = new RunFixtures(Graph());
        f.Approve();
        f.Complete(f.Reserve(T), report: "T ready.\n");
        var refused = View(f, new Dictionary<TaskId, TaskHold> { [G] = new TaskHold.Refused(new(RunProblem.InputConflict), null, Transient: false) });
        Assert.Equal((TaskState.Refused, RunProblem.InputConflict), (refused.Tasks[G].State, refused.Tasks[G].Refusal!.Problem));
        Assert.Equal(RunStatus.NeedsAttention, refused.Status);

        var operation = f.Op();
        var block = new MaterializationBlock(operation, G, null, MaterializationProblem.FanInConflict, null, [], "conflict") { Scope = new BlockScope.Operation() };
        Assert.Equal(TaskState.Blocked, View(f, new Dictionary<TaskId, TaskHold> { [G] = new TaskHold.Blocked(block) }).Tasks[G].State);
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, operation, new RunEvent.Blocked(block)));
        var blocked = View(f);
        Assert.Equal((TaskState.Blocked, "conflict"), (blocked.Tasks[G].State, blocked.Tasks[G].Block!.Detail));
    }
}
