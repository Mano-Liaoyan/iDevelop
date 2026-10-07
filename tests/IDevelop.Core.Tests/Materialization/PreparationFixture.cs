using IDevelop.Core.Tests.Git;
using IDevelop.Core.Tests.Runs;
using IDevelop.Execution;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Materialization;

internal sealed class PreparationFixture : IDisposable
{
    public readonly GitFixture Git = new();
    public readonly RunStore Store;
    public readonly RunId RunId;
    private int _id = 100;
    private int _op = 1000;
    private CoordinatorPermit? _permit;
    private readonly Dictionary<TaskId, TaskLease> _leases = [];
    public readonly CommitId A;
    public PreparationFixture(Workflow workflow, CommitId? runBase = null, RunId? run = null, Func<GitFixture, CommitId>? configureBase = null)
    {
        A = Git.Diamond();
        RunId = run ?? Run;
        Store = RunStore.Open(Git.Folder, new Clock(), () => Id(++_id));
        Assert.IsType<RunDecision.Created>(Store.Approve(W, RunId, Op(), Revision.Capture(workflow), new(configureBase?.Invoke(Git) ?? runBase ?? A, BaseChoice.Head)));
    }

    public Materializer Materializer(IJoinComposer? joins = null, Action<string>? probe = null, string? project = null, IExecutionBoundary? boundary = null) =>
        Execution.Materializer.Open(project ?? Git.Folder, Store, joins, boundary ?? new QuiescentBoundary(), new Clock(), Git.Environment, probe);
    public CoordinatorPermit Permit => _permit ??= Assert.IsType<ControlTake.Owned>(RunStore.Open(Git.Folder).TakeControl(W, RunId)).Permit;

    public TaskLease Lease(TaskId task)
    {
        if (_leases.TryGetValue(task, out var lease)) return lease;
        lease = Assert.IsType<LeaseTake.Taken>(Permit.TakeTask(task)).Lease;
        _leases.Add(task, lease);
        return lease;
    }

    public void Release(TaskId task)
    {
        if (_leases.Remove(task, out var lease)) lease.Dispose();
    }

    public void ReleaseControl()
    {
        foreach (var lease in _leases.Values) lease.Dispose();
        _leases.Clear();
        _permit?.Dispose();
        _permit = null;
    }

    public OperationId Op() => new(Id(++_op));
    public RunRecord Read() => Assert.IsType<RunRead.Loaded>(Store.Read(W, RunId)).Record;
    public ValueTask<Preparation> Prepare(TaskId task, OperationId? operation = null, AttemptCause? cause = null, string? prompt = null) =>
        Materializer().Prepare(Lease(task), operation ?? Op(), cause ?? new AttemptCause.Initial(), prompt);

    public static TaskDefinition Writer(TaskId task, string template = "{{brief}}")
    {
        var blueprint = new Blueprint(new($"example.writer.{task}", 1), "Writer",
            new WorkSpec.Agent(AgentAccess.Edit, false, PromptTemplate.Parse(template)),
            [new("brief", "Brief", FieldShape.Text, true, "Inspect")], new(null, ConversationMode.Autonomous));
        return new(task, blueprint) { Execution = Task().Execution, Title = task == T ? "B" : task == C ? "C" : "U" };
    }

    public async Task<ResultRecord> Publish(TaskId task, CommitId commit, string report = "B ready.\n", bool artifact = false, string artifactName = "payload")
    {
        var ready = Assert.IsType<Preparation.Ready>(await Prepare(task));
        Assert.Equal(0, Git.Run(ready.Checkout, "merge", "--ff-only", "--no-edit", commit.Hex).ExitCode);
        if (artifact)
        {
            var outbox = Path.Combine(ready.Checkout, ready.Execution.OutboxPath);
            File.WriteAllBytes(Path.Combine(outbox, "payload.bin"), [67, 0, 127]);
            File.WriteAllText(Path.Combine(outbox, "manifest.json"),
                "{\"schema\":1,\"artifacts\":[{\"name\":\"" + artifactName + "\",\"path\":\"payload.bin\"}]}");
        }
        Close(ready, report);
        return Assert.IsType<Publication.Accepted>(Materializer().Publish(Lease(task), Op(), ready.Execution.Launch.Attempt)).Result;
    }

    public void Close(Preparation.Ready ready, string report = "B ready.\n", TerminalAttemptOutcome outcome = TerminalAttemptOutcome.Succeeded)
    {
        var execution = ready.Execution;
        var attempt = Read().Attempts[execution.Launch.Attempt];
        var input = Read().Inputs[execution.Inputs];
        Assert.IsType<RunDecision.Granted>(Store.Claim(Lease(attempt.Task), Op(), execution.Launch, input, execution.PromptHash));
        var definition = Read().Revision.Snapshot.Tasks[attempt.Task];
        var folder = Store.AttemptFolder(W, RunId, attempt.Task, attempt.Id);
        using (var log = AttemptLog.Create(Path.GetDirectoryName(Path.GetDirectoryName(folder))!, new AttemptEvent.Requested(
            At, attempt.Id, attempt.Task, definition.Title, definition.Execution!, execution.Prompt, "codex", [])
        {
            RunBinding = new(W, RunId, attempt.Revision, input.Id), Conversation = definition.Conversation,
            ReadOnly = definition.Blueprint.Work is WorkSpec.Agent { Access: AgentAccess.ReadOnly } or WorkSpec.Review,
            Fix = Read().ReviewOf(attempt.Id),
        }))
        {
            log.Append(new AttemptEvent.Agent(At, new AgentEvent.SessionStarted("fixture")));
            log.Append(new AttemptEvent.Agent(At, outcome == TerminalAttemptOutcome.Failed ? new AgentEvent.Failed("Failed.") : new AgentEvent.Succeeded(report)));
            log.Append(new AttemptEvent.Exited(At, outcome == TerminalAttemptOutcome.Failed ? 1 : 0, ""));
        }
        Assert.IsType<RunDecision.Recorded>(Store.CloseAttempt(W, RunId, Op(), attempt.Id, outcome, Checkpoint(folder)));
    }

    public void Dispose()
    {
        ReleaseControl();
        Git.Dispose();
    }
    public sealed class QuiescentBoundary : IExecutionBoundary
    { public WriterState Inspect(AttemptId attempt) => new WriterState.Quiescent(attempt, "controlled fixture"); }
    public sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => At; }
}
